using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Provisioning;
using Xunit;

namespace Servyx.Infrastructure.Docker.Tests.Integration;

/// <summary>
/// The one thing a substituted <see cref="IDockerClient"/> cannot tell us: whether a write into a container
/// that has been <em>created and never started</em> actually lands.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because the bug it guards against passed every unit test in this project. Seeding was
/// written against a fake <see cref="IExecutionTarget"/>, so nothing noticed that the real one finalized its
/// write with a <c>docker exec</c> — and <c>docker exec</c> starts a process <em>inside</em> a running
/// container, which a just-created one is not. The mode step had the same defect for the same reason. Both
/// are asserted here against a real daemon, where the distinction is real.
/// </para>
/// <para>
/// <strong>Opt-in, and never run by default.</strong> Every test here carries
/// <c>[Trait("Category", "Integration")]</c>, and this project's <c>VSTestTestCaseFilter</c> defaults to
/// <c>Category!=Integration</c> — the same mechanism <c>Servyx.Infrastructure.Ssh.Tests</c> uses, documented
/// in <c>docs/testing.md</c>. A bare <c>dotnet test</c> (including CI's) filters these out at discovery, so
/// no container is created and no image is pulled. Run them deliberately with:
/// <c>dotnet test tests/Infrastructure/Servyx.Infrastructure.Docker.Tests --filter "Category=Integration"</c>.
/// Each is additionally a <c>[SkippableFact]</c> guarded by a daemon ping, so opting in on a machine with no
/// Docker skips with a reason rather than failing.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PreStartSeedingIntegrationTests
{
    /// <summary>A tiny, universally available base image. Nothing in these tests runs it — the container is
    /// created and then only ever addressed through the daemon's archive endpoint.</summary>
    private const string Image = "alpine";

    private const string ImageTag = "3.20";

    private const string SecretContent = "a-real-credential-9f3b2c";

    /// <summary>
    /// Creates a container from <see cref="Image"/> and leaves it in the <c>created</c> state, returning its
    /// id together with the client. The caller removes it.
    /// </summary>
    private static async Task<(IDockerClient Client, string ContainerId)> CreateNeverStartedContainerAsync()
    {
        var client = new DockerClientConfiguration().CreateClient();

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = Image, Tag = ImageTag },
            null,
            new Progress<JSONMessage>());

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{Image}:{ImageTag}",
            Name = $"servyx-preseed-{Guid.NewGuid():N}",
            Cmd = ["sleep", "3600"],
        });

        return (client, created.ID);
    }

    private static async Task RemoveQuietlyAsync(IDockerClient client, string containerId)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch (DockerApiException)
        {
            // The test is already reporting whatever went wrong; a cleanup failure must not replace it.
        }
    }

    private static async Task SkipUnlessDockerIsReachableAsync()
    {
        try
        {
            using var probe = new DockerClientConfiguration().CreateClient();
            await probe.System.PingAsync();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"No reachable Docker daemon: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [SkippableFact]
    public async Task Seeding_lands_with_its_declared_mode_on_a_container_that_has_never_been_started()
    {
        await SkipUnlessDockerIsReachableAsync();

        var (client, containerId) = await CreateNeverStartedContainerAsync();
        try
        {
            // The state that matters: created, never started. If this ever reads "running", the test below
            // proves nothing, because an exec would work.
            var inspect = await client.Containers.InspectContainerAsync(containerId);
            inspect.State.Status.Should().Be("created");

            var target = new DockerExecutionTarget(
                client, containerId, containerRootPath: "/etc", ownsClient: false, writeMode: WriteMode.Enabled);
            await using var session = new WriteGuardedExecutionTarget(target, WriteMode.Enabled, containerId);

            var path = new SandboxedPathResolver("/etc").Resolve("servyx-seed-credential");
            var file = new SeededFile(path, Encoding.UTF8.GetBytes(SecretContent), mode: "0600", isSensitive: true);

            // createOnly's pre-check has to work here too — it is an archive stat, not an exec.
            (await session.ExistsAsync(path)).Should().BeFalse();

            var outcomes = await DeployedFileSeeder.SeedAsync(session, [file], "/etc");

            outcomes.Should().ContainSingle().Which.Action.Should().Be(SeededFileAction.Written);

            var stat = await session.StatAsync(path);
            stat.Exists.Should().BeTrue();
            stat.Mode.Should().Be(0x180, "the declared 0600 rides in the tar header, applied by the daemon");

            await using var readBack = await session.OpenReadAsync(path);
            using var buffer = new MemoryStream();
            await readBack.CopyToAsync(buffer);
            Encoding.UTF8.GetString(buffer.ToArray()).Should().Be(SecretContent);

            // And a second seed of the same createOnly file leaves it alone.
            (await DeployedFileSeeder.SeedAsync(session, [file], "/etc"))
                .Should().ContainSingle().Which.Action.Should().Be(SeededFileAction.SkippedBecauseItAlreadyExists);
        }
        finally
        {
            await RemoveQuietlyAsync(client, containerId);
            client.Dispose();
        }
    }

    [SkippableFact]
    public async Task The_atomic_strategy_is_the_one_that_genuinely_cannot_run_before_the_first_start()
    {
        // The regression this whole change exists for, stated as an assertion rather than a comment: the
        // default stage-and-rename write fails against a created-but-not-started container, because its
        // final `mv` is an exec. That is why DirectPlacement is a declared strategy and not an optimisation.
        await SkipUnlessDockerIsReachableAsync();

        var (client, containerId) = await CreateNeverStartedContainerAsync();
        try
        {
            var target = new DockerExecutionTarget(
                client, containerId, containerRootPath: "/etc", ownsClient: false, writeMode: WriteMode.Enabled);
            await using var session = (IExecutionTarget)target;

            var path = new SandboxedPathResolver("/etc").Resolve("servyx-seed-atomic");
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(SecretContent), writable: false);

            var act = async () => await session.WriteFileAsync(path, content, new FileWriteOptions(null));

            await act.Should().ThrowAsync<DockerApiException>();
        }
        finally
        {
            await RemoveQuietlyAsync(client, containerId);
            client.Dispose();
        }
    }
}
