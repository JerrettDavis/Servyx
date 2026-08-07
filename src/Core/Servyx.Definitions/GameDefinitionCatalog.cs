using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions;

/// <summary>
/// The aggregate, queryable view over every registered <see cref="IGameDefinitionProvider"/> — one
/// current <see cref="LoadedDefinition"/> per <c>metadata.id</c>, plus every <see cref="GameDefinition"/>
/// this process has ever successfully loaded, indexed by content hash.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Singleton-shaped.</strong> One instance is meant to be shared for the process lifetime (see
/// <see cref="ServiceCollectionExtensions.AddServyxDefinitions"/>), refreshed in place by
/// <see cref="RefreshAsync"/> rather than replaced.
/// </para>
/// <para>
/// <strong>Atomic swap.</strong> Each <see cref="RefreshAsync"/> call builds an entirely new immutable
/// snapshot — both dictionaries and the fault list — off to the side, then publishes it with a single
/// reference assignment. A reader can never observe a state where <see cref="DefinitionsById"/> reflects
/// the new refresh but <see cref="DefinitionsByContentHash"/> or <see cref="Faults"/> still reflects the
/// old one, or vice versa, because all three come from the same snapshot object.
/// </para>
/// <para>
/// <strong>The content-hash index only grows.</strong> <see cref="GameDefinitionRef"/>'s own doc comment
/// promises that a server pinned to a specific <see cref="GameDefinitionRef.ContentHash"/> never has its
/// behaviour silently changed by a catalog mutation. A hot reload replaces what <see cref="DefinitionsById"/>
/// considers "current" for an id, but every content hash this process has ever successfully loaded — for
/// that id or any other, current or superseded — remains resolvable through
/// <see cref="DefinitionsByContentHash"/> for the rest of the process's lifetime. A refresh only adds to
/// this index; it never removes an entry, even for an id that has since disappeared entirely.
/// </para>
/// <para>
/// <strong>A reload that fails validation never evicts the previously-good version.</strong> If a winning
/// reference's <see cref="IGameDefinitionProvider.LoadAsync"/> throws — most commonly
/// <see cref="DefinitionValidationException"/>, but any other exception is treated the same way, since
/// <c>LoadAsync</c> carries no "never throws" contract the way <c>ListAsync</c> does — the fault is recorded
/// and the id's entry in the new snapshot's <see cref="DefinitionsById"/> is carried forward unchanged from
/// the previous snapshot, if one existed. An id with no previous good version and a failing load simply has
/// no entry, which is the correct "never had a usable version" state rather than a crash.
/// </para>
/// <para>
/// <strong>The one exception to "carry forward": the file is actually gone.</strong> <see cref="FileNotFoundException"/>
/// and <see cref="DirectoryNotFoundException"/> from <c>LoadAsync</c> mean something different from every
/// other load failure above — not "the content that's there right now is bad", but "there is no content
/// there any more". This is a real TOCTOU window: a file <c>ListAsync</c> saw a moment ago can be deleted
/// before the matching <c>LoadAsync</c> call runs. Carrying the previous version forward here would
/// resurrect a deleted definition for one refresh cycle, contradicting "deletion removes, it does not
/// persist". So this one case deliberately evicts instead — the id is left out of the new snapshot
/// entirely — and self-heals on the very next refresh, whose own <c>ListAsync</c> will no longer see the
/// file at all.
/// </para>
/// <para>
/// <strong>Duplicate ids across providers.</strong> Within a single provider's own <see cref="IGameDefinitionProvider.ListAsync"/>
/// result, duplicate-id resolution is that provider's own job (see <see cref="FileSystemGameDefinitionProvider"/>'s
/// remarks). Across two different providers that both claim the same id, this catalog resolves the
/// collision by provider priority — the order providers were supplied to the constructor, first wins — and
/// records a fault for the loser.
/// </para>
/// <para>
/// <strong>Publishing is serialized.</strong> <see cref="RefreshAsync"/> and <see cref="RecordFaultAsync"/>
/// both acquire the same internal gate around their read-modify-publish of <see cref="_snapshot"/>. Nothing
/// today calls <see cref="RefreshAsync"/> concurrently with itself — the one caller in this phase,
/// <see cref="DefinitionCatalogRefreshService"/>, drains a single channel sequentially — but without the
/// gate a future second caller (a manual "refresh now" admin endpoint, say) could read the same
/// <c>previous</c> snapshot as an in-flight refresh and then publish over it, silently discarding whichever
/// finished last's work. The gate is cheap insurance against that, paid now while it is a two-line change.
/// </para>
/// </remarks>
public sealed class GameDefinitionCatalog : IDefinitionCatalogDiagnostics
{
    private sealed record Snapshot(
        ImmutableDictionary<string, LoadedDefinition> ById,
        ImmutableDictionary<string, GameDefinition> ByContentHash,
        ImmutableList<DefinitionFault> Faults);

