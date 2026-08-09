using NSubstitute;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Servers;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The requirement this whole phase turns on: <strong>a grant flipped after a session was opened must be
/// honoured on that session's very next command.</strong>
/// </summary>
/// <remarks>
/// <para>
/// Caching alone cannot deliver this. <c>IWriteModeResolver.Resolve</c> used to be called once per connect
/// and the answer frozen inside the returned guard, and sessions in this codebase are memoized for the life
/// of the process and never evicted on success — so a better cache would only ever change what a NEW
/// connection saw, while an already-open session kept a stale grant indefinitely. The fix is per-command
/// re-resolution, and it had to be applied on BOTH capture paths, which fail independently:
/// </para>
/// <list type="number">
/// <item>the exec path — <c>WriteGuardedTransport.ConnectAsync</c> into <c>WriteGuardedExecutionTarget</c>;</item>
/// <item>the RCON path — <c>ServyxRconChannels.BuildAsync</c> into <c>WriteGuardedRconSession</c>.</item>
/// </list>
/// <para>
/// Every test below therefore opens its session BEFORE the grant changes and then asserts on a SUBSEQUENT
/// call against that same, already-open session. A test that opened a fresh session after the flip would
/// pass against the old, broken code and prove nothing.
/// </para>
/// </remarks>
public class WriteGrantRevocationTests : IDisposable
{
    private const string ContainerId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ContainerName = "palworld-server";

    private static readonly SecretUrn PasswordUrn = SecretUrn.Create("server", ContainerName, "rcon", "password");

    private readonly WriteGrantTestDatabase _database = new();
    private readonly ProvisioningGate _gate = new(enabled: true);
    private readonly WriteGrantCache _cache;
    private readonly IServerRepository _servers;
    private readonly IWriteGrantService _grants;

    public WriteGrantRevocationTests()
    {
        _cache = new WriteGrantCache(_gate, _database.Factory);

        // The composed shape, not the raw one: AddServyxCore registers IServerRepository as the durable
        // repository wrapped in GrantInvalidatingServerRepository, so this is the only IServerRepository
        // anything in the process can resolve. Composing it here is what lets the removal tests below assert
        // on production behaviour rather than on a hand-written Invalidate() the test supplied for itself.
        _servers = new GrantInvalidatingServerRepository(_database.Repository, _cache);
        _grants = new WriteGrantService(_gate, _servers, _cache, new RecordingLogger());
    }

    public void Dispose() => _database.Dispose();

    private static CommandSpec Mutating() => new("rm", ["-rf", "/palworld/Saved"]);

