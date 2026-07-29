namespace Servyx.Domain.Transport;

/// <summary>
/// Answers "what write posture does this specific target hold?" for the write guard.
/// </summary>
/// <remarks>
/// The question is asked per <see cref="TargetDescriptor"/> and never once per process: M4's shape is a
/// write toggle owned by one server, so there is deliberately no API here that could enable writes for
/// everything a transport can reach. A resolver that cannot identify the target must answer
/// <see cref="WriteMode.ReadOnly"/> — an unknown target is a read-only one, always.
/// </remarks>
public interface IWriteModeResolver
{
    /// <summary>Returns the write posture held for <paramref name="target"/>.</summary>
    /// <param name="target">The target about to be connected to.</param>
    WriteMode Resolve(TargetDescriptor target);
}

/// <summary>
/// The safe default: every target is <see cref="WriteMode.ReadOnly"/>. Registered by every transport DI
/// extension so that a host which configures nothing gets a process incapable of writing anywhere.
/// </summary>
public sealed class ReadOnlyWriteModeResolver : IWriteModeResolver
{
    /// <summary>The shared instance; the type is stateless.</summary>
    public static readonly ReadOnlyWriteModeResolver Instance = new();

    /// <inheritdoc />
    public WriteMode Resolve(TargetDescriptor target) => WriteMode.ReadOnly;
}

/// <summary>
/// One composition-root-authored statement that a specific target may be written to.
/// </summary>
/// <remarks>
/// <para>
/// A grant is matched against a <see cref="TargetDescriptor"/> by transport id, optionally by endpoint,
/// and optionally by required descriptor options — which is how a Docker grant names a single container
/// (<c>containerName</c>) and an SSH grant names a single host (its endpoint).
/// </para>
/// <para>
/// <b>A grant can never be transport-wide.</b> Constructing one that permits writing with no constraint
/// beyond <see cref="TransportId"/> throws: "enable writes for every container this daemon can see" is not
/// a sentence M4 allows anyone to say, including the composition root. Only a
/// <see cref="WriteMode.ReadOnly"/> grant may be unconstrained, and such a grant changes nothing, since
/// read-only is already the default.
/// </para>
/// </remarks>
public sealed class WriteModeGrant
{
    private readonly IReadOnlyDictionary<string, string> _requiredOptions;

    /// <summary>Creates a grant.</summary>
    /// <param name="mode">The posture granted to matching targets.</param>
    /// <param name="transportId">The <see cref="TargetDescriptor.TransportId"/> this grant applies to.</param>
    /// <param name="endpoint">The exact <see cref="TargetDescriptor.Endpoint"/> this grant applies to, or null for any.</param>
    /// <param name="requiredOptions">
    /// Descriptor options that must all be present with these exact values for the grant to match — e.g.
    /// <c>{ "containerName": "palworld-server" }</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="mode"/> permits writes but neither <paramref name="endpoint"/> nor
    /// <paramref name="requiredOptions"/> narrows the grant to a specific target.
    /// </exception>
    public WriteModeGrant(
        WriteMode mode,
        string transportId,
        string? endpoint = null,
        IReadOnlyDictionary<string, string>? requiredOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportId);

        _requiredOptions = requiredOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(requiredOptions, StringComparer.Ordinal);

        if (mode != WriteMode.ReadOnly && endpoint is null && _requiredOptions.Count == 0)
        {
            throw new ArgumentException(
                $"A {mode} grant must name a specific target: supply an endpoint, required descriptor options, " +
                "or both. Writes are enabled per server, never for everything a transport can reach.",
                nameof(mode));
        }

        Mode = mode;
        TransportId = transportId;
        Endpoint = endpoint;
    }

    /// <summary>The posture granted to matching targets.</summary>
    public WriteMode Mode { get; }

    /// <summary>The transport this grant applies to.</summary>
    public string TransportId { get; }

    /// <summary>The exact endpoint this grant applies to, or null when the grant is not endpoint-scoped.</summary>
    public string? Endpoint { get; }

    /// <summary>Descriptor options that must all match for this grant to apply.</summary>
    public IReadOnlyDictionary<string, string> RequiredOptions => _requiredOptions;

    /// <summary>Whether this grant applies to <paramref name="target"/>.</summary>
    /// <param name="target">The descriptor to test.</param>
    public bool Matches(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!string.Equals(target.TransportId, TransportId, StringComparison.Ordinal))
        {
            return false;
        }

        if (Endpoint is not null && !string.Equals(target.Endpoint, Endpoint, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (key, value) in _requiredOptions)
        {
            if (!target.Options.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Resolves a target's write posture from the set of <see cref="WriteModeGrant"/>s the composition root
/// registered, defaulting to <see cref="WriteMode.ReadOnly"/> when none matches.
/// </summary>
/// <remarks>
/// When several grants match the same target the <em>most restrictive</em> wins. Two grants disagreeing
/// about one target is a misconfiguration, and the only safe way to read a misconfiguration is the way that
/// writes least.
/// </remarks>
public sealed class GrantedWriteModeResolver : IWriteModeResolver
{
    private readonly IReadOnlyList<WriteModeGrant> _grants;

    /// <summary>Creates a resolver over <paramref name="grants"/>.</summary>
    /// <param name="grants">The grants to consult; an empty set makes every target read-only.</param>
    public GrantedWriteModeResolver(IEnumerable<WriteModeGrant>? grants) =>
        _grants = grants?.ToList() ?? [];

    /// <inheritdoc />
    public WriteMode Resolve(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var resolved = WriteMode.ReadOnly;
        var matched = false;

        foreach (var grant in _grants)
        {
            if (!grant.Matches(target))
            {
                continue;
            }

            resolved = matched ? (WriteMode)Math.Min((int)resolved, (int)grant.Mode) : grant.Mode;
            matched = true;
        }

        return resolved;
    }
}
