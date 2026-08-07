using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Covers the provisioning half of deployment-seeded files: where in
/// <see cref="DockerContainerProvisioner"/>'s create sequence the bytes are written, and through what.
/// </summary>
/// <remarks>
/// Two things are being pinned here, and both are ordering/routing claims rather than behavioural ones.
/// First, seeding happens between <c>CreateContainerAsync</c> and <c>StartContainerAsync</c>: a file written
/// after the start is useless for the case the feature exists for, since the workload will already have
/// generated its own. Second, the bytes go out through the transport's guarded session and not through the
/// provisioner's own <see cref="IDockerClient"/>, which it holds and could trivially use — a write through
/// that private door would reach a read-only server.
/// </remarks>
public class DockerContainerSeedingTests
{
    private const string Endpoint = "npipe://./pipe/dockerDesktopLinuxEngine";
    private const string ContainerId = "container-1";
    private const string RootPath = "/data";
    private const string SecretContent = "a-real-rcon-password-9f3b2c";

    private static (IDockerClient Client, IContainerOperations Containers) SubstituteClient()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = ContainerId }));
        return (client, containers);
    }

    private static DockerContainerSpec SpecWithSeededFile() =>
        new("example/image:latest", "seeded-server", ServyxResourceTags.For("srv-0001", "job-42", "docker-local"))
        {
            RootPath = RootPath,
            SeededFiles =
            [
                new SeededFile(
                    new SandboxedPathResolver(RootPath).Resolve("config/credential"),
                    Encoding.UTF8.GetBytes(SecretContent),
                    isSensitive: true),
            ],
        };

    private static (ITransport Transport, IExecutionTarget Inner) GuardedTransport(WriteMode mode)
    {
        var inner = Substitute.For<IExecutionTarget>();
        inner.ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        inner.WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileWriteReceipt(null, "post", DateTimeOffset.UnixEpoch)));
        inner.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)));

        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IExecutionTarget>(new WriteGuardedExecutionTarget(inner, mode, "seeded-server")));

        return (transport, inner);
    }

    [Fact]
    public async Task Declared_files_are_written_after_the_container_is_created_and_before_it_is_started()
    {
        var (client, containers) = SubstituteClient();
        var (transport, inner) = GuardedTransport(WriteMode.Enabled);
        var provisioner = new DockerContainerProvisioner(client, Endpoint, transport: transport);

        await provisioner.CreateOperation(SpecWithSeededFile()).CreateAsync();

        Received.InOrder(() =>
        {
            containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>());
            inner.WriteFileAsync(
                Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
            containers.StartContainerAsync(ContainerId, Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task The_write_the_provisioner_issues_is_one_a_container_that_has_never_started_can_serve()
    {
        // Between create and start there is no process inside the container, so a stage-and-rename write's
        // final rename — and a follow-up chmod — have nowhere to run. The provisioner therefore issues a
        // direct placement carrying the declared mode, which the Docker transport serves entirely through
        // the daemon's archive endpoint.
        var (client, _) = SubstituteClient();
        var (transport, inner) = GuardedTransport(WriteMode.Enabled);
        var provisioner = new DockerContainerProvisioner(client, Endpoint, transport: transport);

        FileWriteOptions? options = null;
        inner.WriteFileAsync(
                Arg.Any<TargetPath>(),
                Arg.Any<Stream>(),
                Arg.Do<FileWriteOptions>(o => options = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileWriteReceipt(null, "post", DateTimeOffset.UnixEpoch)));

        await provisioner.CreateOperation(SpecWithSeededFile()).CreateAsync();

        options.Should().NotBeNull();
        options!.Strategy.Should().Be(FileWriteStrategy.DirectPlacement);
        options.Mode.Should().Be(0x180, "the seeded file declares the default 0600");
        await inner.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task Seeding_a_file_onto_a_non_writable_server_is_refused_and_the_container_is_never_started(WriteMode mode)
    {
        var (client, containers) = SubstituteClient();
        var (transport, inner) = GuardedTransport(mode);
        var provisioner = new DockerContainerProvisioner(client, Endpoint, transport: transport);

        var act = async () => await provisioner.CreateOperation(SpecWithSeededFile()).CreateAsync();

        await act.Should().ThrowAsync<WritesDisabledException>();
        await inner.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().StartContainerAsync(
            Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_spec_that_declares_files_with_no_transport_to_write_them_through_is_refused_rather_than_seeded_another_way()
    {
        // The provisioner holds an IDockerClient and could place the archive itself. Falling back to that
        // "because nothing else is wired up" is precisely how the write guard stops being one.
        var (client, containers) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var act = async () => await provisioner.CreateOperation(SpecWithSeededFile()).CreateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        await containers.DidNotReceive().StartContainerAsync(
            Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_spec_that_declares_no_files_needs_no_transport_and_connects_to_none()
    {
        var (client, containers) = SubstituteClient();
        var (transport, _) = GuardedTransport(WriteMode.Enabled);
        var spec = SpecWithSeededFile() with { SeededFiles = [] };
        var provisioner = new DockerContainerProvisioner(client, Endpoint, transport: transport);

        await provisioner.CreateOperation(spec).CreateAsync();

        await transport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
        await containers.Received(1).StartContainerAsync(
            ContainerId, Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_plan_describes_seeding_before_the_start_and_never_names_the_content()
    {
        var (client, _) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var plan = await Task.FromResult(provisioner.BuildPlan(SpecWithSeededFile()));

        plan.Stages.Select(s => s.StageId).Should().Equal("create-container", "publish-ports", "seed-files", "start-container");
        plan.Stages.Single(s => s.StageId == "seed-files").Description
            .Should().Contain("config/credential").And.Contain(SeededFile.Mask).And.NotContain(SecretContent);
    }

    [Fact]
    public async Task A_plan_hash_reflects_which_files_are_seeded_but_is_not_an_oracle_for_their_content()
    {
        var (client, _) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var spec = SpecWithSeededFile();
        var withoutFiles = spec with { SeededFiles = [] };
        var sameLengthDifferentContent = spec with
        {
            SeededFiles =
            [
                new SeededFile(
                    new SandboxedPathResolver(RootPath).Resolve("config/credential"),
                    Encoding.UTF8.GetBytes(new string('x', SecretContent.Length)),
                    isSensitive: true),
            ],
        };

        var baseline = provisioner.BuildPlan(spec).PlanHash;

        provisioner.BuildPlan(withoutFiles).PlanHash.Should().NotBe(baseline);
        provisioner.BuildPlan(sameLengthDifferentContent).PlanHash.Should().Be(
            baseline,
            "the hash covers a seeded file's path, mode and size but never its bytes, so rotating a secret "
            + "does not change the id of a plan that describes the same operation");

        await Task.CompletedTask;
    }
}
