using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions;

/// <summary>
/// Populates a <see cref="GameDefinitionCatalog"/> at startup and, when enabled, keeps it current by
/// pumping every registered <see cref="IGameDefinitionProvider.WatchAsync"/> stream into a single
/// <see cref="GameDefinitionCatalog.RefreshAsync"/> loop.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Always performs one initial refresh</strong>, regardless of <see cref="Watch"/> — a host that
/// registers this service gets a populated catalog on startup even with hot reload disabled. Only the
/// ongoing subscription to <see cref="IGameDefinitionProvider.WatchAsync"/> is gated.
/// </para>
/// <para>
/// <strong>Hot reload is a dev convenience, not a production feature.</strong> <see cref="Watch"/> is
/// resolved by <see cref="ServiceCollectionExtensions.AddServyxDefinitions"/> from
/// <c>Servyx:Definitions:Watch</c>, defaulting to <see langword="true"/> only in the Development
/// environment. When disabled, <see cref="ExecuteAsync"/> performs the one initial refresh and returns —
/// a completed <see cref="BackgroundService"/> is a normal, supported state, not a failure.
/// </para>
/// <para>
/// <strong>One refresh at a time.</strong> Every provider's watch stream feeds a single unbounded channel;
/// a dedicated loop drains it and calls <see cref="GameDefinitionCatalog.RefreshAsync"/> once per received
/// signal, so concurrent filesystem events from several providers never trigger overlapping refreshes.
/// </para>
/// <para>
/// <strong>A refresh failure never stops the service.</strong> Each iteration's call to <see cref="GameDefinitionCatalog.RefreshAsync"/>
/// is wrapped and logged; the loop continues to the next signal regardless. <see cref="GameDefinitionCatalog.RefreshAsync"/>
/// itself already isolates a single bad definition (see its own remarks), so this is a second, outer layer
/// of the same never-crash-the-host posture.
/// </para>
/// <para>
/// <strong>One dead watch stream never silences the others, and never goes unnoticed.</strong> <c>WatchAsync</c>
/// carries no "never throws" contract, so <see cref="PumpAsync"/> catches anything that is not
/// <see cref="OperationCanceledException"/>, logs it, and records a <see cref="DefinitionFault"/> via
/// <see cref="GameDefinitionCatalog.RecordFaultAsync"/> — visible through <see cref="GameDefinitionCatalog.Faults"/>
/// immediately, not only at shutdown once <see cref="Task.WhenAll(Task[])"/> over every pump task finally
/// surfaces it, misattributed to shutdown long after the real failure. That pump simply ends; every other
/// provider's pump, and the refresh loop itself, are unaffected.
/// </para>
/// </remarks>
public sealed class DefinitionCatalogRefreshService : BackgroundService
{
    private readonly GameDefinitionCatalog _catalog;
    private readonly IReadOnlyList<IGameDefinitionProvider> _providers;
    private readonly ILogger<DefinitionCatalogRefreshService>? _logger;

    /// <summary>Creates the refresh service.</summary>
    /// <param name="catalog">The catalog kept current.</param>
    /// <param name="providers">
    /// Every registered provider. Watched for changes only when <paramref name="watch"/> is <see langword="true"/>.
    /// </param>
    /// <param name="watch">Whether to subscribe to hot reload after the initial refresh.</param>
    /// <param name="logger">Optional logger for refresh failures and watcher lifecycle.</param>
    public DefinitionCatalogRefreshService(
        GameDefinitionCatalog catalog,
        IEnumerable<IGameDefinitionProvider> providers,
        bool watch,
        ILogger<DefinitionCatalogRefreshService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(providers);

        _catalog = catalog;
        _providers = providers.ToArray();
        Watch = watch;
        _logger = logger;
    }

    /// <summary>Whether this instance subscribes to hot reload after its initial refresh.</summary>
    public bool Watch { get; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshQuietlyAsync(stoppingToken).ConfigureAwait(false);

        if (!Watch || _providers.Count == 0)
        {
            return;
        }

        var signals = Channel.CreateUnbounded<GameDefinitionRef>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var pumps = _providers
            .Select(provider => PumpAsync(provider, signals.Writer, stoppingToken))
            .ToArray();

        try
        {
            await foreach (var _ in signals.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshQuietlyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            signals.Writer.TryComplete();
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
    }

    private async Task RefreshQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await _catalog.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh the game definition catalog.");
        }
    }

    private async Task PumpAsync(IGameDefinitionProvider provider, ChannelWriter<GameDefinitionRef> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var reference in provider.WatchAsync(ct).ConfigureAwait(false))
            {
                await writer.WriteAsync(reference, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            // Unlike IGameDefinitionProvider.ListAsync, WatchAsync carries no "never throws" contract.
            // Left uncaught here, this pump task would fault silently: nothing would log it, this loop
            // would never learn the provider's stream died, and hot reload for it would just go quiet —
            // the fault would only ever have surfaced at shutdown via Task.WhenAll(pumps) in ExecuteAsync's
            // finally, misattributed to shutdown long after the real failure. Catching it here, logging it,
            // and recording it as a fault keeps one dead stream from silencing the others or taking down
            // the service, while still making the condition visible immediately rather than only in hindsight.
            _logger?.LogError(
                ex,
                "Provider '{SourceId}' stopped watching for changes after an unexpected error; hot reload for it is no longer active.",
                provider.SourceId);

            // Best-effort and deliberately not tied to the pump's own (likely-cancelled-by-now-or-soon) ct:
            // the point is to make this visible before the process shuts down, not to let a shutdown race
            // suppress the very fault explaining why hot reload for this provider stopped.
            try
            {
                await _catalog.RecordFaultAsync(
                    new DefinitionFault(
                        provider.SourceId,
                        $"Provider '{provider.SourceId}' stopped watching for changes: {ex.Message}. Hot reload "
                        + "for it is not active again until the process restarts.",
                        null,
                        null),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception recordEx)
            {
                _logger?.LogError(recordEx, "Additionally failed to record the fault for provider '{SourceId}''s dead watch stream.", provider.SourceId);
            }
        }
    }
}
