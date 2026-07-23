namespace Servyx.Domain.Connectors;

/// <summary>
/// How a connection should decide whether to trust a remote host's presented key.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>AcceptAny</c> case, and no bypass flag anywhere in this hierarchy — this is the entire
/// point of making <see cref="TrustPolicy"/> a closed, abstract record rather than an options bag with a
/// boolean.</b> A previous project of this codebase's author shipped the equivalent of OpenSSH's
/// <c>StrictHostKeyChecking=no</c> not because anyone decided that was an acceptable risk, but because the
/// type system happened to allow a configuration value that meant "skip verification". The fix here is
/// structural: make that state unrepresentable. If you find yourself wanting to add a case, a boolean, or a
/// nullable <see cref="IHostKeyVerifier"/> that means "skip" — don't. That desire is the design working as
/// intended, not a gap in it.
/// </para>
/// <para>
/// <see cref="TrustOnFirstUse"/> does not mean "trust the first thing you see and remember it silently". It
/// means the same as <see cref="RequirePinned"/> does when the host is unknown — <see cref="HostKeyVerdict.Unknown"/>
/// — the difference is purely what the caller is expected to do about that verdict: <see cref="RequirePinned"/>
/// means refuse and stop, <see cref="TrustOnFirstUse"/> means show a human the fingerprint and let them decide
/// whether to call <see cref="IHostKeyStore.PinAsync"/>. Auto-pinning on first sight would just be
/// trust-on-first-<i>connection</i> misspelled as trust-on-first-<i>use</i>, and it is exactly how people
/// MITM themselves.
/// </para>
/// </remarks>
public abstract record TrustPolicy
{
    private TrustPolicy()
    {
    }

    /// <summary>
    /// The default posture for anything automated: a connector with no pinned fingerprint for its target
    /// simply cannot connect. An unknown host yields <see cref="HostKeyVerdict.Unknown"/>, which the caller
    /// must treat as a hard refusal.
    /// </summary>
    public sealed record RequirePinned : TrustPolicy;

    /// <summary>
    /// The human-in-the-loop path for first contact with a host: an unknown host still yields
    /// <see cref="HostKeyVerdict.Unknown"/> — it is not auto-pinned — so that a person can be shown the
    /// fingerprint, confirm it out of band, and pin it via a separate, explicit
    /// <see cref="IHostKeyStore.PinAsync"/> call.
    /// </summary>
    public sealed record TrustOnFirstUse : TrustPolicy;

    /// <summary>
    /// Trusts a presented key only if its <see cref="HostKeyFingerprint.ComputeSha256"/> fingerprint appears
    /// in <see cref="Sha256"/>, compared in constant time. Useful when a fingerprint is known and pinned out
    /// of band (e.g. baked into an infrastructure-as-code definition) rather than through the persistent
    /// <see cref="IHostKeyStore"/>.
    /// </summary>
    /// <param name="Sha256">The set of acceptable <c>SHA256:...</c> fingerprints.</param>
    public sealed record PinnedFingerprints(IReadOnlyList<string> Sha256) : TrustPolicy;
}
