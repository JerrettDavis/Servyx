using NSubstitute;

using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The point of the whole adapter: <c>docs/provisioning.md</c> §2 claims shape I produces a <em>host</em>, not
/// a game server, so a cloud deployment is a two-stage plan in which shape H then "runs against that connector
/// identically to any bare-metal SSH box" and "no cloud adapter contains install logic".
/// </summary>
/// <remarks>
/// <para>
/// <strong>What these tests can and cannot prove, stated up front.</strong> The Docker adapter's handoff test
/// can pass the <em>same object</em> from provisioner to transport, because <c>DockerTransport</c> takes a
/// <see cref="TargetDescriptor"/>. <c>SshProcessProvisioner</c> does not: its constructor takes an endpoint
/// string, a credential URN and an options dictionary, and builds its own descriptor internally. So the
/// shape I → shape H hand-off is <strong>parameter-passing, not object hand-off</strong>, and nothing at the
/// type level stops a future call site forwarding the endpoint while dropping the credential URN. These tests
/// therefore prove the weaker but true claim — <em>data preservation</em>: every field the cloud adapter put
/// on its descriptor comes out of the SSH adapter unchanged, with no translation step in between. The
/// stronger "unchanged by construction" reading is not available here, and the report says so.
/// </para>
/// <para>
/// Note also that <c>SshTransport</c> <em>does</em> take a <see cref="TargetDescriptor"/>, so the
/// object-identity form of the claim is available for the transport half and is asserted below.
/// </para>
/// </remarks>
public class ShapeIToShapeHCompositionTests
{
    private static SshTransport RealSshTransport() =>
        new(Substitute.For<ISecretStore>(), Substitute.For<IHostKeyVerifier>());

