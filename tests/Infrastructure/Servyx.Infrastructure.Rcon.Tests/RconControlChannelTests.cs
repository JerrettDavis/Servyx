using System.Reflection;

using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The control-channel path for a resource no transport can reach — <c>docs/provisioning.md</c> §11.8's
/// item (2).
/// </summary>
/// <remarks>
/// <para>
/// Two shape-M adapters can create a resource whose <see cref="ResourceReachability"/> is
/// <see cref="ResourceReachability.NoTransport"/>, and until this type existed nothing in the codebase could
/// consume one: the workload was reachable by nothing at all. These assertions cover the three things that
/// makes it, in order of how expensive they are to get wrong.
/// </para>
/// <para>
/// First, that operating an unreachable resource does not make it <em>look</em> reachable — no
/// <see cref="TargetDescriptor"/> appears anywhere, and the resource that was operated still refuses to hand
/// one back afterwards. Second, that the definition's <c>readOnly</c> discipline reaches this path intact,
/// because a control channel to a cloud workload with no other route in is exactly where a missing write
/// guard would matter most. Third, that a non-durable address is refused rather than opened, since a channel
/// that connects today and silently points elsewhere after a routine restart is worse than no channel.
/// </para>
/// <para>
/// End to end where it can be: a real <see cref="SourceRconClient"/> against the loopback
/// <see cref="FakeRconServer"/>, with the credential resolved through a real <see cref="ISecretStore"/>.
/// </para>
/// </remarks>
public class RconControlChannelTests
{
    private const string Password = "S3cr3t-Palworld-Admin-Password";

    private const string AciUnreachableReason =
        "An Azure Container Instances container group exposes no Docker Engine endpoint, runs no sshd, and is not "
        + "the Servyx host, so none of Servyx's transports ('docker', 'ssh', 'local') can address it.";

    private const string FargateNoAddressReason =
        "an ECS service on Fargate has no address that outlives the workload; a load balancer or a Cloud Map "
        + "registration would have to be created first.";

    private const string DnsLabelJustification =
        "the container group was provisioned with a dnsNameLabel, and Azure keeps that name pointed at whatever "
        + "public IP the group currently holds.";

    private const string NoDnsLabelReason =
        "the container group has no dnsNameLabel, so its public IP is the only address it has, and ACI warns that "
        + "IP may change when the group restarts. Provision the group with a dnsNameLabel.";

    private static readonly SecretUrn PasswordUrn =
        SecretUrn.Create("server", "palworld-server", "rcon", "password");

