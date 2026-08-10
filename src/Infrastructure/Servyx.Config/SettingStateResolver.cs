using System.Collections.Concurrent;
using System.Text;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;

namespace Servyx.Config;

/// <summary>
/// The default <see cref="ISettingStateResolverFactory"/>: resolves one server's declared surfaces against
/// every session it is reachable through, loads its recorded intent, and hands back a
/// <see cref="SettingStateResolver"/> bound to the result.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Resolution runs once per session, and the results are merged.</strong> A
/// <see cref="SurfaceResolutionContext"/> names exactly one <see cref="SurfaceResolutionContext.SessionRoot"/>,
/// but a <c>kind: docker</c> deployment's <c>${DATA_DIR}</c> lives inside the container while its
/// <c>${COMPOSE_DIR}</c> is a host directory, so one session cannot serve both. Each session therefore
/// resolves the whole declared set and contributes the subset it can actually reach; a surface no session
/// can reach keeps its failure.
/// </para>
/// <para>
/// <strong>A surface resolving on two sessions is a bug, and is treated as one.</strong> The composition
/// root nulls <see cref="SurfaceResolutionContext.ComposeDirectory"/> on the container session and
/// <see cref="SurfaceResolutionContext.DataDirectory"/> on the host one precisely so this cannot happen —
/// which is exactly why it is checked rather than assumed. Silently picking a winner would hide a
/// regression in that nulling, and the observable consequence would be reading a file from the wrong
/// filesystem: the single failure mode this whole layer exists to prevent. See
/// <see cref="CreateAsync"/>'s exception.
/// </para>
/// <para>
/// <strong>This type reads. It never writes.</strong> Nothing here calls
/// <see cref="IExecutionTarget.WriteFileAsync"/>, <see cref="IExecutionTarget.DeleteAsync"/>, or
/// <see cref="IExecutionTarget.ExecuteAsync"/>. Applying a value is <c>IPlanExecutor</c>'s job and is
/// deliberately absent from this phase.
/// </para>
/// </remarks>
public sealed class SettingStateResolverFactory : ISettingStateResolverFactory
{
    private readonly IServerConfigSessionSource _sessions;
    private readonly ISurfaceResolver _surfaceResolver;
    private readonly IReadOnlyDictionary<string, IConfigAdapter> _adapters;
    private readonly IReadOnlyDictionary<string, IConfigValueCodec> _codecs;
    private readonly IServerSettingsService? _desiredValues;