    private static readonly Snapshot EmptySnapshot = new(
        ImmutableDictionary<string, LoadedDefinition>.Empty,
        ImmutableDictionary<string, GameDefinition>.Empty,
        ImmutableList<DefinitionFault>.Empty);

    private readonly IReadOnlyList<IGameDefinitionProvider> _providers;
    private readonly ILogger<GameDefinitionCatalog>? _logger;

    // Reference-type assignment is already atomic; volatile only adds the memory-visibility guarantee that
    // a refresh on one thread is promptly visible to a reader on another. The gate below is what keeps two
    // concurrent publishers (RefreshAsync, RecordFaultAsync) from racing each other's read-modify-publish —
    // see the class remarks.
    private volatile Snapshot _snapshot = EmptySnapshot;
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    /// <summary>Creates a catalog aggregating <paramref name="providers"/>, in priority order.</summary>
    /// <param name="providers">
    /// Every provider to aggregate. Order matters: the first provider to claim a given
    /// <c>metadata.id</c> wins a cross-provider collision — see the class remarks.
    /// </param>
    /// <param name="logger">Optional logger for provider misbehaviour and load failures during a refresh.</param>
    public GameDefinitionCatalog(IEnumerable<IGameDefinitionProvider> providers, ILogger<GameDefinitionCatalog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToArray();
        _logger = logger;
    }

    /// <summary>The current definition for every known id.</summary>
    public IReadOnlyDictionary<string, LoadedDefinition> DefinitionsById => _snapshot.ById;

    /// <summary>Every definition this process has ever successfully loaded, indexed by content hash. Only ever grows.</summary>
    public IReadOnlyDictionary<string, GameDefinition> DefinitionsByContentHash => _snapshot.ByContentHash;

    /// <inheritdoc />
    public IReadOnlyList<DefinitionFault> Faults => _snapshot.Faults;

    /// <summary>The current definition for <paramref name="id"/>, or <see langword="null"/> if none is known.</summary>
    public LoadedDefinition? TryGetById(string id) =>
        _snapshot.ById.TryGetValue(id, out var definition) ? definition : null;

    /// <summary>
    /// The exact definition previously loaded for <paramref name="contentHash"/>, or <see langword="null"/>
    /// if this process has never successfully loaded content with that hash. Never affected by a
    /// subsequent hot reload of the id it originally belonged to — see the class remarks.
    /// </summary>
    public GameDefinition? TryGetByContentHash(string contentHash) =>
        _snapshot.ByContentHash.TryGetValue(contentHash, out var definition) ? definition : null;

    /// <summary>
    /// Re-lists and re-loads every provider, then atomically publishes the result. Safe to call
    /// concurrently with reads (see the class remarks). Safe to call concurrently with itself or with
    /// <see cref="RecordFaultAsync"/> too — both are serialized behind the same internal gate, so a slower
    /// concurrent refresh can never publish a stale snapshot over a faster one that started later and
    /// finished first.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _publishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var previous = _snapshot;
        var faults = new List<DefinitionFault>();

        var winners = new Dictionary<string, (GameDefinitionRef Reference, IGameDefinitionProvider Provider)>(StringComparer.Ordinal);

        for (var priority = 0; priority < _providers.Count; priority++)
        {
            var provider = _providers[priority];

            IReadOnlyList<GameDefinitionRef> refs;
            try
            {
                refs = await provider.ListAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // ListAsync is documented to never throw; this is a defensive backstop so one
                // contract-violating provider cannot prevent every other provider's definitions from being
                // cataloged.
                _logger?.LogError(ex, "Provider '{SourceId}' violated ListAsync's never-throw contract.", provider.SourceId);
                faults.Add(new DefinitionFault(provider.SourceId, $"Provider failed to list definitions: {ex.Message}", null, null));
                continue;
            }

            if (provider is IDefinitionCatalogDiagnostics diagnostics)
            {
                faults.AddRange(diagnostics.Faults);
            }

            foreach (var reference in refs)
            {
                if (winners.TryGetValue(reference.Id, out var existing))
                {
                    faults.Add(new DefinitionFault(
                        DescribeSource(reference, provider),
                        $"Duplicate definition id '{reference.Id}': provider '{existing.Provider.SourceId}' "
                        + $"(registered first, at '{DescribeSource(existing.Reference, existing.Provider)}') takes "
                        + $"precedence over provider '{provider.SourceId}'.",
                        null,
                        null));
                    continue;
                }

                winners[reference.Id] = (reference, provider);
            }
        }

        var byId = ImmutableDictionary.CreateBuilder<string, LoadedDefinition>(StringComparer.Ordinal);
        var byContentHash = previous.ByContentHash.ToBuilder();

