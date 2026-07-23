namespace Servyx.Domain.Control;

/// <summary>
/// The user-facing "how much control does Servyx have over this server" slider. Each tier is satisfied
/// by <b>any</b> qualifying combination of the underlying <see cref="ControlCapability"/> mechanisms —
/// see the remarks on <see cref="ControlCapability"/> for why that matters.
/// </summary>
public enum ControlTier
{
    /// <summary>Servyx cannot even observe the server's runtime state.</summary>
    Blind = 0,

    /// <summary>Servyx can see the server but not change or control it.</summary>
    Observe = 1,

    /// <summary>Servyx can change settings and restart the server.</summary>
    Configure = 2,

    /// <summary>Servyx can run live commands, back up, and restore the server.</summary>
    Operate = 3,

    /// <summary>Servyx owns the deployment: ports, volumes, and container lifecycle.</summary>
    Provision = 4,
}

/// <summary>The full definition of a <see cref="ControlTier"/>: what is required to hold it and what it unlocks.</summary>
/// <param name="Tier">The tier being defined.</param>
/// <param name="Required">The requirement that must be satisfied for this tier to be held.</param>
/// <param name="Recommended">
/// Capabilities that are not required to hold this tier but whose absence means the tier is held in a
/// degraded form. See <see cref="ControlTiers.IsDegraded"/>.
/// </param>
/// <param name="UserSummary">A short, human-readable summary of what this tier means, shown in the UI.</param>
public sealed record TierDefinition(ControlTier Tier, CapabilityRequirement Required, ControlCapability Recommended, string UserSummary);

/// <summary>Describes what is missing to advance from one tier to the next.</summary>
/// <param name="Current">The tier currently held.</param>
/// <param name="Next">The next tier up.</param>
/// <param name="MissingAlternatives">The capabilities that would satisfy <see cref="Next"/>'s unmet requirement.</param>
/// <param name="Blockers">Deduplicated remediation hints for the missing capabilities, end-user-fixable items first.</param>
public sealed record TierGap(ControlTier Current, ControlTier Next, IReadOnlyList<ControlCapability> MissingAlternatives, IReadOnlyList<RemediationHint> Blockers);

/// <summary>
/// The canonical definitions of every <see cref="ControlTier"/>, and the operations for evaluating a
/// <see cref="ControlCapabilitySet"/> against them.
/// </summary>
public static class ControlTiers
{
    /// <summary>
    /// The <see cref="ControlTier.Observe"/> tier: Servyx can see the server but not change or control it.
    /// </summary>
    public static readonly TierDefinition Observe = new(
        ControlTier.Observe,
        new CapabilityRequirement.All(ControlCapability.ReadRuntimeState),
        ControlCapability.StreamLogs | ControlCapability.ReadMetrics | ControlCapability.ReadDerivedConfig,
        "Servyx can see this server but cannot change or control it.");

    /// <summary>
    /// The <see cref="ControlTier.Configure"/> tier: Servyx can change settings and restart the server.
    /// </summary>
    public static readonly TierDefinition Configure = new(
        ControlTier.Configure,
        new CapabilityRequirement.Every(
            Observe.Required,
            new CapabilityRequirement.All(ControlCapability.ReadAuthoritativeConfig),
            new CapabilityRequirement.AnyOf(ControlCapability.WriteAuthoritativeConfig, ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile),
            new CapabilityRequirement.All(ControlCapability.StartWorkload | ControlCapability.StopWorkloadGraceful)),
        ControlCapability.CreateBackup,
        "Servyx can change settings and restart this server.");

    /// <summary>
    /// The <see cref="ControlTier.Operate"/> tier: Servyx can run live commands, back up, and restore the server.
    /// </summary>
    public static readonly TierDefinition Operate = new(
        ControlTier.Operate,
        new CapabilityRequirement.Every(
            Configure.Required,
            new CapabilityRequirement.All(ControlCapability.CreateBackup | ControlCapability.RestoreBackup),
            new CapabilityRequirement.AnyOf(ControlCapability.ExecInWorkload, ControlCapability.ControlChannelWrite)),
        ControlCapability.SignalProcess | ControlCapability.KillWorkload | ControlCapability.InstallMods,
        "Servyx can run live commands, back up, and restore this server.");