    /// <summary>Creates the factory.</summary>
    /// <param name="sessions">Supplies each server's live sessions and its declared surface set.</param>
    /// <param name="surfaceResolver">Turns declared surfaces into concrete, capability-checked paths.</param>
    /// <param name="adapters">
    /// The registered format adapters, keyed by <see cref="IConfigAdapter.FormatId"/>. Injected as a set so
    /// a newly registered adapter is picked up with no change here.
    /// </param>
    /// <param name="codecs">The registered value codecs, keyed by <see cref="IConfigValueCodec.CodecId"/>.</param>
    /// <param name="desiredValues">
    /// Servyx's own record of operator intent, or <see langword="null"/> when no store is wired — in which
    /// case <see cref="SettingState.Desired"/> is reported as <see langword="null"/> for every setting
    /// rather than being invented. Optional so <see cref="ServiceCollectionExtensions.AddServyxConfig"/>
    /// stays self-contained and validatable on its own, exactly like
    /// <see cref="ISurfaceResolutionContextSource"/>'s placeholder registration.
    /// </param>
    public SettingStateResolverFactory(
        IServerConfigSessionSource sessions,
        ISurfaceResolver surfaceResolver,
        IEnumerable<IConfigAdapter> adapters,
        IEnumerable<IConfigValueCodec> codecs,
        IServerSettingsService? desiredValues = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(surfaceResolver);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(codecs);

        _sessions = sessions;
        _surfaceResolver = surfaceResolver;
        _adapters = adapters.ToDictionary(a => a.FormatId, StringComparer.OrdinalIgnoreCase);
        _codecs = codecs.ToDictionary(c => c.CodecId, StringComparer.OrdinalIgnoreCase);
        _desiredValues = desiredValues;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The same surface id resolved successfully on more than one of the server's sessions, so there is no
    /// single filesystem its value can honestly be read from.
    /// </exception>
    public async Task<ISettingStateResolver> CreateAsync(SettingStateScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ServerId);

        var sessions = await _sessions.GetAsync(scope.ServerId, ct).ConfigureAwait(false);
        var declared = (sessions?.Surfaces ?? []).ToDictionary(s => s.Id, StringComparer.Ordinal);

        var bound = new Dictionary<string, BoundSurface>(StringComparer.Ordinal);
        var failures = new Dictionary<string, SurfaceResolutionFailure>(StringComparer.Ordinal);

        foreach (var session in sessions?.Sessions ?? [])
        {
            var resolution = await _surfaceResolver
                .ResolveAsync(scope.ServerId, session.Target, sessions!.Surfaces, ct)
                .ConfigureAwait(false);

            foreach (var surface in resolution.Resolved)
            {
                if (bound.TryGetValue(surface.Id, out var already))
                {
                    throw new InvalidOperationException(
                        $"Surface '{surface.Id}' resolved on two different sessions for server "
                        + $"'{scope.ServerId}': '{already.Surface.Path?.Value}' via {already.Session.Description}, "
                        + $"and '{surface.Path?.Value}' via {session.Description}. Exactly one filesystem can "
                        + "hold a given surface, so picking either one would be a guess — and the wrong guess "
                        + "reads a real file from the wrong machine instead of failing. The session contexts "
                        + "must expand at most one of '${DATA_DIR}'/'${COMPOSE_DIR}' each; fix the "
                        + "ISurfaceResolutionContextSource that produced both.");
                }

                if (!declared.TryGetValue(surface.Id, out var declaration))
                {
                    // Unreachable via ISurfaceResolver, which only ever returns surfaces it was handed, but
                    // a resolved surface with no declaration would silently lose its regeneration trigger.
                    continue;
                }

                bound[surface.Id] = new BoundSurface(surface, declaration, session);
            }

            foreach (var failure in resolution.Unresolvable)
            {
                Record(failures, failure);
            }
        }

        // A surface that failed on the session that cannot reach it and succeeded on the one that can is
        // simply reachable; its failure is an artefact of asking the wrong session, not a fact about it.
        foreach (var id in bound.Keys)
        {
            failures.Remove(id);
        }

        var desired = _desiredValues is null
            ? null
            : await _desiredValues.LoadAsync(scope.ServerId, ct).ConfigureAwait(false);

        return new SettingStateResolver(scope, bound, failures, _adapters, _codecs, desired);
    }

    /// <summary>
    /// Collapses the per-session failures for one surface into a single entry. Identical reasons (the usual
    /// case — a missing adapter fails the same way on every session) dedupe outright; genuinely different
    /// ones are combined, because two entries for one surface reads as two problems when there is one.
    /// </summary>
    private static void Record(Dictionary<string, SurfaceResolutionFailure> failures, SurfaceResolutionFailure failure)
    {
        if (!failures.TryGetValue(failure.SurfaceId, out var existing))
        {
            failures[failure.SurfaceId] = failure;
            return;
        }

        if (string.Equals(existing.Reason, failure.Reason, StringComparison.Ordinal))
        {
            return;
        }

        failures[failure.SurfaceId] = existing with
        {
            Reason = $"{existing.Reason} On this server's other configuration session: {failure.Reason}",
        };
    }
}

