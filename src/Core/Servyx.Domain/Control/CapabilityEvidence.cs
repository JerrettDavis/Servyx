namespace Servyx.Domain.Control;

/// <summary>
/// How confident Servyx is that a given <see cref="ControlCapability"/> is actually held.
/// </summary>
/// <remarks>
/// The values are ordered from least to most confident (<see cref="Denied"/> &lt; <see cref="Unknown"/>
/// &lt; <see cref="Inferred"/> &lt; <see cref="Verified"/>) so callers merging grants for the same
/// capability can pick the highest value. <b>Do not read that ordering as "Unknown is worse than
/// Denied" in user-facing terms</b> — it is not. <see cref="Unknown"/> means Servyx could not determine
/// whether the capability is held (a probe was skipped, a transport was unavailable, a probe failed);
/// <see cref="Denied"/> means Servyx positively determined the capability is absent. Rendering
/// <see cref="Unknown"/> to a user as "no" is wrong and actively misleading: it would tell the operator
/// their working server cannot do something Servyx simply never got to check.
/// </remarks>
public enum CapabilityConfidence
{
    /// <summary>Servyx positively determined the capability is not held.</summary>
    Denied,

    /// <summary>
    /// Servyx could not determine whether the capability is held. This is not a "no" — see the
    /// type-level remarks.
    /// </summary>
    Unknown,

    /// <summary>The capability was not directly verified but is inferred from other evidence.</summary>
    Inferred,

    /// <summary>The capability was directly verified, e.g. by a successful probe.</summary>
    Verified,
}

/// <summary>
/// A single piece of evidence backing a <see cref="CapabilityGrant"/>, typically produced by one probe.
/// </summary>
/// <param name="ProbeId">Identifier of the probe (or other source) that produced this evidence.</param>
/// <param name="Summary">A short, human-readable summary of what was observed.</param>
/// <param name="Detail">Optional longer-form detail (e.g. raw probe output), for diagnostics.</param>
/// <param name="ObservedAt">When this evidence was observed.</param>
public sealed record CapabilityEvidence(string ProbeId, string Summary, string? Detail, DateTimeOffset ObservedAt);

/// <summary>
/// Who would need to act to unlock a missing capability.
/// </summary>
public enum RemediationActor
{
    /// <summary>The end user can fix this themselves (e.g. a settings toggle in Servyx).</summary>
    EndUser,

    /// <summary>Only the host's administrator can fix this (e.g. filesystem permissions, mounting a socket).</summary>
    HostAdmin,

    /// <summary>Servyx itself could fix this automatically, given approval (e.g. requesting a capability grant).</summary>
    Servyx,
}

/// <summary>
/// A suggested remediation for unlocking a missing or unknown <see cref="ControlCapability"/>.
/// </summary>
/// <param name="Code">A stable, documentable identifier for this remediation, e.g. <c>"SVX-CAP-0041"</c>.</param>
/// <param name="Summary">A short, human-readable explanation of what to do.</param>
/// <param name="SuggestedCommand">An optional literal command the actor could run.</param>
/// <param name="Actor">Who would need to act.</param>
/// <param name="Unlocks">The capability (or capabilities) this remediation would unlock.</param>
/// <param name="DocsUrl">Optional link to further documentation.</param>
public sealed record RemediationHint(
    string Code,
    string Summary,
    string? SuggestedCommand,
    RemediationActor Actor,
    ControlCapability Unlocks,
    Uri? DocsUrl)
{
    /// <summary>
    /// A generic remediation hint used when a capability's status is <see cref="CapabilityConfidence.Unknown"/>
    /// and no more specific remediation is available.
    /// </summary>
    /// <param name="capability">The capability this generic hint concerns.</param>
    public static RemediationHint Unknown(ControlCapability capability) => new(
        Code: "SVX-CAP-0000",
        Summary: "Servyx could not determine whether this capability is available. It may still work; try the action or re-run discovery.",
        SuggestedCommand: null,
        Actor: RemediationActor.Servyx,
        Unlocks: capability,
        DocsUrl: null);
}

/// <summary>
/// The result of evaluating a single <see cref="ControlCapability"/> for a server: whether it is held,
/// how confident Servyx is, and the evidence and remediations behind that conclusion.
/// </summary>
/// <param name="Capability">The capability (or combination of capabilities) this grant covers.</param>
/// <param name="Confidence">How confident Servyx is in this conclusion.</param>
/// <param name="Evidence">The evidence backing this conclusion.</param>
/// <param name="Remediations">Suggested remediations if the capability is missing or unknown.</param>
public sealed record CapabilityGrant(
    ControlCapability Capability,
    CapabilityConfidence Confidence,
    IReadOnlyList<CapabilityEvidence> Evidence,
    IReadOnlyList<RemediationHint> Remediations)
{
    /// <summary>
    /// Creates a grant asserting the capability is held, with <see cref="CapabilityConfidence.Verified"/>
    /// or <see cref="CapabilityConfidence.Inferred"/> confidence.
    /// </summary>
    /// <param name="capability">The capability (or combination) that is held.</param>
    /// <param name="confidence">Must be <see cref="CapabilityConfidence.Verified"/> or <see cref="CapabilityConfidence.Inferred"/>.</param>
    /// <param name="evidence">The evidence backing this conclusion.</param>
    /// <param name="remediations">Optional remediations, typically empty for a positive grant.</param>
    public static CapabilityGrant Granted(
        ControlCapability capability,
        CapabilityConfidence confidence,
        IReadOnlyList<CapabilityEvidence> evidence,
        IReadOnlyList<RemediationHint>? remediations = null)
    {
        if (confidence is not (CapabilityConfidence.Verified or CapabilityConfidence.Inferred))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Granted() requires Verified or Inferred confidence.");
        }

        return new CapabilityGrant(capability, confidence, evidence, remediations ?? []);
    }

    /// <summary>Creates a grant asserting the capability is positively not held.</summary>
    /// <param name="capability">The capability (or combination) that is denied.</param>
    /// <param name="evidence">The evidence backing this conclusion.</param>
    /// <param name="remediations">Suggested remediations for unlocking the capability.</param>
    public static CapabilityGrant Denied(
        ControlCapability capability,
        IReadOnlyList<CapabilityEvidence> evidence,
        IReadOnlyList<RemediationHint>? remediations = null)
        => new(capability, CapabilityConfidence.Denied, evidence, remediations ?? []);

    /// <summary>
    /// Creates a grant whose status could not be determined. Never use <see cref="Denied"/> for this —
    /// see the remarks on <see cref="CapabilityConfidence"/>.
    /// </summary>
    /// <param name="capability">The capability (or combination) whose status is unknown.</param>
    /// <param name="evidence">The evidence explaining why the status could not be determined.</param>
    /// <param name="remediations">Optional remediations; defaults to <see cref="RemediationHint.Unknown"/>.</param>
    public static CapabilityGrant Unknown(
        ControlCapability capability,
        IReadOnlyList<CapabilityEvidence> evidence,
        IReadOnlyList<RemediationHint>? remediations = null)
        => new(capability, CapabilityConfidence.Unknown, evidence, remediations ?? [RemediationHint.Unknown(capability)]);
}
