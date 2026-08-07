using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using Renci.SshNet;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Connectors;
using Servyx.Infrastructure.Ssh.Docker;
using Xunit;
using ContainerBuilder = DotNet.Testcontainers.Builders.ContainerBuilder;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// REAL mutation integration tests proving the "ssh+docker" transport's write-enablement end to end: a
/// disposable, uniquely-named throwaway container is genuinely started, stopped, restarted, and killed
/// through <see cref="SshDockerLifecycleSession"/> over a real SSH connection, and every outcome is verified
/// independently — via <see cref="DisposableWorkloadContainer.InspectAsync"/>'s own fresh Docker client, never
/// through the code under test.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real <c>docker</c> CLI, not the argv-echoing stub <c>SshDockerIntegrationTests</c> uses.</b> The base
/// image (<c>linuxserver/openssh-server</c>) ships no Docker client at all. This class starts its own SSH
/// container (mirroring <see cref="SshTestContainer"/>'s construction rather than reusing it, since that
/// helper has no bind-mount hook) with two additions: (1) the host daemon's own socket is bind-mounted at the
/// same path inside the container (<c>/var/run/docker.sock:/var/run/docker.sock</c> — Docker resolves bind
/// mount sources against wherever the daemon itself runs, including inside Docker Desktop's Linux VM on
/// Windows/macOS, so this works regardless of host OS), and (2) <c>apk add --no-cache docker-cli</c> installs
/// a genuine Alpine <c>docker-cli</c> package once the container is up, followed by <c>chmod 666</c> on the
/// socket so the non-root SSH user can reach it. A <c>docker stop</c> issued over SSH inside that container
/// therefore reaches the exact same daemon this test process itself talks to through
/// <see cref="DisposableWorkloadContainer"/> — which is what makes independent verification meaningful.
/// Measured cost: the <c>apk add</c> step adds roughly 5-15 seconds to fixture start (one-time per test
/// class run, not per test), on top of the few seconds <c>linuxserver/openssh-server</c> itself takes to
/// finish key generation and accept connections.
/// </para>
/// <para>
/// <b>The guard is structural, not conventional, within this file.</b> This suite cannot modify
/// <see cref="MutationTargetGuard"/> or <see cref="DisposableWorkloadContainer"/> (owned elsewhere), so true
/// compiler-enforced impossibility is out of reach. Instead, <see cref="ConnectToApprovedMutationTargetAsync"/>
/// is made the <em>only</em> method in this file that builds a live, SSH-connected
/// <see cref="IExecutionTarget"/> for a mutation-fixture container — every positive-path test obtains its
/// target exclusively through it, and it always calls <see cref="MutationTargetGuard.Approve"/> before
/// opening any connection. <see cref="The_only_connection_helper_routes_through_the_guard"/> pins that fact
/// by disassembling the compiled IL of its async state machine and asserting the call is actually present, so
/// a future edit that quietly drops the <c>Approve</c> call fails the build rather than merely hoping a
/// reviewer notices. Bypassing the guard in this suite therefore requires visibly hand-rolling a second SSH
/// connection path rather than skipping a single call.
/// </para>
/// <para>
/// <b>Two different <see cref="TargetDescriptor"/>s, deliberately.</b> <see cref="MutationTargetGuard"/>'s
/// endpoint-pinning layer checks a descriptor's endpoint against the ephemeral port
/// <see cref="DisposableWorkloadContainer"/> registered for its stand-in port — exactly the shape
/// <c>DisposableWorkloadContainerTests.The_guard_approves_the_fixtures_own_target</c> demonstrates
/// (<c>Endpoint: "{container.Host}:{container.Port}"</c>). That has nothing to do with how this suite
/// actually reaches the container: the real mutation runs over the throwaway SSH container's own address.
/// <see cref="BuildGuardDescriptor"/> builds the former (fed to <c>Approve</c>);
/// <see cref="BuildSshConnectDescriptor"/> builds the latter (fed to <see cref="SshDockerTransport.ConnectAsync"/>).
/// Both carry the same <c>containerName</c> option, so approving the first is a genuine statement about the
/// container the second one names.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SshDockerMutationTests : IAsyncLifetime
{
    private const string Password = "servyx-mutation-test-password";
    private const string SshUsername = "servyx";
    private const int SshContainerPort = 2222;
    private const string DockerSocketPath = "/var/run/docker.sock";
    private const string PrivateKeyUrn = "secret://connector/it-ssh-docker-mutation/ssh/private-key";

    private IContainer? _sshContainer;
    private string _sshHost = string.Empty;
    private int _sshPort;
    private SshRsaKeyPair? _keyPair;
    private IHostKeyVerifier? _hostKeyVerifier;
    private InMemorySecretStore? _secretStore;
    private bool _dockerAvailable;
    private string? _unavailableReason;

    public async Task InitializeAsync()
    {
        try
        {
            _keyPair = SshRsaKeyPair.Generate();
            _sshContainer = await StartSshContainerWithRealDockerAsync(Password, _keyPair.AuthorizedKeyLine);
            _sshHost = _sshContainer.Hostname;
            _sshPort = _sshContainer.GetMappedPublicPort(SshContainerPort);

            _hostKeyVerifier = await PinHostKeyAsync();
            _secretStore = await MakeSecretStoreAsync();

            _dockerAvailable = true;
        }
        catch (Exception ex)
        {
            _dockerAvailable = false;
            _unavailableReason = $"Docker is not available for mutation integration tests: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_sshContainer is not null)
        {
            await _sshContainer.DisposeAsync();
        }

        _keyPair?.Dispose();
    }

    private void SkipUnlessDockerAvailable() => Skip.IfNot(_dockerAvailable, _unavailableReason ?? "Docker unavailable");

    // ---- SSH container with a REAL docker CLI ----------------------------------------------------------------

    private static async Task<IContainer> StartSshContainerWithRealDockerAsync(string password, string publicKeyLine)
    {
        var environment = new Dictionary<string, string>
        {
            ["PUID"] = "1000",
            ["PGID"] = "1000",
            ["TZ"] = "Etc/UTC",
            ["USER_NAME"] = SshUsername,
            ["SUDO_ACCESS"] = "false",
            ["PASSWORD_ACCESS"] = "true",
            ["USER_PASSWORD"] = password,
            ["PUBLIC_KEY"] = publicKeyLine,
        };

        var container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:latest")
            .WithEnvironment(environment)
            .WithBindMount(DockerSocketPath, DockerSocketPath)
            .WithWaitStrategy(DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(SshContainerPort))
            .WithPortBinding(SshContainerPort, assignRandomHostPort: true)
            .Build();

        await container.StartAsync().ConfigureAwait(false);

        // The internal-port wait strategy only confirms sshd is listening; give it a brief additional moment
        // to finish key generation and start accepting the protocol banner, mirroring SshTestContainer.
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // The base image ships no docker client at all. Install a real one from Alpine's own repos (this
        // image is Alpine-based, so apk is always present) — this is the one piece of extra latency this
        // fixture pays that the stub-based SshDockerIntegrationTests does not.
        var install = await container.ExecAsync(["apk", "add", "--no-cache", "docker-cli"]).ConfigureAwait(false);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"apk add docker-cli failed (exit {install.ExitCode}): {install.Stderr}");
        }

        // The bind-mounted socket is root:docker-owned on the host side; chmod it world-read/write so the
        // non-root SSH user this suite authenticates as can reach it. This is a throwaway, single-purpose
        // test container — never a stand-in for how a real remote host would be configured.
        var chmod = await container.ExecAsync(["sh", "-c", $"chmod 666 {DockerSocketPath}"]).ConfigureAwait(false);
        if (chmod.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"chmod on the bind-mounted docker socket failed (exit {chmod.ExitCode}): {chmod.Stderr}");
        }

        return container;
    }

    // ---- SSH transport plumbing (host key pinning, credentials) ----------------------------------------------

    private async Task<(string Algorithm, byte[] Blob)> ProbeRawHostKeyAsync()
    {
        var connectionInfo = new ConnectionInfo(
            _sshHost, _sshPort, SshUsername, new PasswordAuthenticationMethod(SshUsername, Password));
        string? algorithm = null;
        byte[]? blob = null;

        using var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, e) =>
        {
            algorithm = e.HostKeyName;
            blob = e.HostKey;
            e.CanTrust = true;
        };

        await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        client.Disconnect();

        return (algorithm!, blob!);
    }

    private async Task<IHostKeyVerifier> PinHostKeyAsync()
    {
        var (algorithm, blob) = await ProbeRawHostKeyAsync().ConfigureAwait(false);
        var hostKeyStore = new FileHostKeyStore(
            Path.Combine(Path.GetTempPath(), $"servyx-it-dockermutation-hostkeys-{Guid.NewGuid():N}.json"));
        await hostKeyStore.PinAsync(
            new HostKeyRecord(_sshHost, _sshPort, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"),
            "test").ConfigureAwait(false);
        return new HostKeyVerifier(hostKeyStore);
    }

    private async Task<InMemorySecretStore> MakeSecretStoreAsync()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(
            SecretUrn.Create("connector", "it-ssh-docker-mutation", "ssh", "private-key"),
            Encoding.ASCII.GetBytes(_keyPair!.PrivateKeyPem),
            "test").ConfigureAwait(false);
        return store;
    }

    private SshDockerTransport CreateInnerTransport()
    {
        var sshTransport = new SshTransport(_secretStore!, _hostKeyVerifier!);
        return new SshDockerTransport(sshTransport);
    }

    // ---- Descriptor construction ------------------------------------------------------------------------------

    /// <summary>
    /// The descriptor fed to <see cref="MutationTargetGuard.Approve"/> — shaped exactly like
    /// <c>DisposableWorkloadContainerTests.The_guard_approves_the_fixtures_own_target</c>: endpoint is the
    /// fixture's own loopback stand-in port, which only a live fixture could supply.
    /// </summary>
    private static TargetDescriptor BuildGuardDescriptor(DisposableWorkloadContainer container) => new(
        TransportId: "ssh+docker",
        Endpoint: $"{container.Host}:{container.Port}",
        CredentialUrn: null,
        DockerContext: null,
        Options: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = container.Name });

    /// <summary>The descriptor actually used to open the real SSH connection that runs docker commands.</summary>
    private TargetDescriptor BuildSshConnectDescriptor(string containerName) => new(
        TransportId: "ssh+docker",
        Endpoint: $"{SshUsername}@{_sshHost}:{_sshPort}",
        CredentialUrn: PrivateKeyUrn,
        DockerContext: null,
        Options: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = containerName });

    /// <summary>
    /// The ONLY method in this suite that opens a live, write-guarded connection to a mutation-fixture
    /// container. Always approves the fixture's own descriptor through <see cref="MutationTargetGuard.Approve"/>
    /// first — see this type's remarks and <see cref="The_only_connection_helper_routes_through_the_guard"/>,
    /// which proves this by IL inspection rather than trusting the source to stay this way.
    /// </summary>
    private async Task<IExecutionTarget> ConnectToApprovedMutationTargetAsync(
        DisposableWorkloadContainer container, IWriteModeResolver writeModes)
    {
        MutationTargetGuard.Approve(BuildGuardDescriptor(container));

        var guardedTransport = new WriteGuardedTransport(CreateInnerTransport(), writeModes);
        return await guardedTransport.ConnectAsync(BuildSshConnectDescriptor(container.Name)).ConfigureAwait(false);
    }

    private static IWriteModeResolver SingleContainerGrant(string containerName) =>
        new GrantedWriteModeResolver(
        [
            new WriteModeGrant(
                WriteMode.Enabled,
                "ssh+docker",
                requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = containerName }),
        ]);

    // ---- Arrangement-only helper: an independent Docker client used ONLY to set up preconditions, never to
    // verify outcomes (verification always goes through DisposableWorkloadContainer.InspectAsync). ----------

    private static async Task StopContainerDirectlyAsync(string containerId)
    {
        using var client = new DockerClientBuilder().Build();
        await client.Containers.StopContainerAsync(
            containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }).ConfigureAwait(false);
    }

    // ---- Tests -------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Stopping_a_container_actually_stops_it()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();
        await using var target = await ConnectToApprovedMutationTargetAsync(container, SingleContainerGrant(container.Name));
        var lifecycle = (IContainerLifecycle)target;

        var result = await lifecycle.InvokeAsync(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, container.Name, TimeSpan.FromSeconds(5)));

        result.Success.Should().BeTrue(result.Detail);

        var state = await container.InspectAsync();
        state.State!.Running.Should().BeFalse();
    }

    [SkippableFact]
    public async Task Starting_a_stopped_container_actually_starts_it()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();

        // Arrangement only: force the container stopped via an independent client, never via the code
        // under test — the "Start" verb is what this test exists to exercise.
        await StopContainerDirectlyAsync(container.ContainerId);
        (await container.InspectAsync()).State!.Running.Should().BeFalse("arrangement must have actually stopped it");

        await using var target = await ConnectToApprovedMutationTargetAsync(container, SingleContainerGrant(container.Name));
        var lifecycle = (IContainerLifecycle)target;

        var result = await lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, container.Name));

        result.Success.Should().BeTrue(result.Detail);
        (await container.InspectAsync()).State!.Running.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Restarting_a_container_moves_its_started_at_forward()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();
        var before = (await container.InspectAsync()).State!.StartedAt;

        await using var target = await ConnectToApprovedMutationTargetAsync(container, SingleContainerGrant(container.Name));
        var lifecycle = (IContainerLifecycle)target;

        var result = await lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Restart, container.Name));

        result.Success.Should().BeTrue(result.Detail);
        var after = (await container.InspectAsync()).State!.StartedAt;
        DateTimeOffset.Parse(after).Should().BeAfter(DateTimeOffset.Parse(before), "a real restart must move the daemon's own StartedAt timestamp forward");
    }

    [SkippableFact]
    public async Task Killing_a_container_with_sigkill_stops_it()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();
        await using var target = await ConnectToApprovedMutationTargetAsync(container, SingleContainerGrant(container.Name));
        var lifecycle = (IContainerLifecycle)target;

        var result = await lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, container.Name));

        result.Success.Should().BeTrue(result.Detail);
        (await container.InspectAsync()).State!.Running.Should().BeFalse();
    }

    [SkippableFact]
    public async Task Stop_honors_the_grace_period()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();
        await using var target = await ConnectToApprovedMutationTargetAsync(container, SingleContainerGrant(container.Name));
        var lifecycle = (IContainerLifecycle)target;

        // busybox `tail -f /dev/null` runs as this container's PID 1 with no SIGTERM handler installed. The
        // Linux kernel does not apply the default terminate-on-SIGTERM disposition to an unhandled signal
        // delivered to PID 1 specifically, so `docker stop --time N` genuinely blocks for the full grace
        // period before falling back to SIGKILL — a real, observable proof that the requested timeout
        // reached the docker command rather than being ignored or clamped.
        var gracePeriod = TimeSpan.FromSeconds(4);
        var stopwatch = Stopwatch.StartNew();
        var result = await lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, container.Name, gracePeriod));
        stopwatch.Stop();

        result.Success.Should().BeTrue(result.Detail);
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(3),
            "the grace period must have actually reached the docker stop command, not been ignored or truncated");
    }

    [SkippableFact]
    public async Task Without_a_write_grant_stop_is_refused_and_the_container_keeps_running()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();

        // The default resolver: every target is read-only, exactly what a server that never opted in gets.
        await using var target = await ConnectToApprovedMutationTargetAsync(container, ReadOnlyWriteModeResolver.Instance);
        var lifecycle = (IContainerLifecycle)target;

        var act = () => lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, container.Name, TimeSpan.FromSeconds(5)));

        await act.Should().ThrowAsync<WritesDisabledException>();

        (await container.InspectAsync()).State!.Running.Should().BeTrue("a refused stop must never reach the daemon");
    }

    [SkippableFact]
    public async Task A_grant_for_a_different_container_does_not_authorize_this_one()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();

        // A real-shaped grant, but scoped to a container name that is not this one.
        var wrongContainerGrant = SingleContainerGrant($"servyx-mutation-test-{Guid.NewGuid():N}");
        await using var target = await ConnectToApprovedMutationTargetAsync(container, wrongContainerGrant);
        var lifecycle = (IContainerLifecycle)target;

        var act = () => lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, container.Name, TimeSpan.FromSeconds(5)));

        await act.Should().ThrowAsync<WritesDisabledException>();
        (await container.InspectAsync()).State!.Running.Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_grant_for_a_different_endpoint_does_not_authorize_this_one()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();

        // Endpoint-scoped rather than container-scoped, and pointed at an endpoint that is not this SSH
        // session's real one — proves the grant's endpoint narrowness independently of containerName scoping.
        var wrongEndpointGrant = new GrantedWriteModeResolver(
        [
            new WriteModeGrant(WriteMode.Enabled, "ssh+docker", endpoint: "someone-else@203.0.113.10:22"),
        ]);
        await using var target = await ConnectToApprovedMutationTargetAsync(container, wrongEndpointGrant);
        var lifecycle = (IContainerLifecycle)target;

        var act = () => lifecycle.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, container.Name, TimeSpan.FromSeconds(5)));

        await act.Should().ThrowAsync<WritesDisabledException>();
        (await container.InspectAsync()).State!.Running.Should().BeTrue();
    }

    [SkippableFact]
    public void The_guard_refuses_a_target_it_did_not_create()
    {
        SkipUnlessDockerAvailable();

        // Hand-built, naming a container this process never started and never registered.
        var handBuilt = new TargetDescriptor(
            TransportId: "ssh+docker",
            Endpoint: "127.0.0.1:54321",
            CredentialUrn: null,
            DockerContext: null,
            Options: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "palworld-server" });

        var act = () => MutationTargetGuard.Approve(handBuilt);

        act.Should().Throw<MutationTargetRefusedException>();
    }

    [SkippableFact]
    public async Task Read_only_commands_still_work_under_a_write_grant()
    {
        SkipUnlessDockerAvailable();
        await using var container = await DisposableWorkloadContainer.StartAsync();
        await using var target = await ConnectToApprovedMutationTargetAsync(container, SingleContainerGrant(container.Name));

        var ps = await target.ExecuteAsync(DockerCli.Ps());
        var inspect = await target.ExecuteAsync(DockerCli.Inspect(container.Name));

        ps.Succeeded.Should().BeTrue(ps.StandardError);
        ps.StandardOutput.Should().Contain(container.Name);
        inspect.Succeeded.Should().BeTrue(inspect.StandardError);
    }

    // ---- Structural proof that the connection helper never skips the guard ------------------------------------

    [Fact]
    public void The_only_connection_helper_routes_through_the_guard()
    {
        var helper = typeof(SshDockerMutationTests).GetMethod(
            nameof(ConnectToApprovedMutationTargetAsync), BindingFlags.NonPublic | BindingFlags.Instance);
        helper.Should().NotBeNull();

        var approve = typeof(MutationTargetGuard).GetMethod(nameof(MutationTargetGuard.Approve));
        approve.Should().NotBeNull();

        var moveNext = AsyncIl.ResolveStateMachineMoveNext(helper!);
        var calledMethods = AsyncIl.ExtractCalledMethods(moveNext);

        calledMethods.Should().Contain(
            m => m.Name == approve!.Name && m.DeclaringType == approve.DeclaringType,
            "ConnectToApprovedMutationTargetAsync is the only method this suite uses to obtain a live " +
            "connection to a mutation-fixture container; its compiled IL must contain a real call to " +
            "MutationTargetGuard.Approve, not merely a comment promising one");
    }

    /// <summary>
    /// A tiny, purpose-built IL reader: given a method, decodes its instruction stream well enough to collect
    /// every method it calls, resolved back to a <see cref="MethodBase"/>. Used only to prove
    /// <see cref="ConnectToApprovedMutationTargetAsync"/>'s compiled async state machine genuinely contains a
    /// call to <see cref="MutationTargetGuard.Approve"/>, rather than trusting a comment to stay true.
    /// </summary>
    private static class AsyncIl
    {
        private static readonly Dictionary<short, OpCode> OneByteOpCodes = BuildOpCodeMap(size: 1);
        private static readonly Dictionary<short, OpCode> TwoByteOpCodes = BuildOpCodeMap(size: 2);

        /// <summary>
        /// An async method's own IL body only constructs and starts its compiler-generated state machine; the
        /// actual method contents (including any pre-await synchronous calls) live in that state machine's
        /// <c>MoveNext</c> override, so that is what must be disassembled.
        /// </summary>
        public static MethodInfo ResolveStateMachineMoveNext(MethodInfo asyncMethod)
        {
            var attribute = asyncMethod.GetCustomAttribute<AsyncStateMachineAttribute>()
                ?? throw new InvalidOperationException($"{asyncMethod.Name} is not a compiler-generated async method.");

            return attribute.StateMachineType.GetMethod(
                "MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"No MoveNext method found on {attribute.StateMachineType}.");
        }

        public static IReadOnlyList<MethodBase> ExtractCalledMethods(MethodBase method)
        {
            var body = method.GetMethodBody() ?? throw new InvalidOperationException($"{method.Name} has no method body.");
            var il = body.GetILAsByteArray() ?? throw new InvalidOperationException($"{method.Name} has no IL bytes.");
            var module = method.Module;
            var typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
            var methodArguments = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;

            var results = new List<MethodBase>();
            var offset = 0;

            while (offset < il.Length)
            {
                OpCode opCode;
                if (il[offset] == 0xFE)
                {
                    var second = il[offset + 1];
                    if (!TwoByteOpCodes.TryGetValue(unchecked((short)(0xFE00 | second)), out opCode))
                    {
                        throw new InvalidOperationException($"Unknown two-byte opcode 0xFE{second:X2} at offset {offset} in {method.Name}.");
                    }

                    offset += 2;
                }
                else
                {
                    if (!OneByteOpCodes.TryGetValue(il[offset], out opCode))
                    {
                        throw new InvalidOperationException($"Unknown one-byte opcode 0x{il[offset]:X2} at offset {offset} in {method.Name}.");
                    }

                    offset += 1;
                }

                var operandSize = OperandSize(opCode.OperandType, il, offset);

                if (opCode.OperandType == OperandType.InlineMethod)
                {
                    var token = BitConverter.ToInt32(il, offset);
                    try
                    {
                        var resolved = module.ResolveMethod(token, typeArguments, methodArguments);
                        if (resolved is not null)
                        {
                            results.Add(resolved);
                        }
                    }
                    catch
                    {
                        // A handful of tokens (rare generic instantiations) can fail to resolve outside a
                        // fully-specified generic context; this scan only needs to find one specific call, not
                        // enumerate every one, so a resolution failure is skipped rather than fatal.
                    }
                }

                offset += operandSize;
            }

            return results;
        }

        private static int OperandSize(OperandType operandType, byte[] il, int offset) => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
            _ => throw new InvalidOperationException($"Unhandled IL operand type {operandType}."),
        };

        private static Dictionary<short, OpCode> BuildOpCodeMap(int size)
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode opCode || opCode.Size != size)
                {
                    continue;
                }

                map[opCode.Value] = opCode;
            }

            return map;
        }
    }
}
