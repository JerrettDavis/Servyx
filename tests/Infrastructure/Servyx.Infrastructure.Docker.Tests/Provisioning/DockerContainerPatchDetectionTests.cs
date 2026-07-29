using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="DockerContainerProvisioner"/>'s <see cref="IPatchDetector"/> half — the answer
/// to "is a newer build of this server's image available?". Same house pattern as
/// <see cref="DockerContainerMaintenanceTests"/>: the engine is an NSubstitute-substituted
/// <see cref="IDockerClient"/>, so no daemon and no registry are involved and every engine call is
/// countable.
/// </summary>
/// <remarks>
/// The negative these exist for is the one the feature is most likely to get wrong: a check that could not
/// resolve anything reporting "up to date". <see cref="PatchCheckResult"/> makes that unconstructible, and
/// the tests below pin that the adapter actually routes every unresolvable case through the unknown door
/// rather than falling through to a comparison of two nulls.
/// </remarks>
public class DockerContainerPatchDetectionTests
{
    private const string Endpoint = "npipe://./pipe/dockerDesktopLinuxEngine";
    private const string TrackedImage = "thijsvanloef/palworld-server-docker:latest";
    private const string ContainerId = "container-1";
    private const string RunningDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewerDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly DateTimeOffset CheckedAt = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static (IDockerClient Client, IContainerOperations Containers, IImageOperations Images) SubstituteClient()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        var images = Substitute.For<IImageOperations>();
        client.Containers.Returns(containers);
        client.Images.Returns(images);
        client.ClearReceivedCalls();
        containers.ClearReceivedCalls();
        images.ClearReceivedCalls();
        return (client, containers, images);
    }

    /// <summary>The labels this provisioner stamps, i.e. what the ledger's handle holds.</summary>
    private static IReadOnlyDictionary<string, string> StampedLabels(string image = TrackedImage) =>
        ServyxResourceTags.For("srv-0001", "job-42", "docker-local").ToLabels(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServyxResourceTags.RootPathLabel] = "/palworld",
                [ServyxResourceTags.ImageLabel] = image,
            });

    private static ResourceHandle Handle(IReadOnlyDictionary<string, string>? tags = null) =>
        new("docker-container", ContainerId, null, tags ?? StampedLabels());

    /// <summary>
    /// A live container running <paramref name="runningDigest"/>, created from <paramref name="reference"/>.
    /// </summary>
    private static ContainerInspectResponse LiveContainer(
        string? reference = TrackedImage,
        string? runningDigest = RunningDigest,
        IReadOnlyDictionary<string, string>? labels = null) =>
        new()
        {
            ID = ContainerId,
            Name = "/palworld-server",
            Image = runningDigest ?? string.Empty,
            Config = new Config
            {
                Image = reference ?? string.Empty,
                Labels = new Dictionary<string, string>(labels ?? StampedLabels(), StringComparer.Ordinal),
            },
            State = new ContainerState { Status = "running", Running = true },
        };

    /// <summary>
    /// Builds a provisioner whose engine reports <paramref name="inspect"/> for the container and
    /// <paramref name="localDigest"/> for the tracked tag. A null <paramref name="localDigest"/> means the
    /// host's image store has never seen that reference.
    /// </summary>
    private static DockerContainerProvisioner ProvisionerOver(
        IDockerClient client,
        IContainerOperations containers,
        IImageOperations images,
        ContainerInspectResponse? inspect,
        string? localDigest,
        string reference = TrackedImage)
    {
        if (inspect is null)
        {
            containers
                .InspectContainerAsync(ContainerId, Arg.Any<CancellationToken>())
                .Returns<Task<ContainerInspectResponse>>(_ => throw new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container"));
        }
        else
        {
            containers.InspectContainerAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(inspect));
        }

        if (localDigest is null)
        {
            images
                .InspectImageAsync(reference, Arg.Any<CancellationToken>())
                .Returns<Task<ImageInspectResponse>>(_ => throw new DockerImageNotFoundException(HttpStatusCode.NotFound, "no such image"));
        }
        else
        {
            images
                .InspectImageAsync(reference, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ImageInspectResponse { ID = localDigest, RepoTags = [reference] }));
        }

        return new DockerContainerProvisioner(client, Endpoint, new FixedTimeProvider(CheckedAt));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── The answer itself ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_container_running_the_digest_its_tag_resolves_to_is_reported_up_to_date()
    {
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(client, containers, images, LiveContainer(), localDigest: RunningDigest);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.UpToDate);
        result.RunningDigest.Should().Be(RunningDigest);
        result.AvailableDigest.Should().Be(RunningDigest);
        result.ImageReference.Should().Be(TrackedImage);
        result.CheckedAt.Should().Be(CheckedAt);

        // The claim is scoped, and says so: "nothing newer has reached this host", not "nothing newer exists".
        result.Source.Should().Be(PatchAvailabilitySource.LocalImageStore);
        result.Summary.Should().Contain("up to date").And.Contain(nameof(PatchAvailabilitySource.LocalImageStore));
    }

    [Fact]
    public async Task A_tag_that_resolves_to_a_different_digest_reports_a_patch_available_and_names_both_digests()
    {
        // The realistic shape: `latest` was re-pulled on the host, so the local tag now points at a newer
        // image, and the container is still running the old one.
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(client, containers, images, LiveContainer(), localDigest: NewerDigest);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.PatchAvailable);
        result.RunningDigest.Should().Be(RunningDigest);
        result.AvailableDigest.Should().Be(NewerDigest);
        result.Summary.Should().Contain(RunningDigest).And.Contain(NewerDigest).And.Contain(TrackedImage);
    }

    // ── Unknown is unknown, never "up to date" ───────────────────────────────────────────────────

    [Fact]
    public async Task A_tag_absent_from_the_hosts_image_store_reports_unknown_rather_than_up_to_date()
    {
        // Nothing local resolves the reference, and resolving it would mean pulling — a write. The honest
        // answer is that the check does not know, in exactly the way CostConfidence.Unknown renders as
        // "unknown" rather than as a fabricated number.
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(client, containers, images, LiveContainer(), localDigest: null);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.Unknown);
        result.Status.Should().NotBe(PatchStatus.UpToDate);
        result.AvailableDigest.Should().BeNull();
        result.RunningDigest.Should().Be(RunningDigest, "what the check did establish is still reported");
        result.Reason.Should().Contain("does not pull");
        result.Summary.Should().Contain("unknown");
    }

    [Fact]
    public async Task A_container_the_engine_no_longer_knows_about_reports_unknown()
    {
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(client, containers, images, inspect: null, localDigest: RunningDigest);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.Unknown);
        result.Reason.Should().Contain("no longer knows");
        result.Summary.Should().Contain("unknown");
    }

    [Fact]
    public async Task A_container_whose_running_image_id_the_engine_does_not_report_is_unknown()
    {
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(client, containers, images, LiveContainer(runningDigest: null), localDigest: RunningDigest);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.Unknown);
        result.RunningDigest.Should().BeNull();
        result.AvailableDigest.Should().BeNull("a comparison that never had a left-hand side reports no right-hand side either");
    }

    [Fact]
    public void A_result_that_claims_a_definite_answer_cannot_be_built_without_both_digests()
    {
        // The invariant behind every test above: the type has no constructor that takes a status, so
        // "up to date" is not expressible without having produced the two digests it compares.
        var act = () => PatchCheckResult.Resolved(
            Handle(),
            PatchAvailabilitySource.LocalImageStore,
            TrackedImage,
            runningDigest: "   ",
            availableDigest: RunningDigest,
            CheckedAt);

        act.Should().Throw<ArgumentException>();
    }

    // ── Detection is read-only ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectPatchAsync_issues_two_reads_and_no_mutating_docker_call()
    {
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(client, containers, images, LiveContainer(), localDigest: NewerDigest);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.PatchAvailable);

        // The assertion is on the whole call log for both operation surfaces, so a mutating call added later
        // cannot slip past an enumerated list.
        containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
        images.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IImageOperations.InspectImageAsync));
        containers.ReceivedCalls().Should().HaveCount(1);
        images.ReceivedCalls().Should().HaveCount(1);

        // Named explicitly for the one that matters most: a pull would re-resolve the tag against the
        // registry and would also write layers to the host. Detection does not get to do that.
        await images.DidNotReceive().CreateImageAsync(
            Arg.Any<ImagesCreateParameters>(),
            Arg.Any<AuthConfig>(),
            Arg.Any<IProgress<JSONMessage>>(),
            Arg.Any<CancellationToken>());
        await images.DidNotReceive().TagImageAsync(Arg.Any<string>(), Arg.Any<ImageTagParameters>(), Arg.Any<CancellationToken>());
        await images.DidNotReceive().DeleteImageAsync(Arg.Any<string>(), Arg.Any<ImageDeleteParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().StopContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().RestartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRestartParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
    }

    // ── Containers Servyx did not provision ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_container_servyx_did_not_provision_is_refused_without_touching_the_engine()
    {
        // Refused, and refused *before* any engine call: Servyx cannot say what tag a container it did not
        // create is supposed to track, and the only available guess — read the tag off the container itself —
        // would make every unmanaged container report itself up to date with whatever it happens to run.
        // Reported as Unknown rather than as an exception because "is a patch available?" is a question with
        // an honest negative answer, and callers sweeping an inventory should not have to catch per resource.
        var (client, containers, images) = SubstituteClient();
        var unlabelled = new Dictionary<string, string>(StringComparer.Ordinal);
        var provisioner = ProvisionerOver(client, containers, images, LiveContainer(), localDigest: RunningDigest);

        var result = await provisioner.DetectPatchAsync(Handle(unlabelled));

        result.Status.Should().Be(PatchStatus.Unknown);
        result.Reason.Should().Contain("recorded no image reference");
        containers.ReceivedCalls().Should().BeEmpty();
        images.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Another_provisioners_handle_is_refused_without_touching_the_engine()
    {
        var (client, containers, images) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var result = await provisioner.DetectPatchAsync(
            new ResourceHandle("hetzner", "srv-77", "nbg1", new Dictionary<string, string>()));

        result.Status.Should().Be(PatchStatus.Unknown);
        result.Reason.Should().Contain("hetzner");
        containers.ReceivedCalls().Should().BeEmpty();
        images.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_container_running_a_different_reference_than_recorded_is_reported_as_drift_not_as_a_patch()
    {
        // The digests would compare perfectly well, and the comparison would be meaningless: the difference
        // is that someone recreated the container from another tag, which is drift. Calling it a patch would
        // invite an operator to "update" to a tag they had already deliberately moved away from.
        var (client, containers, images) = SubstituteClient();
        var provisioner = ProvisionerOver(
            client,
            containers,
            images,
            LiveContainer(reference: "thijsvanloef/palworld-server-docker:v1.2.3"),
            localDigest: NewerDigest);

        var result = await provisioner.DetectPatchAsync(Handle());

        result.Status.Should().Be(PatchStatus.Unknown);
        result.Reason.Should().Contain("drift").And.Contain("v1.2.3");
        images.ReceivedCalls().Should().BeEmpty("there is no point resolving a reference the container is not on");
    }

    // ── Wiring stays behind the same gate as creation ────────────────────────────────────────────

    [Fact]
    public void The_provisioner_is_reachable_as_a_patch_detector_naming_the_same_provisioner_id()
    {
        var (client, _, _) = SubstituteClient();

        IPatchDetector detector = new DockerContainerProvisioner(client);

        detector.ProvisionerId.Should().Be("docker-container");
    }

    [Fact]
    public void The_opt_in_provisioning_registration_publishes_the_patch_detector()
    {
        var services = new ServiceCollection();

        services.AddServyxDockerProvisioning(Endpoint);

        services.Should().Contain(d => d.ServiceType == typeof(IPatchDetector));
    }

    [Fact]
    public void A_composition_root_that_never_opts_in_has_no_patch_detector_at_all()
    {
        // Servyx:Provisioning:Enabled=false means Program.cs never calls AddServyxDockerProvisioning(), so
        // there is no object in the process that can be asked the patch question either.
        var services = new ServiceCollection();

        services.AddServyxDocker();

        services.Should().NotContain(d => d.ServiceType == typeof(IPatchDetector));
    }
}