    private static TimeoutPolicy Fast() => new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(1),
        1);

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
        new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
    ]);

    /// <summary>A container group as the ACI adapter hands one back: created, billing, and unreachable.</summary>
    private static ProvisionedResource UnreachableResource() => new(
        Handle: new ResourceHandle(
            "azure-container-instance",
            "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ContainerInstance/containerGroups/palworld",
            "eastus",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" }),
        ConnectorId: "conn-aci-1",
        Reachability: new ResourceReachability.NoTransport(AciUnreachableReason),
        Facts: new ResourceFacts(null, null, CostEstimate.Unknown("not priced in this test"), DateTimeOffset.UnixEpoch));

    private static ProvisionedResource ReachableResource() => new(
        Handle: new ResourceHandle("docker-container", "abc123", null, new Dictionary<string, string>(StringComparer.Ordinal)),
        ConnectorId: "conn-docker-1",
        Target: new TargetDescriptor(
            TransportId: "docker",
            Endpoint: "npipe://./pipe/docker_engine",
            CredentialUrn: null,
            DockerContext: null,
            Options: new Dictionary<string, string>(StringComparer.Ordinal)),
        Facts: new ResourceFacts(null, null, CostEstimate.Unknown("not priced in this test"), DateTimeOffset.UnixEpoch));

    private static (RconControlChannel Channel, InMemorySecretStore Secrets) Build(IRconAuditSink? audit = null)
    {
        var secrets = new InMemorySecretStore().With(PasswordUrn, Password);
        return (new RconControlChannel(new SourceRconClient(Fast()), secrets, audit), secrets);
    }

    private static RconControlChannelSpec Spec(int port, WriteMode mode = WriteMode.Enabled) =>
        new(port, PasswordUrn, Palworld(), mode);

    private static ControlChannelAddress Durable(string host) => new ControlChannelAddress.Durable(host, DnsLabelJustification);

    // -----------------------------------------------------------------------------------------------------
    // A resource no transport can reach can nevertheless be operated.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resource_no_transport_can_reach_can_still_be_operated_through_the_control_channel()
    {
        await using var server = new FakeRconServer(password: Password, responseFragments: ["Complete Save"]);
        var (channel, secrets) = Build();
        var resource = UnreachableResource();

        var session = channel.Open(resource, Durable(server.Endpoint.Host), Spec(server.Endpoint.Port));

        secrets.GetCalls.Should().Be(0, "opening a channel resolves nothing; the credential is resolved at the point of use");

        var response = await session.InvokeAsync("save", null);

        response.Text.Should().Be("Complete Save");
        server.Commands.Should().ContainSingle().Which.Should().Be("Save");
        secrets.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task Operating_it_does_not_make_it_reachable()
    {
        // The load-bearing assertion of the whole path. The operator can talk to the game; Servyx still
        // cannot read a file, run a command, or claim a transport - so the Provision tier stays out of reach.
        await using var server = new FakeRconServer(password: Password);
        var (channel, _) = Build();
        var resource = UnreachableResource();

        var session = channel.Open(resource, Durable(server.Endpoint.Host), Spec(server.Endpoint.Port));
        await session.InvokeAsync("info", null);

        resource.Reachability.Should().BeOfType<ResourceReachability.NoTransport>()
            .Which.Reason.Should().Be(AciUnreachableReason);
        resource.TargetOrNull().Should().BeNull();

        var act = () => resource.RequireTarget();
        act.Should().Throw<InvalidOperationException>().WithMessage("*not reachable by any transport*");
    }

    [Fact]
    public async Task The_session_it_hands_back_is_guarded_by_construction()
    {
        await using var server = new FakeRconServer(password: Password);
        var (channel, _) = Build();

        var session = channel.Open(UnreachableResource(), Durable(server.Endpoint.Host), Spec(server.Endpoint.Port));

        // Not "a session that a caller should remember to wrap" - there is no branch that returns the inner one.
        session.Should().BeOfType<WriteGuardedRconSession>()
            .Which.TargetDescription.Should().Contain("azure-container-instance");
    }

    [Fact]
    public async Task The_address_is_resolved_from_the_adapter_that_owns_the_resource()
    {
        await using var server = new FakeRconServer(password: Password);
        var (channel, _) = Build();
        var resource = UnreachableResource();
        var addresses = new RecordingAddressSource(Durable(server.Endpoint.Host));

        var session = await channel.OpenAsync(resource, addresses, Spec(server.Endpoint.Port));
        await session.InvokeAsync("info", null);

        addresses.Asked.Should().ContainSingle().Which.Should().BeSameAs(resource.Handle);
    }

    // -----------------------------------------------------------------------------------------------------
    // The readOnly discipline holds on this path.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_only_command_passes_on_a_read_only_server()
    {
        await using var server = new FakeRconServer(password: Password, responseFragments: ["Welcome to Palworld"]);
        var (channel, _) = Build();

        var session = channel.Open(
            UnreachableResource(),
            Durable(server.Endpoint.Host),
            Spec(server.Endpoint.Port, WriteMode.ReadOnly));

        var response = await session.InvokeAsync("info", null);

        response.Text.Should().Be("Welcome to Palworld");
        server.Commands.Should().ContainSingle().Which.Should().Be("Info");
    }

    [Fact]
    public async Task A_mutating_command_is_refused_on_a_read_only_server_before_the_socket_is_touched()
    {
        await using var server = new FakeRconServer(password: Password);
        var (channel, secrets) = Build();

        var session = channel.Open(
            UnreachableResource(),
            Durable(server.Endpoint.Host),
            Spec(server.Endpoint.Port, WriteMode.ReadOnly));

        var act = () => session.InvokeAsync("save", null);

        await act.Should().ThrowAsync<WritesDisabledException>();
        server.Commands.Should().BeEmpty();
        secrets.GetCalls.Should().Be(0, "the guard refuses before the credential is resolved");
    }

    [Fact]
    public async Task The_raw_escape_hatch_is_refused_on_a_read_only_server()
    {
        await using var server = new FakeRconServer(password: Password);
        var (channel, _) = Build(new RecordingAuditSink());

        var session = channel.Open(
            UnreachableResource(),
            Durable(server.Endpoint.Host),
            Spec(server.Endpoint.Port, WriteMode.ReadOnly));

        var act = () => session.SendRawAsync("DoExit");

        await act.Should().ThrowAsync<WritesDisabledException>();
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task An_undeclared_command_id_is_refused_whatever_the_write_mode()
    {
        await using var server = new FakeRconServer(password: Password);
        var (channel, _) = Build();

        var session = channel.Open(UnreachableResource(), Durable(server.Endpoint.Host), Spec(server.Endpoint.Port));

        var act = () => session.InvokeAsync("rm-rf", null);

        await act.Should().ThrowAsync<RconUnknownCommandException>();
        server.Commands.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------------
    // A non-durable address is refused, not opened.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_ephemeral_address_is_refused_and_the_refusal_names_the_change_that_would_fix_it()
    {
        var (channel, secrets) = Build();

        var act = () => channel.Open(
            UnreachableResource(),
            new ControlChannelAddress.Ephemeral("203.0.113.42", NoDnsLabelReason),
            Spec(25575));

        var thrown = act.Should().Throw<ControlChannelUnavailableException>().Which;

        thrown.Message.Should().Contain("203.0.113.42");
        thrown.Message.Should().Contain("dnsNameLabel");
        thrown.Message.Should().Contain(AciUnreachableReason, "the operator needs both halves of the story, not just the second");
        thrown.Address.Should().BeOfType<ControlChannelAddress.Ephemeral>();
        secrets.GetCalls.Should().Be(0);
    }

    [Fact]
    public void An_absent_address_is_refused_and_the_refusal_carries_the_provider_reason()
    {
        var (channel, _) = Build();

        var act = () => channel.Open(
            UnreachableResource(),
            new ControlChannelAddress.NoAddress(FargateNoAddressReason),
            Spec(25575));

        var thrown = act.Should().Throw<ControlChannelUnavailableException>().Which;

        thrown.Message.Should().Contain(FargateNoAddressReason);
        thrown.Message.Should().Contain("Nothing is broken");
        thrown.Address.Should().BeOfType<ControlChannelAddress.NoAddress>();
    }

    [Fact]
    public async Task A_resolved_absent_address_refuses_the_same_way_as_a_supplied_one()
    {
        var (channel, _) = Build();
        var addresses = new RecordingAddressSource(new ControlChannelAddress.NoAddress(FargateNoAddressReason));

        var act = () => channel.OpenAsync(UnreachableResource(), addresses, Spec(25575));

        await act.Should().ThrowAsync<ControlChannelUnavailableException>();
        addresses.Asked.Should().ContainSingle();
    }

    [Fact]
    public void There_is_no_force_path_past_a_non_durable_address()
    {
        // No overload, flag, or option on this type takes an ephemeral address and opens it anyway. If one
        // is ever added, this fails - which is the point: the refusal is the feature.
        var openers = typeof(RconControlChannel)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name is "Open" or "OpenAsync")
            .ToArray();

        openers.Should().HaveCount(2, "one synchronous opener and one that resolves the address first");

        openers.SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType)
            .Should().NotContain(typeof(bool), "a boolean on an opener is how a force path arrives");
    }

    // -----------------------------------------------------------------------------------------------------
    // Nothing here produces a transport.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_on_this_path_accepts_or_produces_a_transport_target()
    {
        var types = new[] { typeof(RconControlChannel), typeof(RconControlChannelSpec), typeof(ControlChannelUnavailableException) };

        foreach (var type in types)
        {
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
                .Should().NotContain(p => p.PropertyType == typeof(TargetDescriptor));

            type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
                .Should().NotContain(m => m.ReturnType == typeof(TargetDescriptor));

            type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
                .SelectMany(m => m.GetParameters())
                .Select(p => p.ParameterType)
                .Should().NotContain(typeof(TargetDescriptor));

            type.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .Should().NotContain(typeof(TargetDescriptor));
        }
    }

    [Fact]
    public void The_only_strategy_that_can_serve_an_unreachable_resource_is_direct_tcp()
    {
        // The other three strategies all need the very things the adapter's unreachability reason says the
        // provider does not have: docker-exec-tool and docker-exec-network need a Docker daemon, ssh-tunnel
        // needs an sshd.
        RconControlChannel.StrategyId.Should().Be(DirectTcpRconReachability.Id);
    }

    // -----------------------------------------------------------------------------------------------------
    // Explaining, without connecting to anything.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_explanation_states_the_transport_answer_and_the_control_answer_together()
    {
        var explanation = RconControlChannel.Explain(
            UnreachableResource(),
            Durable("palworld.eastus.azurecontainer.io"));

        explanation.Should().Contain(AciUnreachableReason);
        explanation.Should().Contain("palworld.eastus.azurecontainer.io");
        explanation.Should().Contain("Operate tier and no higher");
        explanation.Should().Contain(DnsLabelJustification);
    }

    [Fact]
    public void An_explanation_of_a_reachable_resource_says_the_channel_is_an_addition_rather_than_the_only_route()
    {
        var explanation = RconControlChannel.Explain(ReachableResource(), Durable("127.0.0.1"));

        explanation.Should().Contain("reachable by the 'docker' transport");
        explanation.Should().Contain("addition to that route");
    }

    [Fact]
    public void An_explanation_of_an_unopenable_address_says_so_without_opening_anything()
    {
        var explanation = RconControlChannel.Explain(
            UnreachableResource(),
            new ControlChannelAddress.NoAddress(FargateNoAddressReason));

        explanation.Should().Contain("No control channel will be opened");
        explanation.Should().Contain(FargateNoAddressReason);
    }

    // -----------------------------------------------------------------------------------------------------
    // Argument discipline.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_channel_without_a_credential_urn_is_refused_rather_than_opened_unauthenticated()
    {
        var (channel, _) = Build();

        var act = () => channel.Open(
            UnreachableResource(),
            Durable("palworld.eastus.azurecontainer.io"),
            new RconControlChannelSpec(25575, default, Palworld(), WriteMode.Enabled));

        act.Should().Throw<ArgumentException>().WithMessage("*URN*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_channel_without_a_usable_port_is_refused(int port)
    {
        var (channel, _) = Build();

        var act = () => channel.Open(
            UnreachableResource(),
            Durable("palworld.eastus.azurecontainer.io"),
            Spec(port));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>An address source that records what it was asked about and answers a fixed address.</summary>
    private sealed class RecordingAddressSource(ControlChannelAddress address) : IControlChannelAddressSource
    {
        internal List<ResourceHandle> Asked { get; } = [];

        public Task<ControlChannelAddress> ResolveControlAddressAsync(ResourceHandle handle, CancellationToken ct = default)
        {
            Asked.Add(handle);
            return Task.FromResult(address);
        }
    }
}