/// <summary>One declared surface, resolved to a concrete path on one specific session.</summary>
/// <param name="Surface">The resolved surface, carrying its path, format id and codec id.</param>
/// <param name="Declaration">
/// The surface as the definition author wrote it. Kept because <see cref="ConfigSurface"/> deliberately
/// drops <see cref="DeclaredConfigSurface.Regeneration"/>, which is the only thing that can answer whether
/// an authoritative/rendered disagreement is waiting on a restart.
/// </param>
/// <param name="Session">The session this surface's path is reachable on.</param>
internal sealed record BoundSurface(ConfigSurface Surface, DeclaredConfigSurface Declaration, ConfigSession Session);

/// <summary>
/// The default <see cref="ISettingStateResolver"/>: reads one server's bound surfaces and projects each
/// setting onto the four-column <see cref="SettingState"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bound to one server, created by <see cref="SettingStateResolverFactory"/>.</strong> See
/// <see cref="ISettingStateResolverFactory"/>'s remarks for why the declared
/// <see cref="ISettingStateResolver"/> contract was kept as-is rather than widened to take a server id.
/// </para>
/// <para>
/// <strong>Each surface is read and parsed at most once per instance.</strong> Resolving fifty settings
/// that all live in one <c>.env</c> opens that file once. The cache's lifetime is deliberately this
/// instance — one settings view — so the next view re-reads and sees current drift rather than replaying a
/// stale file.
/// </para>
/// <para>
/// <strong>Every column is masked for a secret, and drift is computed before masking.</strong> Masking
/// first would make every secret compare equal to every other secret and report
/// <see cref="DriftKind.None"/> for a value that had in fact drifted — a wrong answer dressed as a safe
/// one. The real values are compared, then discarded; only the fixed <c>"********"</c> mask leaves this
/// type, matching what <c>ServerQueryService.BuildSettings</c> already does. Nothing here logs a value.
/// </para>
/// <para>
/// <strong>A surface with no registered adapter degrades to an unreadable column, never a wrong one.</strong>
/// <see cref="ISurfaceResolver"/> refuses such a surface up front (that is how a <c>yaml</c> surface behaves
/// while no YAML <see cref="IConfigAdapter"/> is registered), so it arrives here as a failure with a reason,
/// its columns stay <see langword="null"/>, and <see cref="DriftKind.Unreadable"/> is set. The same is true
/// of a surface whose file is missing, whose parse throws, or whose codec is not registered.
/// </para>
/// </remarks>
public sealed class SettingStateResolver : ISettingStateResolver
{
    /// <summary>The fixed placeholder every column of a secret setting is reported as.</summary>
    /// <remarks>
    /// Identical to <c>ServerQueryService.BuildSettings</c>'s own mask on purpose: two different masks would
    /// let an operator infer which code path produced a row.
    /// </remarks>
    public const string SecretMask = "********";

    private readonly IReadOnlyDictionary<string, SettingDescriptor> _settings;
    private readonly IReadOnlyDictionary<string, BoundSurface> _bound;
    private readonly IReadOnlyDictionary<string, SurfaceResolutionFailure> _failures;
    private readonly IReadOnlyDictionary<string, IConfigAdapter> _adapters;
    private readonly IReadOnlyDictionary<string, IConfigValueCodec> _codecs;
    private readonly ServerSettingsSnapshot? _desired;
    private readonly ConcurrentDictionary<string, Lazy<Task<SurfaceRead>>> _reads = new(StringComparer.Ordinal);

