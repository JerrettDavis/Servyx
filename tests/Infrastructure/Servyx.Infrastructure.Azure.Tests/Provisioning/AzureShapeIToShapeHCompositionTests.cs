using NSubstitute;

using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The point of the whole adapter, asked of a second cloud: <c>docs/provisioning.md</c> §2 claims shape I
/// produces a <em>host</em>, not a game server, so a cloud deployment is a two-stage plan in which shape H then
/// runs against that connector "identically to any bare-metal SSH box" and "no cloud adapter contains install
/// logic".
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the half of the "mechanical repeat" claim that holds.</strong> Everything below is a
/// near-exact transcription of <c>ShapeIToShapeHCompositionTests</c> in the DigitalOcean suite, with the
/// provider name and the username changed — and it passes for the same reasons. Whatever else diverges between
/// the two clouds (authentication, resource count, teardown ordering, price composition), the <em>output</em>
/// of shape I genuinely does not: both hand back an ordinary <c>ssh://user@host:22</c> descriptor that shape H
/// consumes with no translation and no branch on which cloud produced it. The architectural seam is the part
/// that repeated; the adapter behind it is the part that did not.
/// </para>
/// <para>
/// <strong>What these tests can and cannot prove, stated up front</strong> — unchanged from the DigitalOcean
/// suite, because the limitation is in <c>SshProcessProvisioner</c>'s constructor rather than in either cloud
/// adapter. That constructor takes an endpoint string, a credential URN and an options dictionary, not a
/// <see cref="TargetDescriptor"/>, so the shape I → shape H hand-off is <strong>parameter-passing, not object
/// hand-off</strong>. These tests therefore prove the weaker but true claim — <em>data preservation</em>.
/// <c>SshTransport</c> does take a <see cref="TargetDescriptor"/>, so the object-identity form of the claim is
/// available for the transport half and is asserted below.
/// </para>
/// </remarks>
public class AzureShapeIToShapeHCompositionTests
{
    private static SshTransport RealSshTransport() =>
        new(Substitute.For<ISecretStore>(), Substitute.For<IHostKeyVerifier>());