        foreach (var (id, (reference, provider)) in winners)
        {
            try
            {
                var loaded = await provider.LoadAsync(reference, ct).ConfigureAwait(false);
                byId[id] = loaded;

                if (loaded.Document is GameDefinition typed)
                {
                    byContentHash[loaded.Ref.ContentHash] = typed;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DefinitionValidationException ex)
            {
                // The first Error-severity issue, not merely the first issue recorded: ex.Issues is every
                // issue in parse order, warnings included, and a document can easily accumulate several
                // warnings (e.g. an unrecognized backup.adopt adapter, an unresolvable template token) before
                // the parser ever reaches the actual disqualifying Error. Pointing the fault at Issues[0]
                // regardless of severity would name a real position in the file, but not necessarily — and
                // in practice usually not — the position of the thing that actually failed validation.
                var firstIssue = ex.Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Error)
                    ?? (ex.Issues.Count > 0 ? ex.Issues[0] : null);
                faults.Add(new DefinitionFault(
                    DescribeSource(reference, provider),
                    $"Definition '{id}' failed validation and was not reloaded: {ex.Message}",
                    firstIssue?.Line,
                    firstIssue?.Column));

                // The previously-good version, if there was one, is carried forward unchanged — see the
                // class remarks. Never let a bad reload evict good, already-running state.
                if (previous.ById.TryGetValue(id, out var previousGood))
                {
                    byId[id] = previousGood;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deliberately NOT carried forward — see the class remarks. This is "no longer exists", not
                // "failed to load"; resurrecting a deleted definition for one refresh cycle would be worse
                // than briefly having no entry for it.
                _logger?.LogWarning(
                    ex,
                    "Definition '{Id}' from provider '{SourceId}' disappeared between listing and loading; evicting it.",
                    id,
                    provider.SourceId);
                faults.Add(new DefinitionFault(
                    DescribeSource(reference, provider),
                    $"Definition '{id}' disappeared before it could be loaded: {ex.Message}",
                    null,
                    null));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Provider '{SourceId}' failed to load definition '{Id}'.", provider.SourceId, id);
                faults.Add(new DefinitionFault(
                    DescribeSource(reference, provider),
                    $"Definition '{id}' could not be loaded: {ex.Message}",
                    null,
                    null));

                if (previous.ById.TryGetValue(id, out var previousGood))
                {
                    byId[id] = previousGood;
                }
            }
        }

        var next = new Snapshot(byId.ToImmutable(), byContentHash.ToImmutable(), faults.ToImmutableList());
        _snapshot = next;
    }

    /// <summary>
    /// What a <see cref="DefinitionFault"/> should call <paramref name="reference"/>'s origin: its real
    /// <see cref="GameDefinitionRef.SourcePath"/> when the provider populated one — which every provider that
    /// has an actual file backing its definitions is expected to do, so an author reading the fault card
    /// always sees the file to open, not an opaque identifier — or else the synthesized
    /// <c>"{SourceId}:{Id}"</c> this class used unconditionally before <see cref="GameDefinitionRef"/> could
    /// carry a path, kept only as the fallback for a provider with no single-file notion of origin.
    /// </summary>
    private static string DescribeSource(GameDefinitionRef reference, IGameDefinitionProvider provider) =>
        reference.SourcePath ?? $"{provider.SourceId}:{reference.Id}";

    /// <summary>
    /// Appends a single fault to the currently-published snapshot without a full <see cref="RefreshAsync"/>,
    /// so a condition discovered outside the list/load cycle — most notably <see cref="DefinitionCatalogRefreshService"/>
    /// learning that a provider's <see cref="IGameDefinitionProvider.WatchAsync"/> stream died — is visible
    /// through <see cref="Faults"/> immediately rather than only after (and only if) something else
    /// triggers another refresh. Serialized against <see cref="RefreshAsync"/> behind the same gate, so this
    /// can never be silently overwritten mid-append by a concurrent refresh publishing its own, freshly
    /// computed fault list — nor can it silently drop a refresh that was about to publish.
    /// </summary>
    /// <remarks>
    /// The appended fault does not survive the <em>next</em> <see cref="RefreshAsync"/> call, which replaces
    /// <see cref="Faults"/> wholesale with whatever it freshly discovers. That is intentional, not an
    /// oversight: this method exists for prompt visibility of a condition between refreshes, not as a
    /// second, independently-tracked fault ledger that would need its own reconciliation story.
    /// </remarks>
    /// <param name="fault">The fault to record.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordFaultAsync(DefinitionFault fault, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fault);

        await _publishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _snapshot;
            _snapshot = current with { Faults = current.Faults.Add(fault) };
        }
        finally
        {
            _publishGate.Release();
        }
    }
}
