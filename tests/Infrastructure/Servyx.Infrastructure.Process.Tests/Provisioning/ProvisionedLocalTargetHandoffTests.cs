using System.Text;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// The local twin of the SSH and Docker handoff tests, and the proof of the same architectural claim for the
/// local shape: <em>a provisioner's job is finished when it hands back a <see cref="TargetDescriptor"/>; from
/// that point the existing transport machinery takes over unchanged.</em>
/// </summary>
/// <remarks>
/// This suite can go further than the SSH twin can. <c>SshTransport</c> constructs a real SSH client
/// internally, so its handoff test can only drive the descriptor as far as credential resolution.
/// <see cref="LocalProcessTransport"/> has nothing to dial, so the very descriptor the provisioner produced is
/// handed to the real transport and used, end to end, to write and read a file in the provisioned data
/// directory. If a mapping step were ever needed between <see cref="ProvisionedResource.Target"/> and the
/// transport, the claim would be false and the mapping step would be the evidence.
/// </remarks>
public class ProvisionedLocalTargetHandoffTests
{
    private sealed class Provisioned : IDisposable
    {
        private Provisioned(TempDirectory temp, RecordingLocalHost host, ProvisionedResource resource, string dataDirectory, string markerRoot)
        {
            Temp = temp;
            Host = host;
            Resource = resource;
            DataDirectory = dataDirectory;
            MarkerRoot = markerRoot;
        }

        internal TempDirectory Temp { get; }

        internal RecordingLocalHost Host { get; }

        internal ProvisionedResource Resource { get; }

        internal string DataDirectory { get; }

        internal string MarkerRoot { get; }

        internal LocalProcessProvisioner Provisioner(IReadOnlyDictionary<string, string>? transportOptions = null) =>
            new(Host, machineId: "test-machine", credentialUrn: "secret://connector/local-palworld/none", transportOptions: transportOptions, markerRoot: MarkerRoot);

        internal static async Task<Provisioned> CreateAsync(IReadOnlyDictionary<string, string>? transportOptions = null)
        {
            var temp = new TempDirectory("handoff");
            var host = new RecordingLocalHost();
            var dataDirectory = temp.At("palworld");
            var markerRoot = temp.At("instances");

            var provisioner = new LocalProcessProvisioner(
                host,
                machineId: "test-machine",
                credentialUrn: "secret://connector/local-palworld/none",
                transportOptions: transportOptions,
                markerRoot: markerRoot);

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instanceId"] = "srv-0001",
                ["jobId"] = "job-42",
                ["connectorId"] = "local-palworld",
                ["executable"] = "./PalServer.sh",
                ["dataDir"] = dataDirectory,
                ["install:0:verb"] = "steamcmd",
                ["install:0:appId"] = "2394010",
            };

            var request = new ProvisioningRequest("palworld", "native-steamcmd", ConnectorId: null, parameters);
            var resource = await provisioner.CreateOperation(LocalProcessProvisioner.BuildSpec(request)).CreateAsync();

            return new Provisioned(temp, host, resource, dataDirectory, markerRoot);
        }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task The_provisioned_target_names_the_transport_that_already_exists()
    {
        using var provisioned = await Provisioned.CreateAsync();

        provisioned.Resource.RequireTarget().TransportId.Should().Be("local");
        provisioned.Resource.RequireTarget().TransportId.Should().Be(new LocalProcessTransport().TransportId);
    }