    internal SettingStateResolver(
        SettingStateScope scope,
        IReadOnlyDictionary<string, BoundSurface> bound,
        IReadOnlyDictionary<string, SurfaceResolutionFailure> failures,
        IReadOnlyDictionary<string, IConfigAdapter> adapters,
        IReadOnlyDictionary<string, IConfigValueCodec> codecs,
        ServerSettingsSnapshot? desired)
    {
        _settings = scope.Settings.GroupBy(s => s.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _bound = bound;
        _failures = failures;
        _adapters = adapters;
        _codecs = codecs;
        _desired = desired;
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="settingKey"/> is not in the catalogue this resolver was bound to. An unknown key is a
    /// caller bug, not a deployment fact, so it is not folded into an "unreadable" state that would look
    /// identical to a genuinely unreachable surface.
    /// </exception>
    public async Task<SettingState> ResolveAsync(string settingKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);

        if (!_settings.TryGetValue(settingKey, out var setting))
        {
            throw new KeyNotFoundException(
                $"'{settingKey}' is not a setting in this server's catalogue, so it has no state to resolve.");
        }

        var desired = _desired is not null && _desired.Values.TryGetValue(settingKey, out var recorded)
            ? recorded.Value
            : null;

        string? authoritative = null;
        string? rendered = null;
        string? runtime = null;
        var unreadable = false;
        BoundSurface? renderedSurface = null;

        foreach (var binding in setting.Bindings)
        {
            ct.ThrowIfCancellationRequested();

            if (!_bound.TryGetValue(binding.SurfaceId, out var surface))
            {
                // Bound in the definition but not reachable on any session — a real gap, not an absence.
                // A binding naming a surface the profile never declared is the same story from the
                // operator's point of view: the value cannot be read.
                unreadable = true;
                continue;
            }

            var read = await ReadAsync(surface, ct).ConfigureAwait(false);
            if (read.Error is not null)
            {
                unreadable = true;
                continue;
            }

            var value = Extract(binding, surface, read);
            switch (surface.Surface.Role)
            {
                case SurfaceRole.Authoritative:
                    authoritative ??= value;
                    break;
                case SurfaceRole.Derived:
                    rendered ??= value;
                    renderedSurface ??= surface;
                    break;
                case SurfaceRole.Runtime:
                    runtime ??= value;
                    break;
                default:
                    break;
            }

            if (value is null)
            {
                // The surface parsed, but the key/member/pointer this binding names is not in it. That is
                // as much an unread column as an unopenable file: reporting "no drift" would be a guess.
                unreadable = true;
            }
        }

        var drift = DriftKind.None;
        if (Differs(desired, authoritative))
        {
            drift |= DriftKind.DesiredVsAuthoritative;
        }

        if (Differs(authoritative, rendered))
        {
            drift |= DriftKind.AuthoritativeVsRendered;
        }

        if (Differs(rendered, runtime))
        {
            drift |= DriftKind.RenderedVsRuntime;
        }

        if (unreadable)
        {
            drift |= DriftKind.Unreadable;
        }

        // A derived surface regenerates from its upstream on a restart, so an authoritative/rendered
        // disagreement on one that regenerates that way is not drift an operator has to act on — it is drift
        // waiting for a restart. A `manual` trigger is not that, and is deliberately excluded.
        var pendingRegeneration = drift.HasFlag(DriftKind.AuthoritativeVsRendered)
            && renderedSurface?.Declaration.Regeneration?.Kind
                is RegenerationKind.ContainerRestart or RegenerationKind.ProcessRestart;

        var (writable, notWritableReason) = Writability(setting);

        return new SettingState(
            Mask(setting, desired),
            Mask(setting, authoritative),
            Mask(setting, rendered),
            Mask(setting, runtime),
            drift,
            pendingRegeneration,
            writable,
            notWritableReason);
    }

    /// <summary>Whether this setting currently has a writable, authoritative, reachable binding — and if not, why not.</summary>
    private (bool Writable, string? Reason) Writability(SettingDescriptor setting)
    {
        if (setting.WritableSurface is not { } target)
        {
            return (false, "This setting declares no writable binding: every binding it has is read-only.");
        }

        if (!_bound.TryGetValue(target.SurfaceId, out var surface))
        {
            var reason = _failures.TryGetValue(target.SurfaceId, out var failure)
                ? $"{failure.Reason} {failure.RemediationHint}"
                : $"Surface '{target.SurfaceId}' is not reachable on any session opened for this server, so "
                    + "this setting cannot be written.";

            return (false, reason);
        }

        return surface.Surface.ServyxMayWrite
            ? (true, null)
            : (false,
                $"Surface '{target.SurfaceId}' is {surface.Surface.Role}, and Servyx never writes a surface "
                + "the workload itself generates.");
    }

    /// <summary>Reads, parses, and (when the surface declares a codec) decodes one surface, once per instance.</summary>
    private Task<SurfaceRead> ReadAsync(BoundSurface surface, CancellationToken ct)
    {
        var lazy = _reads.GetOrAdd(
            surface.Surface.Id,
            _ => new Lazy<Task<SurfaceRead>>(() => ReadUncachedAsync(surface, ct)));

        return lazy.Value;
    }

    private async Task<SurfaceRead> ReadUncachedAsync(BoundSurface surface, CancellationToken ct)
    {
        if (surface.Surface.Path is not { } path)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' resolved without a concrete path, so there is nothing to read.");
        }

        if (!_adapters.TryGetValue(surface.Surface.FormatId, out var adapter))
        {
            // ISurfaceResolver already refuses this case, so reaching it means the adapter set differs
            // between the two — reported rather than dereferenced.
            return SurfaceRead.Failed(
                $"No IConfigAdapter is registered for format '{surface.Surface.FormatId}', so surface "
                + $"'{surface.Surface.Id}' cannot be parsed.");
        }

        string raw;
        try
        {
            var stream = await surface.Session.Target.OpenReadAsync(path, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A derived surface that does not exist yet (the workload has never started) is the common
            // case here and is not an error — but it is emphatically not "the value is absent", either.
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' could not be read via "
                + $"{surface.Session.Description}: {ex.Message}");
        }

        ConfigDocument document;
        try
        {
            document = adapter.Parse(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' is not valid {surface.Surface.FormatId}: {ex.Message}");
        }

        if (surface.Surface.CodecId is not { } codecId)
        {
            return new SurfaceRead(document, Members: null, Error: null);
        }

        if (!_codecs.TryGetValue(codecId, out var codec))
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' declares codec '{codecId}', but no IConfigValueCodec is "
                + "registered for it.");
        }