    private static TargetDescriptor DockerTarget() => new(
        "docker",
        "npipe://./pipe/docker_engine",
        null,
        null,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["containerId"] = ContainerId,
            ["containerName"] = ContainerName,
        });

    private ITransport GuardedTransport()
    {
        var inner = Substitute.For<ITransport>();
        inner.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));

        return new WriteGuardedTransport(
            inner,
            new DbBackedWriteModeResolver(_gate, _cache, new GrantedWriteModeResolver([])));
    }

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
    ]);

    private ServyxRconChannels Channels()
    {
        var client = new SourceRconClient();
        var secrets = new RecordingSecretStore();
        var catalog = Palworld();

        return new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(ContainerName, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            catalog,
            client,
            secrets,
            WritableServers.Live(_cache),
            chainFactory: channel => new RconReachabilityChain(
            [
                new AlwaysAvailableRconReachability(
                    endpoint => new RconSession(client, endpoint, catalog, secrets, channel.PasswordUrn)),
            ]));
    }

    // ── Exec path ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exec_path_a_revoked_grant_is_refused_on_the_next_command_of_an_already_open_session()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        // The session is opened FIRST, while the grant still stands. This is the whole point: everything
        // after this line is asserted against a session the process already holds and will never re-open.
        await using var session = await GuardedTransport().ConnectAsync(DockerTarget());
        await session.ExecuteAsync(Mutating());

        await _grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var act = async () => await session.ExecuteAsync(Mutating());

        await act.Should().ThrowAsync<WritesDisabledException>(
            because: "a revocation that only applies to future connections is not a revocation — sessions " +
                "here are memoized for the life of the process and never evicted on success");
    }

    [Fact]
    public async Task Exec_path_a_new_grant_takes_effect_on_the_next_command_of_an_already_open_session()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.ReadOnly);

        await using var session = await GuardedTransport().ConnectAsync(DockerTarget());

        var refusedFirst = async () => await session.ExecuteAsync(Mutating());
        await refusedFirst.Should().ThrowAsync<WritesDisabledException>();

        await _grants.SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        var permittedNow = async () => await session.ExecuteAsync(Mutating());
        await permittedNow.Should().NotThrowAsync(
            because: "granting from the UI has to work without an operator being told to restart Servyx");
    }

    [Fact]
    public async Task Exec_path_file_writes_follow_the_same_live_posture_as_commands()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        await using var session = await GuardedTransport().ConnectAsync(DockerTarget());

        // TargetPath is only constructible through SandboxedPathResolver, so the default value stands in —
        // the guard never inspects the path for its decision, only for the refusal message.
        await session.WriteFileAsync(default, Stream.Null, new FileWriteOptions(null));

        await _grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var act = async () => await session.WriteFileAsync(default, Stream.Null, new FileWriteOptions(null));

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Exec_path_read_only_commands_keep_working_across_a_revocation()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        await using var session = await GuardedTransport().ConnectAsync(DockerTarget());

        await _grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var act = async () => await session.ExecuteAsync(new CommandSpec("cat", ["/proc/uptime"])
        {
            Intent = CommandIntent.ReadOnly,
        });

        await act.Should().NotThrowAsync(
            because: "a command the caller declared ReadOnly passes in every posture — that is what keeps " +
                "readiness probes and the read-only control tier working on a locked server");
    }

    // ── RCON path ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rcon_path_a_revoked_grant_is_refused_on_the_next_command_of_an_already_open_session()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        var channels = Channels();

        // Established FIRST, and memoized by ServyxRconChannels for the life of the process from here on.
        var session = (await channels.GetSessionAsync(ContainerId, ContainerName))
            .Should().BeOfType<WriteGuardedRconSession>().Subject;
        session.Mode.Should().Be(WriteMode.Enabled);

        await _grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var act = async () => await session.InvokeAsync("save", null);

        await act.Should().ThrowAsync<WritesDisabledException>(
            because: "the RCON channel is a second, independent capture site for the write posture; fixing " +
                "only the exec path would leave save/broadcast/shutdown flowing on a revoked grant");

        // And the same memoized instance is what a later caller gets back, so nothing about this depended on
        // the session cache being quietly emptied underneath the test.
        (await channels.GetSessionAsync(ContainerId, ContainerName)).Should().BeSameAs(session);
    }

    [Fact]
    public async Task Rcon_path_a_new_grant_takes_effect_on_the_next_command_of_an_already_open_session()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.ReadOnly);

        var channels = Channels();

        var session = (await channels.GetSessionAsync(ContainerId, ContainerName))
            .Should().BeOfType<WriteGuardedRconSession>().Subject;
        session.Mode.Should().Be(WriteMode.ReadOnly);

        var refusedFirst = async () => await session.InvokeAsync("save", null);
        await refusedFirst.Should().ThrowAsync<WritesDisabledException>();

        await _grants.SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        session.Mode.Should().Be(WriteMode.Enabled,
            because: "the guard reads the posture per command rather than holding the one it was built with");
        session.WritesPermitted.Should().BeTrue();

        // The command itself is not sent — there is no RCON server on 127.0.0.1:25575 in a hermetic test —
        // so the assertion is that it is no longer refused BY THE GUARD, i.e. it fails to connect instead.
        var act = async () => await session.InvokeAsync("save", null);
        await act.Should().NotThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Rcon_path_read_only_commands_keep_working_across_a_revocation()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        var channels = Channels();
        var session = (await channels.GetSessionAsync(ContainerId, ContainerName))
            .Should().BeOfType<WriteGuardedRconSession>().Subject;

        await _grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var act = async () => await session.InvokeAsync("info", null);
        await act.Should().NotThrowAsync<WritesDisabledException>(
            because: "the definition declares 'info' readOnly, and a read-only server that could not be " +
                "queried would defeat the purpose of the read-only tier");
    }

    // ── Both paths, one flip ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_revocation_reaches_both_already_open_capture_paths_at_once()
    {
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        await using var exec = await GuardedTransport().ConnectAsync(DockerTarget());
        var rcon = (await Channels().GetSessionAsync(ContainerId, ContainerName))
            .Should().BeOfType<WriteGuardedRconSession>().Subject;

        await exec.ExecuteAsync(Mutating());
        rcon.WritesPermitted.Should().BeTrue();

        await _grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var execAfter = async () => await exec.ExecuteAsync(Mutating());
        var rconAfter = async () => await rcon.InvokeAsync("save", null);

        await execAfter.Should().ThrowAsync<WritesDisabledException>();
        await rconAfter.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Forgetting_a_server_returns_its_already_open_session_to_read_only()
    {
        // Removing the row is the other way a grant disappears. It must fail closed exactly like a revoke:
        // a missing row is a read-only one.
        //
        // This test used to call _cache.Invalidate() by hand right after the removal, which made it look
        // like coverage and prove nothing: ServerAdoptionService.ForgetAsync calls RemoveAsync directly and
        // told the cache nothing, so the only thing being tested was that a lookup against an
        // already-invalidated cache fails closed. The invalidation is production's job now — it lives in
        // GrantInvalidatingServerRepository, which is the only IServerRepository anything can resolve — and
        // there is deliberately no hand-rolled Invalidate() anywhere in this test.
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        await using var session = await GuardedTransport().ConnectAsync(DockerTarget());
        await session.ExecuteAsync(Mutating());

        (await _servers.RemoveAsync(id)).Should().BeTrue();

        var act = async () => await session.ExecuteAsync(Mutating());
        await act.Should().ThrowAsync<WritesDisabledException>(
            because: "forgetting a server is precisely the moment an operator believes they severed every " +
                "route to it; a cache that keeps answering Enabled for that container id makes them wrong");
    }

    [Fact]
    public async Task Re_adopting_a_forgotten_server_does_not_inherit_the_grant_it_used_to_hold()
    {
        // The escalation the test above's gap made reachable. Adoption ALWAYS writes ReadOnly — granting is
        // a separate, deliberate operator act — so a freshly-adopted server must never be writable. But if
        // removal leaves the cache mapping this container id to Enabled, the new row's ReadOnly is never
        // read: the guard resolves the stale entry and a server nobody has ever granted accepts writes.
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        await using var session = await GuardedTransport().ConnectAsync(DockerTarget());
        await session.ExecuteAsync(Mutating());

        (await _servers.RemoveAsync(id)).Should().BeTrue();

        // Byte-for-byte what ServerAdoptionService.AdoptAsync persists: same container id, a new server id,
        // and WriteMode.ReadOnly.
        await _servers.AddAsync(new Server
        {
            Id = ServerId.New(),
            Name = ContainerName,
            ContainerId = ContainerId,
            GameDefinitionId = "palworld",
            DefinitionContentHash = "sha256:test",
            HostId = null,
            AdoptionMode = AdoptionMode.Adopted,
            WriteMode = ServerWriteMode.ReadOnly,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        var readopted = await _grants.DescribeAsync(ContainerId);
        readopted.Should().NotBeNull();
        readopted!.Mode.Should().Be(ServerWriteMode.ReadOnly, "adoption never grants write access by itself");

        var act = async () => await session.ExecuteAsync(Mutating());
        await act.Should().ThrowAsync<WritesDisabledException>(
            because: "the grant the container held before it was forgotten must not survive its re-adoption");

        // And a session opened after the re-adoption sees the same thing, so this is a property of the
        // grant, not of one session instance.
        await using var fresh = await GuardedTransport().ConnectAsync(DockerTarget());
        var freshAct = async () => await fresh.ExecuteAsync(Mutating());
        await freshAct.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public void ServerId_is_what_the_grant_service_takes_so_a_forgotten_row_cannot_be_re_granted_by_name()
    {
        // Guards the shape of the API rather than a behaviour: SetWriteModeAsync takes a ServerId, so there
        // is no overload an operator (or a future caller) could use to grant "whatever is called X now".
        typeof(IWriteGrantService).GetMethod(nameof(IWriteGrantService.SetWriteModeAsync))!
            .GetParameters()[0].ParameterType.Should().Be<ServerId>();
    }
}
