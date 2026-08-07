using System.Diagnostics;
using Servyx.E2E.Tests;

namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// A second, independent <see cref="ServyxAppProcess"/> — started with
/// <c>Servyx:Provisioning:Enabled=true</c> and per-server write grants — so a handful of scenarios can
/// illustrate <c>WriteMode.PreviewOnly</c> and <c>WriteMode.Enabled</c> without weakening the default
/// posture every other scenario runs against (see <see cref="TestRunContext.Fixture"/>, which never sets
/// the provisioning gate and stays closed for every scenario that doesn't opt into this one).
/// </summary>
/// <remarks>
/// <para>
/// Reuses the shared Chromium instance from <see cref="TestRunContext.Fixture"/> — only the app process
/// differs, not the browser — because launching a second Chromium instance for two scenarios would be pure
/// overhead. <see cref="ScenarioHooks"/> decides, per scenario, which app's <c>ServerAddress</c> a new
/// browser context points at.
/// </para>
/// <para>
/// <b>Why a real, adopted Docker container.</b> <c>ServerOverviewTab</c>'s Start/Restart/Stop/Kill controls
/// only render clickable when <c>ServerDetailPage</c>'s <c>_lifecycle</c> is non-null, and that is built by
/// <c>ServyxServerLifecycles</c> from <c>IServerQueryService</c> — the REAL, Docker-backed discovery
/// service, which <c>Servyx:DataSource=Mock</c> never replaces (only the dashboard's own display data is
/// mocked; the write-guard/lifecycle machinery always talks to a genuine Docker daemon). A per-server write
/// GRANT alone is therefore not enough to render live, clickable controls — Servyx also has to actually
/// find a matching container. Rather than fake that state or weaken <c>ServerOverviewTab</c>'s own logic to
/// make a screenshot possible, this fixture provisions one: a real (but never started) container, named
/// exactly <see cref="StubContainerName"/>, built from the bundled Palworld image and carrying the mount
/// path <c>DockerServerDiscovery</c> requires, so the SAME real discovery code path the product runs in
/// production finds it and <c>ServerDetailPage</c> resolves a genuine, non-null lifecycle. The container is
/// never started (<c>docker create</c>, not <c>docker run</c>) — nothing needs to actually run Palworld for
/// a card to render its buttons as enabled — and nothing in these scenarios ever invokes a lifecycle verb
/// against it (see the feature file's safety note).
/// </para>
/// </remarks>
public sealed class WriteEnabledAppFixture : IAsyncDisposable
{
    /// <summary>
    /// Grants both mock servers a non-default write mode, in the SAME process, so one extra app instance
    /// covers both the <c>PreviewOnly</c> and <c>Enabled</c> captures rather than needing two. The keys are
    /// each server's discovery id (see <c>MockDashboardDataService</c>) — <see cref="Web.Services.WritableServers.Mode"/>
    /// matches a configured key against either the server's id or its display name, and the id is the more
    /// stable, kebab-case-friendly choice documented in enabling-writes.md's example configuration.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EnvironmentOverrides = new Dictionary<string, string>
    {
        ["Servyx__Provisioning__Enabled"] = "true",
        ["Servyx__Servers__palygondwanaland__WriteMode"] = "PreviewOnly",
        ["Servyx__Servers__example-remote-palworld__WriteMode"] = "Enabled",
    };

    /// <summary>
    /// Must equal the mock "Example Remote Palworld" server's discovery id exactly: real discovery matches
    /// a queried server id first against a container's own id (an opaque Docker hash Servyx cannot
    /// predict), then falls back to the container's NAME — this is what makes that fallback match.
    /// </summary>
    private const string StubContainerName = "example-remote-palworld";

    /// <summary>
    /// The bundled Palworld image's real repository — <c>DockerServerDiscovery.Matches</c> requires an
    /// exact repository match (tag/digest ignored), so only this image (already pulled locally in this
    /// environment; see <c>definitions/palworld-docker.yaml</c>'s <c>detect.imageRepo</c>) satisfies it.
    /// </summary>
    private const string StubImage = "thijsvanloef/palworld-server-docker:latest";

    /// <summary>The mount path <c>DockerServerDiscovery.Matches</c> requires (<c>detect.requiredMounts</c>).</summary>
    private const string RequiredMountContainerPath = "/palworld";

