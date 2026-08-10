using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Regression coverage for the two disposal defects <c>ServyxSurfaceResolutionContextSource</c> was just
/// fixed for, which <see cref="ServyxBackupContextSource"/> shared member for member: it used to implement
/// only <see cref="IAsyncDisposable"/> — which <c>ServiceProvider.Dispose()</c> (the synchronous path every
/// <c>using var host = builder.Build()</c> and composition test relies on) throws for on a resolved singleton
/// — and its <c>DisposeAsync</c> used to await every memoized session unconditionally, including one still
/// mid-connect, which would hang disposal (and therefore <c>host.Dispose()</c>) forever.
/// </summary>
public class ServyxBackupContextSourceDisposalTests
{
    /// <summary>A fresh, single-bundled-definition install with the provisioning gate open — <see cref="ServyxBackupContextSource"/> is only registered inside that gated block.</summary>
    private static HostApplicationBuilder BuildFreshInstallBuilder()
    {
        var definitionsDir = Directory.CreateTempSubdirectory("servyx-web-tests-backup-dispose-defs-");
        var repoDefinitionsDir = Path.Combine(RepoRootLocator.Find().FullName, "definitions");
        var source = Directory.EnumerateFiles(repoDefinitionsDir, "*.yaml").First();
        File.Copy(source, Path.Combine(definitionsDir.FullName, Path.GetFileName(source)));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsDir.FullName;
        builder.Configuration["Servyx:Provisioning:Enabled"] = "true";
        return builder;
    }

    [Fact]
    public void Resolving_the_context_source_and_disposing_the_host_synchronously_does_not_throw()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        var act = () =>
        {
            using var host = builder.Build();

            // Resolving is what registers this singleton with the container's disposal tracker; the
            // assertion that matters is what happens when 'host' goes out of scope below and the container
            // disposes it SYNCHRONOUSLY, exactly as ServiceProvider.Dispose() does for every host and test
            // harness that never awaits shutdown.
            host.Services.GetRequiredService<ServyxBackupContextSource>().Should().NotBeNull();
        };

        act.Should().NotThrow(
            because: "ServyxBackupContextSource is a DI singleton resolved by hosts this project does not " +
                "own, so it must implement IDisposable alongside IAsyncDisposable or ServiceProvider.Dispose() " +
                "throws on shutdown for a singleton that implements only the async interface");
    }

    // LOAD-BEARING TEST SHAPE — do not simplify this back to a synchronous fake.
    //
    // The whole defect under test is that the old DisposeAsync awaited every memoized
    // Lazy<Task<IExecutionTarget>> unconditionally, including one still in flight. Proving the fix requires a
    // build that is GENUINELY still pending — not yet completed in any sense — at the exact moment
    // DisposeAsync's Task.IsCompletedSuccessfully check runs. A fake built on Task.FromResult(...) (or any
    // other synchronously-completed task) would already report IsCompletedSuccessfully == true by the time
    // that check executes, so this test would pass whether or not the fix is present and would silently stop
    // catching a regression back to the unconditional-await version.
    //
    // The transport's ConnectAsync below instead parks on an uncompleted TaskCompletionSource<T> that this
    // test controls, and 'reachedConnect' is awaited before disposal starts so the build is provably already
    // inside the real ITransport.ConnectAsync call (past _sessions.GetOrAdd) rather than racing disposal
    // against a build that has not even started yet.
    [Fact]
    public async Task Disposal_completes_promptly_with_a_build_still_pending_in_flight()
    {
        var reachedConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource<IExecutionTarget>(TaskCreationOptions.RunContinuationsAsynchronously);

        var transport = Substitute.For<ITransport>();
        transport.Capabilities.Returns(TransportCapabilities.ContainerScopedFiles);
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reachedConnect.TrySetResult();

                // Never completed by this test: that is the entire point of the assertion below.
                return releaseConnect.Task;
            });

        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(
                    "palworld-server",
                    "palworld-server",
                    "palworld",
                    ServerState.Running,
                    ServerHealthStatus.Healthy,
                    null,
                    null,
                    "localhost",
                    []),
                "thijsvanloef/palworld-server-docker:latest",
                "/data",
                "/data",
                null,
                null,
                null,
                null,
                [])));

        var source = new ServyxBackupContextSource(
            query,
            transport,
            new BackupWiringOptions(include: ["saves/**"]));

        // Started, not awaited: this build must still be running — parked on releaseConnect.Task — at the
        // moment disposal runs below.
        _ = source.GetAsync("palworld-server");

        await reachedConnect.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose() is the synchronous bridge, so it has to run on its own thread for this test to be able to
        // race it against a timeout rather than deadlocking the test itself.
        var disposeTask = Task.Run(() => source.Dispose());
        var winner = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));

        winner.Should().Be(
            disposeTask,
            because: "disposal must terminate even while a session build is still in flight, never wait for it to finish");

        // Let the still-pending build finish so nothing keeps running past the end of the test.
        releaseConnect.TrySetResult(Substitute.For<IExecutionTarget>());
    }
}