    private static ProvisioningRequest PalworldInstallRequest() =>
        new(
            "palworld",
            "native-steamcmd",
            AzureScenario.ConnectorId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instanceId"] = AzureScenario.InstanceId,
                ["jobId"] = AzureScenario.JobId,
                ["connectorId"] = AzureScenario.ConnectorId,
                ["dataDir"] = "/opt/palworld",
                ["executable"] = "PalServer.sh",
            });

    [Fact]
    public async Task The_cloud_adapter_produces_an_ordinary_ssh_target_not_an_azure_specific_one()
    {
        var resource = await new AzureScenario().CreateAsync();

        // Not "azure", not "azure-ssh" - the transport that already existed before this adapter did, asserted
        // against the real transport's own property so the magic string cannot drift.
        resource.Target.TransportId.Should().Be("ssh");
        resource.Target.TransportId.Should().Be(RealSshTransport().TransportId);
    }

    [Fact]
    public async Task The_endpoint_is_exactly_what_the_ssh_endpoint_parser_reads()
    {
        var resource = await new AzureScenario().CreateAsync();

        // SshEndpoint.Parse is literally the first thing SshConnector.OpenAsync does with a descriptor's
        // endpoint, so agreeing with it is agreeing with the transport.
        var (endpoint, username) = SshEndpoint.Parse(resource.Target.Endpoint);

        resource.Target.Endpoint.Should().Be($"ssh://azureuser@{AzureScenario.PublicIp}:22");
        endpoint.Host.Should().Be(AzureScenario.PublicIp);
        endpoint.Port.Should().Be(22);

        // The one visible difference from the DigitalOcean descriptor, and it is a provider constraint rather
        // than a choice: ARM refuses 'root' as a Linux adminUsername.
        username.Should().Be("azureuser");
    }

    [Fact]
    public async Task The_descriptor_carries_no_azure_specific_field_at_all()
    {
        var resource = await new AzureScenario().CreateAsync();

        // Everything on it is either a key the SSH transport already read before this adapter existed, or the
        // caller's own pass-through option. There is no subscription, no resource group, no ARM id, no NIC -
        // those live on the ResourceHandle, which is where provider-specific state belongs.
        resource.Target.Options.Keys.Should().BeEquivalentTo(["trustPolicy", "declaredChannels", "rootPath"]);
        resource.Target.DockerContext.Should().BeNull();
        resource.Target.CredentialUrn.Should().Be(AzureScenario.SshCredentialUrn);
        resource.Target.Options["rootPath"].Should().Be("/", "shape I hands back a host, which has no per-server data directory");

        var rendered = string.Join(
            "|",
            resource.Target.Options.Select(o => $"{o.Key}={o.Value}").Append(resource.Target.Endpoint));

        foreach (var vocabulary in new[] { "subscription", "resourceGroup", "Microsoft.Compute", "networkInterface" })
        {
            rendered.Should().NotContain(vocabulary, "the descriptor must not leak the provider's vocabulary downstream");
        }
    }

    [Fact]
    public async Task The_real_ssh_transport_consumes_the_cloud_adapters_descriptor_with_no_translation()
    {
        var resource = await new AzureScenario().CreateAsync();

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
        var resource = await new AzureScenario().CreateAsync();

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
        var cloud = await new AzureScenario().CreateAsync();
        var host = new SshHostDouble();

        // THE COMPOSITION. Every argument below is read straight off the descriptor shape I handed back. There
        // is no mapping function, no Azure type, and no branch on "is this a cloud host" anywhere - and, the
        // point of doing this twice, no branch on WHICH cloud either.
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
        var cloud = await new AzureScenario().CreateAsync();
        var host = new SshHostDouble();

        var installer = new SshProcessProvisioner(
            host.Transport,
            endpoint: cloud.Target.Endpoint,
            credentialUrn: cloud.Target.CredentialUrn,
            transportOptions: cloud.Target.Options);

        await installer.CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest())).CreateAsync();

        host.Connected.Should().NotBeEmpty();
        host.Connected.Should().OnlyContain(d => d.Endpoint == cloud.Target.Endpoint);
        SshEndpoint.Parse(host.Connected[0].Endpoint).Endpoint.Host.Should().Be(AzureScenario.PublicIp);
    }

    [Fact]
    public async Task Both_stages_stamp_the_same_servyx_identity_so_one_sweep_understands_the_others_handles()
    {
        var cloud = await new AzureScenario().CreateAsync();
        var host = new SshHostDouble();

        var installed = await new SshProcessProvisioner(
                host.Transport,
                endpoint: cloud.Target.Endpoint,
                credentialUrn: cloud.Target.CredentialUrn,
                transportOptions: cloud.Target.Options)
            .CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest()))
            .CreateAsync();

        // Two handles, two different providers, one vocabulary. Everything above provisioning receives a
        // ResourceHandle.Tags dictionary and must not need to know which stage - or which cloud - produced it.
        foreach (var key in ServyxTagKeys.Canonical)
        {
            cloud.Handle.Tags[key].Should().Be(installed.Handle.Tags[key], $"both stages must agree on '{key}'");
        }

        cloud.Handle.ProvisionerId.Should().Be("azure-vm");
        installed.Handle.ProvisionerId.Should().Be("ssh-process");
    }

    [Fact]
    public void The_ssh_adapter_cannot_see_the_azure_adapter_at_all()
    {
        // The structural half of "no cloud adapter contains install logic": the two assemblies cannot reference
        // each other, so neither can special-case the other. If the SSH code path needed one line of
        // Azure-specific handling, this reference would have to exist and this test would fail.
        typeof(SshProcessProvisioner).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => (a.Name ?? string.Empty).Contains("Azure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_azure_adapter_references_nothing_but_the_domain()
    {
        var referenced = typeof(AzureVirtualMachineProvisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Servyx", StringComparison.Ordinal))
            .ToList();

        // The other half: the cloud adapter cannot reach an installer even if someone wanted it to. It has no
        // reference to Servyx.Infrastructure.Ssh, and none to Servyx.Infrastructure.DigitalOcean either - so
        // the second cloud adapter could not have shared code with the first even where they agree.
        referenced.Should().Equal("Servyx.Domain");
    }

    [Fact]
    public async Task The_cloud_adapter_never_opens_a_connection_to_the_machine_it_creates()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        // Every request it made went to an Azure control plane - two of them, which is itself the finding. Not
        // one went to the VM: shape I does not log in to the machine, run a command on it, or upload anything.
        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.IsArm || r.IsTokenExchange);
        scenario.Api.Requests.Should().NotContain(r => r.Uri.Host.Contains(AzureScenario.PublicIp, StringComparison.Ordinal));
    }
}
