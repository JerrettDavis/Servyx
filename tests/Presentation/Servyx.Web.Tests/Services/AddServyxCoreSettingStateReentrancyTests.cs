using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Discovery;
using Servyx.Domain.Transport;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Guards the seam where the settings-read path and the server-query path meet, on the real composition.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because a deadlock lived here and 4,764 passing tests did not see it.</strong>
/// <c>ServerQueryService</c>, <see cref="Servyx.Domain.Configuration.IServerConfigSessionSource"/> and
/// <see cref="Servyx.Domain.Configuration.ISettingStateResolverFactory"/> are all singletons, so a session
/// source that asked <c>IServerQueryService</c> for a server's details was asking the very instance already
/// executing. <c>GetServerDetailAsync</c> enriches its settings rows, enrichment opens the session set, and
/// opening the session set called <c>GetServerDetailAsync</c> — for the same server id, on the same object.
/// The memoizing <c>Lazy&lt;Task&gt;</c> publishes as soon as the outer build hits its first await, so the
/// re-entrant call did not recurse: it received the pending task the outer frame was already awaiting, and
/// the two waited on each other forever. No exception, no timeout, and
/// <c>ServerQueryService</c>'s own catch-all cannot help — a deadlocked task never throws.
/// </para>
/// <para>
/// <strong>Every existing test missed it for the same reason: none of them crossed the seam.</strong> The
/// composition tests resolve the singletons and stop; the resolver's own tests use stub session sources
/// rather than the real one. So this test does the one thing neither did — builds the container the host
/// actually builds, and calls the method the Settings tab actually calls.
/// </para>
/// <para>
/// <strong>The timeout is the assertion.</strong> A regression here does not fail, it hangs, and a hung CI
/// job reports nothing useful. Bounding the wait converts that into a named failure.
/// </para>
/// </remarks>
public class AddServyxCoreSettingStateReentrancyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private const string ContainerId = "pal-1";
    private const string ContainerName = "palworld-server";
    private const string DataRoot = "/palworld";

    /// <summary>
    /// A fresh install pinned to the bundled Palworld definition specifically — it declares a settings
    /// catalogue and a docker profile with config surfaces, which is what makes enrichment run at all.
    /// </summary>
    private static HostApplicationBuilder BuildFreshInstallBuilder()
    {
        var definitionsDir = Directory.CreateTempSubdirectory("servyx-web-tests-reentrancy-defs-");
        var source = Path.Combine(RepoRootLocator.Find().FullName, "definitions", "palworld-docker.yaml");
        File.Copy(source, Path.Combine(definitionsDir.FullName, "palworld-docker.yaml"));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsDir.FullName;
        return builder;
    }

    [Fact]
    public async Task GetServerDetailAsync_OnTheRealComposition_CompletesRatherThanDeadlockingOnItsOwnSettingsRead()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        // Discovery and the transport are the only two things standing between this test and a Docker
        // daemon. Everything between them — the query service, the setting-state factory, the session
        // source, and the wiring that connects them — is the real registration under test.
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerDiscovery>(new OneServerDiscovery()));
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(new UnreachableTransport()));

        using var host = builder.Build();
        var query = host.Services.GetRequiredService<IServerQueryService>();

        var detail = await query.GetServerDetailAsync(ContainerId).WaitAsync(Budget);

        detail.Should().NotBeNull();

        // Enrichment genuinely ran: a settings-bearing definition is what makes the re-entrant path
        // reachable, so a test that saw no rows would prove nothing.
        detail!.Settings.Should().NotBeEmpty(
            because: "the deadlock is only reachable when there is at least one settings row to enrich");
    }

    [Fact]
    public async Task GetServerDetailAsync_CalledTwice_StillCompletes_BecauseTheSessionSetIsMemoized()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerDiscovery>(new OneServerDiscovery()));
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(new UnreachableTransport()));

        using var host = builder.Build();
        var query = host.Services.GetRequiredService<IServerQueryService>();

        // The second call hits the memoized session set rather than building one. A cached faulted or
        // half-built entry would surface here and nowhere else.
        await query.GetServerDetailAsync(ContainerId).WaitAsync(Budget);
        var second = await query.GetServerDetailAsync(ContainerId).WaitAsync(Budget);

        second.Should().NotBeNull();
    }

    [Fact]
    public async Task TheSessionSource_AskedDirectly_CompletesWithoutGoingThroughTheSettingsPipeline()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerDiscovery>(new OneServerDiscovery()));
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(new UnreachableTransport()));

        using var host = builder.Build();
        var sessions = host.Services.GetRequiredService<Servyx.Domain.Configuration.IServerConfigSessionSource>();

        // The same question from the other direction: building a server's session set must not depend on
        // anything that reads settings, so it completes on its own.
        var result = await sessions.GetAsync(ContainerId).WaitAsync(Budget);

        result.Should().NotBeNull();
        result!.Surfaces.Should().NotBeEmpty(because: "the Palworld docker profile declares config surfaces");
    }

    /// <summary>Discovery that reports exactly one adopted container, without a daemon.</summary>
    /// <remarks>
    /// <strong>The <see cref="Task.Yield"/> is load-bearing, not decoration.</strong> Real discovery talks
    /// to a daemon and therefore suspends; a fake that completes synchronously does not reproduce the bug
    /// this file exists for. With a synchronous fake the whole re-entrant chain runs inside the memoizing
    /// <c>Lazy</c>'s own factory invocation, so <c>Lazy</c>'s recursion detection throws and the enrichment
    /// catch-all swallows it — the call completes and the test passes while the production path still
    /// deadlocks. Yielding once publishes the pending task before re-entry, which is exactly what a real
    /// daemon round trip does.
    /// </remarks>
    private sealed class OneServerDiscovery : IServerDiscovery
    {
        public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
            string imageRepository,
            string requiredMountContainerPath,
            CancellationToken ct = default)
        {
            await Task.Yield();

            return
            [
                new DiscoveredServer(
                    ContainerId,
                    ContainerName,
                    "thijsvanloef/palworld-server-docker:latest",
                    ImageDigest: null,
                    "running",
                    "healthy",
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    Ports: [],
                    Mounts: [new DiscoveredMount("/srv/palworld", DataRoot, ReadWrite: true)],
                    NetworkName: null,
                    ContainerIp: null,
                    MemoryLimitBytes: null,
                    CpuLimit: null,
                    RestartPolicy: null,
                    ComposeLabels: new Dictionary<string, string>(StringComparer.Ordinal),
                    EnvironmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["SERVER_NAME"] = "From the live container",
                    }),
            ];
        }
    }

    /// <summary>
    /// A transport that refuses to connect. Deliberate: the point of this test is that the settings read
    /// runs to completion and degrades honestly, not that it succeeds. A session that cannot be opened is
    /// the ordinary condition on a machine with no daemon, which is exactly what CI is.
    /// </summary>
    private sealed class UnreachableTransport : ITransport
    {
        public string TransportId => "docker";

        public TransportCapabilities Capabilities =>
            TransportCapabilities.FileRead
            | TransportCapabilities.FileWrite
            | TransportCapabilities.ContainerScopedFiles;

        public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
            Task.FromResult(new TargetHealth(false, null, "No daemon in this test."));

        public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default) =>
            throw new InvalidOperationException("No daemon in this test.");
    }
}