    [Fact]
    public async Task A_registry_of_transports_resolves_the_provisioned_target_by_its_transport_id()
    {
        using var provisioned = await Provisioned.CreateAsync();

        var docker = Substitute.For<ITransport>();
        docker.TransportId.Returns("docker");
        var ssh = Substitute.For<ITransport>();
        ssh.TransportId.Returns("ssh");
        ITransport[] registered = [docker, ssh, new LocalProcessTransport()];

        var resolved = registered.Single(t =>
            string.Equals(t.TransportId, provisioned.Resource.RequireTarget().TransportId, StringComparison.Ordinal));

        resolved.Should().BeOfType<LocalProcessTransport>();
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.ExecuteCommand);
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.FileWrite);
    }

    [Fact]
    public async Task The_real_local_transport_probes_the_provisioned_target_with_no_translation()
    {
        using var provisioned = await Provisioned.CreateAsync();

        // The descriptor is passed straight through — no adapter, no copy, no field fix-up.
        var health = await new LocalProcessTransport().ProbeAsync(provisioned.Resource.RequireTarget());

        health.Reachable.Should().BeTrue();
        health.Detail.Should().Contain(provisioned.DataDirectory);
    }

    [Fact]
    public async Task The_real_local_transport_connects_to_the_provisioned_target_and_reads_back_what_it_writes()
    {
        // The full round trip, and the thing the SSH twin cannot do without a live host: provision, then use
        // the produced descriptor with the production transport to write a config file into the data
        // directory the provisioner reported.
        using var provisioned = await Provisioned.CreateAsync();

        await using var session = await new LocalProcessTransport().ConnectAsync(provisioned.Resource.RequireTarget());

        var path = ((LocalExecutionTarget)session).Resolve("server.cfg");
        await using (var content = new MemoryStream(Encoding.UTF8.GetBytes("port=8211"), writable: false))
        {
            await session.WriteFileAsync(path, content, new FileWriteOptions(null));
        }

        File.Exists(Path.Combine(provisioned.DataDirectory, "server.cfg")).Should().BeTrue();

        await using var read = await session.OpenReadAsync(path);
        using var reader = new StreamReader(read);
        (await reader.ReadToEndAsync()).Should().Be("port=8211");
    }

    [Fact]
    public async Task The_provisioned_target_is_sandboxed_to_the_data_directory_the_provisioner_reported()
    {
        // The handoff carries the sandbox with it: a session opened from the provisioned descriptor cannot
        // reach outside the data directory, without the caller having to configure anything.
        using var provisioned = await Provisioned.CreateAsync();

        await using var session = await new LocalProcessTransport().ConnectAsync(provisioned.Resource.RequireTarget());

        var act = () => ((LocalExecutionTarget)session).Resolve("../instances/srv-0001.servyx.json");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public async Task The_transports_own_option_conventions_read_the_provisioned_target_unaided()
    {
        using var provisioned = await Provisioned.CreateAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operatorNote"] = "provisioned by the local adapter",
        });

        // rootPath is exactly the key LocalProcessTransport.ResolveRootPath already reads; the provisioner
        // invents no option of its own beyond it, and caller options survive untouched.
        provisioned.Resource.RequireTarget().Options["rootPath"].Should().Be(provisioned.DataDirectory);
        provisioned.Resource.RequireTarget().Options["operatorNote"].Should().Be("provisioned by the local adapter");
        provisioned.Resource.RequireTarget().CredentialUrn.Should().Be("secret://connector/local-palworld/none");
        provisioned.Resource.RequireTarget().DockerContext.Should().BeNull();
        provisioned.Resource.RequireTarget().Endpoint.Should().Be("local://test-machine");

        LocalProcessTransport.ResolveRootPath(provisioned.Resource.RequireTarget()).Should().Be(provisioned.DataDirectory);
    }

    [Fact]
    public async Task The_provisioner_connects_with_exactly_the_descriptor_it_hands_back()
    {
        // The invariant the Docker provisioning registration bug violated, stated structurally rather than by
        // comparison: the descriptor used to run the install is the very object handed to the ledger, so the
        // directory installed into and the directory recorded cannot be different directories.
        using var provisioned = await Provisioned.CreateAsync();

        provisioned.Host.Connected.Should().Contain(d => ReferenceEquals(d, provisioned.Resource.RequireTarget()));
    }

    [Fact]
    public async Task A_refreshed_target_is_identical_to_the_one_handed_over_at_creation()
    {
        using var provisioned = await Provisioned.CreateAsync();

        var refreshed = await provisioned.Provisioner().RefreshAsync(provisioned.Resource.Handle);

        refreshed.Should().NotBeNull();

        // Compared field by field rather than with record equality: TargetDescriptor's Options is an
        // IReadOnlyDictionary, which the compiler-generated record Equals compares by reference — the same
        // pre-existing defect the Docker and SSH handoff tests pin.
        refreshed!.RequireTarget().TransportId.Should().Be(provisioned.Resource.RequireTarget().TransportId);
        refreshed.RequireTarget().Endpoint.Should().Be(provisioned.Resource.RequireTarget().Endpoint);
        refreshed.RequireTarget().CredentialUrn.Should().Be(provisioned.Resource.RequireTarget().CredentialUrn);
        refreshed.RequireTarget().DockerContext.Should().Be(provisioned.Resource.RequireTarget().DockerContext);
        refreshed.RequireTarget().Options.Should().BeEquivalentTo(provisioned.Resource.RequireTarget().Options);
    }
}
