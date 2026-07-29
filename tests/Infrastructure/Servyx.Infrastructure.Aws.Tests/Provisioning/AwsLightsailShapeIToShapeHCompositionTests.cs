using NSubstitute;

using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The point of the whole adapter, asked of a fourth Shape I: <c>docs/provisioning.md</c> §2 claims shape I
/// produces a <em>host</em>, not a game server, so a cloud deployment is a two-stage plan in which shape H then
/// runs against that connector "identically to any bare-metal SSH box".
/// </summary>
/// <remarks>
/// A near-transcription of <c>AwsShapeIToShapeHCompositionTests</c> (EC2) and the DigitalOcean/Azure suites it
/// in turn transcribes, with the provider name and the username source changed. What repeats a fourth time is
/// the architectural seam, not the adapter behind it: Lightsail's identity is caller-chosen, its protocol is
/// JSON rather than EC2's Query/XML, and its login username comes from the provider's own answer rather than a
/// constructor default - and none of that is visible in the descriptor shape H consumes.
/// </remarks>
public class AwsLightsailShapeIToShapeHCompositionTests
{
    private static SshTransport RealSshTransport() =>
        new(Substitute.For<ISecretStore>(), Substitute.For<IHostKeyVerifier>());

    private static ProvisioningRequest PalworldInstallRequest() =>
        new(
            "palworld",
            "native-steamcmd",
            LightsailScenario.ConnectorId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instanceId"] = LightsailScenario.InstanceId,
                ["jobId"] = LightsailScenario.JobId,
                ["connectorId"] = LightsailScenario.ConnectorId,
                ["dataDir"] = "/opt/palworld",
                ["executable"] = "PalServer.sh",
            });

    [Fact]
    public async Task The_cloud_adapter_produces_an_ordinary_ssh_target_not_a_lightsail_specific_one()
    {
        var resource = await new LightsailScenario().CreateAsync();

        resource.RequireTarget().TransportId.Should().Be("ssh");
        resource.RequireTarget().TransportId.Should().Be(RealSshTransport().TransportId);
    }

    [Fact]
    public async Task The_endpoint_is_exactly_what_the_ssh_endpoint_parser_reads()
    {
        var resource = await new LightsailScenario().CreateAsync();

        var (endpoint, username) = SshEndpoint.Parse(resource.RequireTarget().Endpoint);

        resource.RequireTarget().Endpoint.Should().Be($"ssh://{LightsailScenario.Username}@{LightsailScenario.PublicIp}:22");
        endpoint.Host.Should().Be(LightsailScenario.PublicIp);
        endpoint.Port.Should().Be(22);

        // Unlike the other three adapters' constructor-supplied usernames, this one came off the wire - proof
        // that the composition holds even when the value is not known until the create call completes.
        username.Should().Be(LightsailScenario.Username);
    }

    [Fact]
    public async Task The_descriptor_carries_no_lightsail_specific_field_at_all()
    {
        var resource = await new LightsailScenario().CreateAsync();

        resource.RequireTarget().Options.Keys.Should().BeEquivalentTo(["trustPolicy", "declaredChannels", "rootPath"]);
        resource.RequireTarget().DockerContext.Should().BeNull();
        resource.RequireTarget().CredentialUrn.Should().Be(LightsailScenario.SshCredentialUrn);
        resource.RequireTarget().Options["rootPath"].Should().Be("/", "shape I hands back a host, which has no per-server data directory");

        var rendered = string.Join(
            "|",
            resource.RequireTarget().Options.Select(o => $"{o.Key}={o.Value}").Append(resource.RequireTarget().Endpoint));

        foreach (var vocabulary in new[] { "bundleId", "blueprintId", "AWS4", "amazonaws", "aws-lightsail", "availabilityZone" })
        {
            rendered.Should().NotContain(vocabulary, "the descriptor must not leak the provider's vocabulary downstream");
        }
    }

    [Fact]
    public async Task The_real_ssh_transport_consumes_the_cloud_adapters_descriptor_with_no_translation()
    {
        var resource = await new LightsailScenario().CreateAsync();

        var health = await RealSshTransport().ProbeAsync(resource.RequireTarget());

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("neither a 'password' nor a 'private-key'");
        health.Detail.Should().NotContain("does not contain a host");
    }

    [Fact]
    public async Task A_registry_of_transports_resolves_the_cloud_targets_transport_by_id()
    {
        var resource = await new LightsailScenario().CreateAsync();

        var docker = Substitute.For<ITransport>();
        docker.TransportId.Returns("docker");
        ITransport[] registered = [docker, RealSshTransport()];

        var resolved = registered.Single(t => string.Equals(t.TransportId, resource.RequireTarget().TransportId, StringComparison.Ordinal));

        resolved.Should().BeOfType<SshTransport>();
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.ExecuteCommand);
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.FileWrite);
    }

    [Fact]
    public async Task The_ssh_install_adapter_takes_the_cloud_adapters_output_and_installs_onto_it_unaided()
    {
        var cloud = await new LightsailScenario().CreateAsync();
        var host = new SshHostDouble();

        var installer = new SshProcessProvisioner(
            host.Transport,
            endpoint: cloud.RequireTarget().Endpoint,
            credentialUrn: cloud.RequireTarget().CredentialUrn,
            transportOptions: cloud.RequireTarget().Options);

        var installed = await installer
            .CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest()))
            .CreateAsync();

        installed.RequireTarget().TransportId.Should().Be(cloud.RequireTarget().TransportId);
        installed.RequireTarget().Endpoint.Should().Be(cloud.RequireTarget().Endpoint);
        installed.RequireTarget().CredentialUrn.Should().Be(cloud.RequireTarget().CredentialUrn);
        installed.RequireTarget().DockerContext.Should().Be(cloud.RequireTarget().DockerContext);
        installed.RequireTarget().Options["trustPolicy"].Should().Be(cloud.RequireTarget().Options["trustPolicy"]);
        installed.RequireTarget().Options["declaredChannels"].Should().Be(cloud.RequireTarget().Options["declaredChannels"]);

        installed.RequireTarget().Options["rootPath"].Should().Be("/opt/palworld");
        cloud.RequireTarget().Options["rootPath"].Should().Be("/");
    }

    [Fact]
    public async Task The_ssh_adapter_connects_to_the_machine_the_cloud_adapter_created()
    {
        var cloud = await new LightsailScenario().CreateAsync();
        var host = new SshHostDouble();

        var installer = new SshProcessProvisioner(
            host.Transport,
            endpoint: cloud.RequireTarget().Endpoint,
            credentialUrn: cloud.RequireTarget().CredentialUrn,
            transportOptions: cloud.RequireTarget().Options);

        await installer.CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest())).CreateAsync();

        host.Connected.Should().NotBeEmpty();
        host.Connected.Should().OnlyContain(d => d.Endpoint == cloud.RequireTarget().Endpoint);
        SshEndpoint.Parse(host.Connected[0].Endpoint).Endpoint.Host.Should().Be(LightsailScenario.PublicIp);
    }

    [Fact]
    public async Task Both_stages_stamp_the_same_servyx_identity_so_one_sweep_understands_the_others_handles()
    {
        var cloud = await new LightsailScenario().CreateAsync();
        var host = new SshHostDouble();

        var installed = await new SshProcessProvisioner(
                host.Transport,
                endpoint: cloud.RequireTarget().Endpoint,
                credentialUrn: cloud.RequireTarget().CredentialUrn,
                transportOptions: cloud.RequireTarget().Options)
            .CreateOperation(SshProcessProvisioner.BuildSpec(PalworldInstallRequest()))
            .CreateAsync();

        foreach (var key in ServyxTagKeys.Canonical)
        {
            cloud.Handle.Tags[key].Should().Be(installed.Handle.Tags[key], $"both stages must agree on '{key}'");
        }

        cloud.Handle.ProvisionerId.Should().Be("aws-lightsail");
        installed.Handle.ProvisionerId.Should().Be("ssh-process");
    }

    [Fact]
    public void The_ssh_adapter_cannot_see_the_aws_adapter_at_all()
    {
        typeof(SshProcessProvisioner).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => (a.Name ?? string.Empty).Contains("Aws", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_aws_adapter_references_nothing_but_the_domain()
    {
        var referenced = typeof(AwsLightsailProvisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Servyx", StringComparison.Ordinal))
            .ToList();

        // Confirms the fourth Shape I adapter did not need a fifth project, and did not smuggle in a reference
        // to the SSH install adapter or either of the two existing cloud adapters to get there.
        referenced.Should().Equal("Servyx.Domain");
    }

    [Fact]
    public async Task The_cloud_adapter_never_opens_a_connection_to_the_machine_it_creates()
    {
        var scenario = new LightsailScenario();

        await scenario.CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.IsLightsail);
        scenario.Api.Requests.Should().NotContain(r => r.Uri.Host.Contains(LightsailScenario.PublicIp, StringComparison.Ordinal));
    }
}
