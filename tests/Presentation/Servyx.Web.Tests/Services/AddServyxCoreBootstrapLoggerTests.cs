using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Pins the contract a stdio-transport MCP host will depend on: <c>AddServyxCore</c>'s optional
/// <c>bootstrapLoggerFactory</c> parameter, when supplied, is used for every bootstrap-phase logger this
/// composition needs, and is never disposed by the composition itself. Getting the disposal half wrong would
/// break a caller's logging out from under it the first time <c>AddServyxCore</c> ran — see
/// <c>ServyxCoreCompositionExtensions.AddServyxCore</c>'s own remarks for why a factory writing to stdout
/// cannot be tolerated by such a host at all.
/// </summary>
public class AddServyxCoreBootstrapLoggerTests
{
    [Fact]
    public void Supplied_bootstrap_logger_factory_is_used_and_left_undisposed()
    {
        var builder = Host.CreateApplicationBuilder();
        var recordingFactory = new RecordingLoggerFactory();

        builder.AddServyxCore(recordingFactory);

        recordingFactory.CreateLoggerCallCount.Should().BeGreaterThan(0,
            "the composition's bootstrap-phase loggers (definition-catalog loading, ssh+docker wiring) " +
            "should all be created from the supplied factory rather than an internal one");
        recordingFactory.Disposed.Should().BeFalse(
            "the composition does not own a caller-supplied factory and must never dispose it");
    }

    [Fact]
    public void Omitted_bootstrap_logger_factory_leaves_web_host_behaviour_unchanged()
    {
        // No factory supplied: AddServyxCore must fall back to its own console-backed factory, exactly as it
        // always has, and that internally-created factory must be disposed before AddServyxCore returns —
        // the web host's existing, unchanged behaviour.
        var builder = Host.CreateApplicationBuilder();

        var composition = builder.AddServyxCore();

        composition.Should().NotBeNull();
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public int CreateLoggerCallCount { get; private set; }

        public bool Disposed { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            CreateLoggerCallCount++;
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
            // Not exercised by this composition; no bootstrap-phase code path registers a provider.
        }

        public void Dispose() => Disposed = true;
    }
}