    /// <summary>
    /// The docker label every container this fixture creates carries, and the only signal cleanup trusts
    /// before force-removing anything. <see cref="StubContainerName"/> must stay fixed — it cannot be
    /// GUID-suffixed the way <c>MutationTargetGuard</c>'s disposable containers are — because real
    /// <c>DockerServerDiscovery</c> matches a queried server id first against the container's own id (an
    /// opaque hash Servyx cannot predict) and falls back to its NAME, and that fallback is what makes this
    /// exact name resolve to the mock "Example Remote Palworld" server whose
    /// <c>Servyx__Servers__example-remote-palworld__WriteMode</c> grant key (see
    /// <see cref="EnvironmentOverrides"/>) also depends on it. Since the name can't change, cleanup instead
    /// never trusts the name alone: only a container carrying this label is ever force-removed, so a machine
    /// or CI runner that happens to have an unrelated container named "example-remote-palworld" is left
    /// untouched (a subsequent <c>docker create</c> with the same name then simply fails, surfaced as
    /// <see cref="DockerAvailable"/> = <see langword="false"/>, never a silent deletion).
    /// </summary>
    private const string FixtureLabel = "servyx-e2e=true";

    private string? _stubMountDir;
    private bool _stubContainerCreated;

    public ServyxAppProcess App { get; } = new();

    /// <summary>Whether the Docker stub container this fixture depends on was successfully provisioned.</summary>
    public bool DockerAvailable { get; private set; }

    /// <summary>Human-readable explanation, populated only when <see cref="DockerAvailable"/> is false.</summary>
    public string? DockerSkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        await TryProvisionStubContainerAsync().ConfigureAwait(false);

        // Started regardless of DockerAvailable: the PreviewOnly scenario never touches a container's
        // lifecycle at all, so it must keep working even in an environment with no Docker daemon reachable
        // — only the Enabled scenario (tagged @requires-docker) is skipped in that case, by ScenarioHooks.
        await App.StartAsync(environmentOverrides: EnvironmentOverrides).ConfigureAwait(false);
    }

    private async Task TryProvisionStubContainerAsync()
    {
        try
        {
            _stubMountDir = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "servyx-e2e-write-enabled-stub-mount", Guid.NewGuid().ToString("N"))).FullName;

            // Best-effort cleanup of a stale container left behind by a previous run that crashed before
            // reaching DisposeAsync. Scoped to FixtureLabel, never a bare name match (see StubContainerName
            // and FixtureLabel's remarks) — failure here (most commonly "no matching container") is
            // expected and ignored; only the subsequent `create` failing is treated as "Docker isn't
            // available".
            await RemoveLabeledStubContainerIfPresentAsync().ConfigureAwait(false);

            await RunDockerAsync(
                [
                    "create", "--name", StubContainerName, "--label", FixtureLabel,
                    "-v", $"{_stubMountDir}:{RequiredMountContainerPath}", StubImage,
                ],
                throwOnFailure: true).ConfigureAwait(false);

            _stubContainerCreated = true;
            DockerAvailable = true;
        }
        catch (Exception ex)
        {
            DockerAvailable = false;
            DockerSkipReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync().ConfigureAwait(false);

        if (_stubContainerCreated)
        {
            await RemoveLabeledStubContainerIfPresentAsync().ConfigureAwait(false);
        }

        if (_stubMountDir is not null)
        {
            try
            {
                Directory.Delete(_stubMountDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a per-run temp directory; a stray temp folder left behind is not
                // worth failing teardown over.
            }
        }
    }

    /// <summary>
    /// Removes the stub container if — and only if — one named exactly <see cref="StubContainerName"/> AND
    /// carrying <see cref="FixtureLabel"/> currently exists. Never a bare <c>docker rm -f &lt;name&gt;</c>:
    /// see <see cref="StubContainerName"/> and <see cref="FixtureLabel"/>'s remarks for why the name alone
    /// is not trusted. Called both as pre-run stale-container cleanup and from <see cref="DisposeAsync"/>.
    /// </summary>
    private static async Task RemoveLabeledStubContainerIfPresentAsync()
    {
        var matches = await RunDockerAsync(
            ["ps", "-aq", "--filter", $"name=^/{StubContainerName}$", "--filter", $"label={FixtureLabel}"],
            throwOnFailure: false).ConfigureAwait(false);

        var ids = matches.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
        {
            await RunDockerAsync(["rm", "-f", id], throwOnFailure: false).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a docker CLI command and returns its captured standard output.</summary>
    private static async Task<string> RunDockerAsync(string[] arguments, bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'docker {string.Join(' ', arguments)}' exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