        if (surface.Surface.CodecPath is not { } codecPath)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' declares codec '{codecId}' but no codecPath, so there is no "
                + "scalar to decode.");
        }

        if (ValueAt(document, codecPath) is not { } scalar)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' declares its codec at '{codecPath}', which the parsed "
                + $"document at '{path.Value}' does not contain.");
        }

        try
        {
            return new SurfaceRead(document, codec.Decode(scalar), Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}''s '{codecId}' codec could not decode the scalar at "
                + $"'{codecPath}': {ex.Message}");
        }
    }

    /// <summary>Pulls one binding's value out of an already-parsed surface, per how that binding addresses it.</summary>
    private static string? Extract(SettingBinding binding, BoundSurface surface, SurfaceRead read) => binding switch
    {
        // A flat key is the whole pointer for the formats that accept `key` addressing (dotenv, properties).
        SettingBinding.ByKey key => ValueAt(read.Document!, key.Key),

        // A codec member never touches the document's own spans: the value lives inside a single scalar the
        // codec already decoded, and a naive key/value read of that surface would return the whole blob.
        SettingBinding.ByMember member => read.Members is null
            ? null
            : read.Members.TryGetValue(member.Member, out var decoded)
                ? member.Unquote ? Unquote(decoded) : decoded
                : null,

        SettingBinding.ByPointer pointer => ValueAt(read.Document!, pointer.Pointer),

        _ => null,
    };

    /// <summary>
    /// The effective value registered at <paramref name="pointerPath"/>, or <see langword="null"/> when the
    /// document has no such value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off <see cref="ConfigDocument.Spans"/> rather than <see cref="ConfigDocument.Root"/>, because
    /// <c>Root</c> is an opaque, format-specific parse tree owned by whichever adapter produced it — reading
    /// it would mean one branch per adapter here, and a new adapter would silently read as "value absent".
    /// The span set is the one representation every adapter is contractually required to populate.
    /// </para>
    /// <para>
    /// Last span wins, matching <see cref="ConfigDocument.WithValue"/>'s duplicate-key semantics: the value a
    /// read sees must be the same one a later write would edit.
    /// </para>
    /// </remarks>
    private static string? ValueAt(ConfigDocument document, string pointerPath)
    {
        for (var i = document.Spans.Count - 1; i >= 0; i--)
        {
            if (string.Equals(document.Spans[i].Pointer.Path, pointerPath, StringComparison.Ordinal))
            {
                return Slice(document, document.Spans[i]);
            }
        }

        // Definition authors quote an INI section name inside a codecPath — '["/Script/…"].OptionSettings' —
        // while IniConfigAdapter's own pointers carry the section name bare. Both name the same value, so a
        // quoting difference must not read as "not present".
        var normalized = Unquoted(pointerPath);
        for (var i = document.Spans.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Unquoted(document.Spans[i].Pointer.Path), normalized, StringComparison.Ordinal))
            {
                return Slice(document, document.Spans[i]);
            }
        }

        return null;
    }

    private static string? Slice(ConfigDocument document, ConfigSpan span)
    {
        if (span.LineIndex < 0 || span.LineIndex >= document.RawLines.Count)
        {
            return null;
        }

        var line = document.RawLines[span.LineIndex];
        return span.ValueStart < 0 || span.ValueLength < 0 || span.ValueStart + span.ValueLength > line.Length
            ? null
            : line.Substring(span.ValueStart, span.ValueLength);
    }

    private static string Unquoted(string pointerPath) => pointerPath.Replace("\"", string.Empty, StringComparison.Ordinal)
        .Replace("'", string.Empty, StringComparison.Ordinal);

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '"' or '\''
            ? trimmed[1..^1]
            : value;
    }

    /// <summary>
    /// Two columns disagree only when both were actually read. A column that could not be read is reported
    /// through <see cref="DriftKind.Unreadable"/>; calling it drift as well would tell an operator a value
    /// changed when what happened is that it could not be seen.
    /// </summary>
    private static bool Differs(string? left, string? right) =>
        left is not null && right is not null && !string.Equals(left, right, StringComparison.Ordinal);

    private static string? Mask(SettingDescriptor setting, string? value) =>
        !setting.IsSecret ? value : value is null ? null : SecretMask;

    /// <summary>One surface's parse result, or the reason it has none.</summary>
    /// <param name="Document">The parsed document, or null when <paramref name="Error"/> is set.</param>
    /// <param name="Members">The codec-decoded members, when the surface declares a codec.</param>
    /// <param name="Error">Why this surface could not be read, phrased for an operator. Null on success.</param>
    private sealed record SurfaceRead(ConfigDocument? Document, IReadOnlyDictionary<string, string>? Members, string? Error)
    {
        public static SurfaceRead Failed(string error) => new(Document: null, Members: null, error);
    }
}

/// <summary>
/// The <see cref="IServerConfigSessionSource"/> registered when nothing else is: it opens no session for any
/// server.
/// </summary>
/// <remarks>
/// The same reasoning as <see cref="UnconfiguredSurfaceResolutionContextSource"/>. A placeholder that
/// refuses keeps <see cref="ServiceCollectionExtensions.AddServyxConfig"/> self-contained and lets a bound
/// resolver answer "unreadable, and here is why" per setting, rather than failing container validation with
/// a message about a missing service that tells an operator nothing about game servers.
/// </remarks>
public sealed class UnconfiguredServerConfigSessionSource : IServerConfigSessionSource
{
    /// <inheritdoc />
    public Task<ServerConfigSessions?> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        return Task.FromResult<ServerConfigSessions?>(null);
    }
}
