using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// End-to-end smoke test proving <see cref="DisposableWorkloadContainer"/> actually starts a real container
/// on the local Docker daemon, reports real state through an independent Docker client, and is genuinely
/// removed on disposal. Mirrors <c>SshDockerIntegrationTests</c>'s Docker-availability probe pattern: a
/// failed <see cref="DisposableWorkloadContainer.StartAsync"/> during <see cref="InitializeAsync"/> is
/// treated as "Docker unavailable" rather than a test failure, and every test skips cleanly via
/// <see cref="SkippableFact"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DisposableWorkloadContainerTests : IAsyncLifetime
{
    private DisposableWorkloadContainer? _container;
    private bool _dockerAvailable;
    private string? _unavailableReason;

    public async Task InitializeAsync()
    {
        try
        {
            _container = await DisposableWorkloadContainer.StartAsync();
            _dockerAvailable = true;
        }
        catch (Exception ex)
        {
            _dockerAvailable = false;
            _unavailableReason = $"Docker is not available for integration tests: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private void SkipUnlessDockerAvailable() => Skip.IfNot(_dockerAvailable, _unavailableReason ?? "Docker unavailable");

    [SkippableFact]
    public async Task A_disposable_container_starts_and_reports_running()
    {
        SkipUnlessDockerAvailable();

        // Independent verification: this inspects via DisposableWorkloadContainer's own fresh
        // Docker.DotNet client, not through any Servyx transport, so it proves real daemon state rather
        // than merely echoing back whatever the fixture assumed.
        var container = _container!;
        var state = await container.InspectAsync();

        state.State.Should().NotBeNull();
        state.State!.Running.Should().BeTrue();
        state.Name.Should().Contain(container.Name);
    }

    [SkippableFact]
    public async Task A_disposable_container_is_removed_on_dispose()
    {
        SkipUnlessDockerAvailable();

        // Ownership moves into this test so DisposeAsync (which runs unconditionally after the test) does
        // not try to dispose an already-disposed container.
        var container = _container!;
        _container = null;

        (await container.StillExistsAsync()).Should().BeTrue("the container is still live before disposal");

        await container.DisposeAsync();

        (await container.StillExistsAsync()).Should().BeFalse("disposal must actually remove the container, not merely stop it");
    }

    [SkippableFact]
    public void The_guard_approves_the_fixtures_own_target()
    {
        SkipUnlessDockerAvailable();

        var target = new TargetDescriptor(
            TransportId: "ssh+docker",
            Endpoint: $"{_container!.Host}:{_container.Port}",
            CredentialUrn: null,
            DockerContext: null,
            Options: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = _container.Name });

        var approved = MutationTargetGuard.Approve(target);

        approved.Should().BeSameAs(target);
    }
}