    private static ProvisioningRequest PalworldInstallRequest() =>
        new(
            "palworld",
            "native-steamcmd",
            DigitalOceanScenario.ConnectorId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instanceId"] = DigitalOceanScenario.InstanceId,
                ["jobId"] = DigitalOceanScenario.JobId,
                ["connectorId"] = DigitalOceanScenario.ConnectorId,
                ["dataDir"] = "/opt/palworld",
                ["executable"] = "PalServer.sh",
            });

    [Fact]
    public async Task The_cloud_adapter_produces_an_ordinary_ssh_target_not_a_digitalocean_specific_one()
    {
        var resource = await new DigitalOceanScenario().CreateAsync();

        // Not "digitalocean", not "digitalocean-ssh" - the transport that already existed before this adapter
        // did, asserted against the real transport's own property so the magic string cannot drift.
        resource.Target.TransportId.Should().Be("ssh");
        resource.Target.TransportId.Should().Be(RealSshTransport().TransportId);
    }

    [Fact]
    public async Task The_endpoint_is_exactly_what_the_ssh_endpoint_parser_reads()
    {
        var resource = await new DigitalOceanScenario().CreateAsync();

        // SshEndpoint.Parse is literally the first thing SshConnector.OpenAsync does with a descriptor's
        // endpoint, so agreeing with it is agreeing with the transport.
        var (endpoint, username) = SshEndpoint.Parse(resource.Target.Endpoint);

        resource.Target.Endpoint.Should().Be($"ssh://root@{DigitalOceanScenario.PublicIp}:22");
        endpoint.Host.Should().Be(DigitalOceanScenario.PublicIp);
        endpoint.Port.Should().Be(22);
        username.Should().Be("root");
    }

    [Fact]
    public async Task The_descriptor_carries_no_digitalocean_specific_field_at_all()
    {
        var scenario = new DigitalOceanScenario();
        var resource = await scenario.CreateAsync();

        // Everything on it is either a key the SSH transport already read before this adapter existed, or the
        // caller's own pass-through option. There is no droplet id, no region, no account, no size - those live
        // on the ResourceHandle, which is where provider-specific state belongs.
        resource.Target.Options.Keys.Should().BeEquivalentTo(["trustPolicy", "declaredChannels", "rootPath"]);
        resource.Target.DockerContext.Should().BeNull();
        resource.Target.CredentialUrn.Should().Be(DigitalOceanScenario.SshCredentialUrn);
        resource.Target.Options["rootPath"].Should().Be("/", "shape I hands back a host, which has no per-server data directory");

        string.Join("|", resource.Target.Options.Select(o => $"{o.Key}={o.Value}") .Append(resource.Target.Endpoint))
            .Should().NotContain("droplet", "the descriptor must not leak the provider's vocabulary downstream");
    }

    [Fact]
    public async Task The_real_ssh_transport_consumes_the_cloud_adapters_descriptor_with_no_translation()
    {
        var resource = await new DigitalOceanScenario().CreateAsync();

        // The descriptor instance is passed straight through - no adapter, no copy, no field fix-up. The probe
        // gets as far as credential resolution, which is past endpoint parsing and connector-descriptor
        // construction, and stops there only because a unit test supplies no credentials.
        var health = await RealSshTransport().ProbeAsync(resource.Target);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("neither a 'password' nor a 'private-key'");
        health.Detail.Should().NotContain("does not contain a host");
    }

    [Fact]
    public async Task A_registry_of_transports_resolves_the_cloud_targets_transport_by_id()
    {
        var resource = await new DigitalOceanScenario().CreateAsync();

        var docker = Substitute.For<ITransport>();
        docker.TransportId.Returns("docker");
        ITransport[] registered = [docker, RealSshTransport()];

        var resolved = registered.Single(t => string.Equals(t.TransportId, resource.Target.TransportId, StringComparison.Ordinal));

        resolved.Should().BeOfType<SshTransport>();
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.ExecuteCommand);
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.FileWrite);
    }

    [Fact]
    public async Task The_ssh_install_adapter_takes_the_cloud_adapters_output_and_installs_onto_it_unaided()
    {
        var cloud = await new DigitalOceanScenario().CreateAsync();
        var host = new SshHostDouble();

        // THE COMPOSITION. Every argument below is read straight off the descriptor shape I handed back. There
        // is no mapping function, no DigitalOcean type, and no branch on "is this a cloud host" anywhere.
        var installer = new SshProcessProvisioner(
            host.Transport,
            endpoint: cloud.Target.Endpoint,
            credentialUrn: cloud.Target.CredentialUrn,
            transportOptions: cloud.Target.Options);

        var installed = await installer
            .CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest()))
            .CreateAsync();

        installed.Target.TransportId.Should().Be(cloud.Target.TransportId);
        installed.Target.Endpoint.Should().Be(cloud.Target.Endpoint);
        installed.Target.CredentialUrn.Should().Be(cloud.Target.CredentialUrn);
        installed.Target.DockerContext.Should().Be(cloud.Target.DockerContext);
        installed.Target.Options["trustPolicy"].Should().Be(cloud.Target.Options["trustPolicy"]);
        installed.Target.Options["declaredChannels"].Should().Be(cloud.Target.Options["declaredChannels"]);

        // The one option that legitimately changes, and the shape boundary made visible: the host's root path
        // ("/") is replaced by the server's data directory once a server exists on that host.
        installed.Target.Options["rootPath"].Should().Be("/opt/palworld");
        cloud.Target.Options["rootPath"].Should().Be("/");
    }

    [Fact]
    public async Task The_ssh_adapter_connects_to_the_machine_the_cloud_adapter_created()
    {
        var cloud = await new DigitalOceanScenario().CreateAsync();
        var host = new SshHostDouble();

        var installer = new SshProcessProvisioner(
            host.Transport,
            endpoint: cloud.Target.Endpoint,
            credentialUrn: cloud.Target.CredentialUrn,
            transportOptions: cloud.Target.Options);

        await installer.CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest())).CreateAsync();

        host.Connected.Should().NotBeEmpty();
        host.Connected.Should().OnlyContain(d => d.Endpoint == cloud.Target.Endpoint);
        SshEndpoint.Parse(host.Connected[0].Endpoint).Endpoint.Host.Should().Be(DigitalOceanScenario.PublicIp);
    }

    [Fact]
    public async Task Both_stages_stamp_the_same_servyx_identity_so_one_sweep_understands_the_others_handles()
    {
        var cloud = await new DigitalOceanScenario().CreateAsync();
        var host = new SshHostDouble();

        var installed = await new SshProcessProvisioner(
                host.Transport,
                endpoint: cloud.Target.Endpoint,
                credentialUrn: cloud.Target.CredentialUrn,
                transportOptions: cloud.Target.Options)
            .CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest()))
            .CreateAsync();

        // Two handles, two different providers, one vocabulary. Everything above provisioning receives a
        // ResourceHandle.Tags dictionary and must not need to know which stage produced it.
        foreach (var key in ServyxTagKeys.Canonical)
        {
            cloud.Handle.Tags[key].Should().Be(installed.Handle.Tags[key], $"both stages must agree on '{key}'");
        }

        cloud.Handle.ProvisionerId.Should().Be("digitalocean-droplet");
        installed.Handle.ProvisionerId.Should().Be("ssh-process");
    }

    [Fact]
    public void The_ssh_adapter_cannot_see_the_digitalocean_adapter_at_all()
    {
        // The structural half of "no cloud adapter contains install logic": the two assemblies cannot reference
        // each other, so neither can special-case the other. If the SSH code path needed one line of
        // DigitalOcean-specific handling, this reference would have to exist and this test would fail.
        typeof(SshProcessProvisioner).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => (a.Name ?? string.Empty).Contains("DigitalOcean", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_digitalocean_adapter_references_nothing_but_the_domain()
    {
        var referenced = typeof(DigitalOceanDropletProvisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Servyx", StringComparison.Ordinal))
            .ToList();

        // The other half: the cloud adapter cannot reach an installer even if someone wanted it to. It has no
        // reference to Servyx.Infrastructure.Ssh, so "shape I contains no install logic" is a fact about what
        // is reachable rather than a matter of discipline.
        referenced.Should().Equal("Servyx.Domain");
    }

    [Fact]
    public async Task The_cloud_adapter_never_opens_a_connection_to_the_machine_it_creates()
    {
        var scenario = new DigitalOceanScenario();

        await scenario.CreateAsync();

        // Every request it made went to the DigitalOcean control plane. Not one went to the droplet: shape I
        // does not log in to the machine, run a command on it, or upload anything to it.
        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.Uri.Host == "api.digitalocean.com");
        scenario.Api.Requests.Should().NotContain(r => r.Uri.Host.Contains(DigitalOceanScenario.PublicIp, StringComparison.Ordinal));
    }
}