    /// <summary>
    /// The <see cref="ControlTier.Provision"/> tier: Servyx owns the deployment (ports, volumes, container lifecycle).
    /// </summary>
    public static readonly TierDefinition Provision = new(
        ControlTier.Provision,
        new CapabilityRequirement.Every(
            Operate.Required,
            new CapabilityRequirement.All(ControlCapability.WriteComposeFile | ControlCapability.RecreateWorkload | ControlCapability.CreateWorkload)),
        ControlCapability.DestroyWorkload,
        "Servyx owns this deployment: ports, volumes, and container lifecycle.");

    /// <summary>Returns the <see cref="TierDefinition"/> for a given tier.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown for <see cref="ControlTier.Blind"/>, which has no requirement — it is the default when no
    /// other tier is satisfied — and for any undefined value.
    /// </exception>
    public static TierDefinition Definition(ControlTier tier) => tier switch
    {
        ControlTier.Observe => Observe,
        ControlTier.Configure => Configure,
        ControlTier.Operate => Operate,
        ControlTier.Provision => Provision,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "ControlTier.Blind has no TierDefinition; it is the default when no tier's requirement is satisfied."),
    };

    /// <summary>
    /// Evaluates the highest <see cref="ControlTier"/> satisfied by <paramref name="set"/>, checking from
    /// <see cref="ControlTier.Provision"/> down to <see cref="ControlTier.Observe"/> and returning
    /// <see cref="ControlTier.Blind"/> if none are satisfied.
    /// </summary>
    public static ControlTier Evaluate(ControlCapabilitySet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        if (Provision.Required.IsSatisfiedBy(set))
        {
            return ControlTier.Provision;
        }

        if (Operate.Required.IsSatisfiedBy(set))
        {
            return ControlTier.Operate;
        }

        if (Configure.Required.IsSatisfiedBy(set))
        {
            return ControlTier.Configure;
        }

        if (Observe.Required.IsSatisfiedBy(set))
        {
            return ControlTier.Observe;
        }

        return ControlTier.Blind;
    }

    /// <summary>
    /// True when <paramref name="set"/> holds <paramref name="tier"/> but is missing one or more of that
    /// tier's <see cref="TierDefinition.Recommended"/> capabilities. False if the tier is not held at all
    /// (that is a gap, not degradation) or if every recommended capability is present.
    /// </summary>
    public static bool IsDegraded(ControlCapabilitySet set, ControlTier tier)
    {
        ArgumentNullException.ThrowIfNull(set);

        if (tier == ControlTier.Blind)
        {
            return false;
        }

        var definition = Definition(tier);
        if (!definition.Required.IsSatisfiedBy(set))
        {
            return false;
        }

        return (definition.Recommended & ~set.Granted) != ControlCapability.None;
    }

    /// <summary>
    /// Describes what is missing to advance <paramref name="set"/> from its current tier to the next one.
    /// Returns null when the current tier is already <see cref="ControlTier.Provision"/>, the top of the scale.
    /// </summary>
    public static TierGap? GapToNext(ControlCapabilitySet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var current = Evaluate(set);
        if (current == ControlTier.Provision)
        {
            return null;
        }

        var next = (ControlTier)((int)current + 1);
        var definition = Definition(next);
        var missing = definition.Required.UnsatisfiedAlternatives(set);

        var blockers = missing
            .SelectMany(capability => set.Grants.TryGetValue(capability, out var grant) ? grant.Remediations : Array.Empty<RemediationHint>())
            .GroupBy(hint => hint.Code)
            .Select(group => group.First())
            .OrderBy(hint => hint.Actor)
            .ToArray();

        return new TierGap(current, next, missing, blockers);
    }
}
