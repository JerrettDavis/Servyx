using Servyx.Domain.Common;

namespace Servyx.Application.Hosts;

/// <summary>Which of the well-known outcomes <see cref="IHostRegistrationService.ProbeAsync"/> landed on.</summary>
public enum HostProbeOutcome
{
    /// <summary>The host was reached and presented a key; <see cref="HostProbeResult.Sha256Fingerprint"/> is populated.</summary>
    Reached,

    /// <summary>The host could not be reached at all; see <see cref="HostProbeResult.Detail"/>.</summary>
    Unreachable,

    /// <summary>The supplied endpoint string could not be parsed, so nothing was ever dialled.</summary>
    InvalidEndpoint,
}

/// <summary>
/// The display-facing half of one <see cref="IHostRegistrationService.ProbeAsync"/> call: what to show an
/// operator so they can confirm the host's identity out of band. Deliberately does NOT carry the raw public
/// key blob — the blob is retained server-side (see <see cref="IHostRegistrationService.RegisterAsync"/>'s
/// remarks) precisely so the fingerprint that eventually gets pinned never has to make a round trip through a
/// caller.
/// </summary>
/// <param name="Outcome">Which of the well-known outcomes this probe landed on.</param>
/// <param name="Host">The host the endpoint resolved to.</param>
/// <param name="Port">The port the endpoint resolved to. Zero for <see cref="HostProbeOutcome.InvalidEndpoint"/>.</param>
/// <param name="Algorithm">The key algorithm offered, when <paramref name="Outcome"/> is <see cref="HostProbeOutcome.Reached"/>; otherwise null.</param>
/// <param name="Sha256Fingerprint">
/// The offered key's fingerprint in OpenSSH's <c>SHA256:...</c> display form, when <paramref name="Outcome"/>
/// is <see cref="HostProbeOutcome.Reached"/>; otherwise null. This is the string the operator confirms and
/// hands back to <see cref="IHostRegistrationService.RegisterAsync"/>.
/// </param>
/// <param name="Detail">Why nothing was observed, for the non-<see cref="HostProbeOutcome.Reached"/> outcomes; otherwise null.</param>
public sealed record HostProbeResult(
    HostProbeOutcome Outcome,
    string Host,
    int Port,
    string? Algorithm,
    string? Sha256Fingerprint,
    string? Detail);

/// <summary>
/// A <see cref="Servyx.Domain.Entities.Host"/> row Servyx already tracks, for the "view what is registered"
/// half of this service. Carries no credential material and no credential URN — a caller rendering a list of
/// hosts has no need for either, and omitting them keeps a display surface from becoming a place a secret
/// locator can leak from.
/// </summary>
/// <param name="Id">The registered host's own id.</param>
/// <param name="Name">The operator-chosen name the host was registered under; unique across the table.</param>
/// <param name="Endpoint">The network address Servyx reaches this host at.</param>
/// <param name="TrustPolicy">How Servyx verifies this host's identity on connect.</param>
/// <param name="PinnedFingerprints">The host key fingerprint(s) pinned for this host, if any.</param>
/// <param name="Enabled">Whether this host is currently eligible for use.</param>
/// <param name="RegisteredBy">Who registered this host, if known.</param>
/// <param name="RegisteredAt">When this host row was created.</param>
public sealed record RegisteredHost(
    HostId Id,
    string Name,
    string Endpoint,
    string TrustPolicy,
    string? PinnedFingerprints,
    bool Enabled,
    string? RegisteredBy,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Result of listing registered hosts, distinguishing a genuine (possibly empty) listing from a failure to
/// produce one — the same "failed vs. genuinely empty" honesty <c>TrackedServersResult</c> already draws for
/// adopted servers. <see cref="ListingFailed"/> must never collapse into <see cref="Ok"/> with an empty list:
/// an operator seeing "no hosts registered" when the truth is "Servyx's own database could not be read" is a
/// false, and actively misleading, signal — and here it is worse than misleading, because the obvious next
/// action is to register the host again.
/// </summary>
/// <param name="Hosts">Every registered host found. Always empty when <paramref name="ListingFailed"/> is <see langword="true"/>.</param>
/// <param name="ListingFailed"><see langword="true"/> when the underlying read threw rather than returning (possibly empty) results.</param>
/// <param name="FailureDetail">The failing exception's message, when <paramref name="ListingFailed"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
public sealed record RegisteredHostsResult(IReadOnlyList<RegisteredHost> Hosts, bool ListingFailed, string? FailureDetail)
{
    /// <summary>The read succeeded; <paramref name="hosts"/> is the true (possibly empty) registered-host list.</summary>
    public static RegisteredHostsResult Ok(IReadOnlyList<RegisteredHost> hosts) => new(hosts, ListingFailed: false, FailureDetail: null);

    /// <summary>The read failed outright — the list could not be produced at all, not "read as empty".</summary>
    public static RegisteredHostsResult Failed(string? detail) => new([], ListingFailed: true, FailureDetail: detail);
}

/// <summary>Which of the well-known outcomes <see cref="IHostRegistrationService.RegisterAsync"/> landed on.</summary>
public enum RegistrationOutcome
{
    /// <summary>A new <see cref="Servyx.Domain.Entities.Host"/> row was created, its key pinned, and its credential stored.</summary>
    Registered,

    /// <summary>A host is already registered under the requested name; no second row was created and nothing was written.</summary>
    AlreadyExists,

    /// <summary>The requested name cannot be used — it is blank, too long, or contains characters that are not valid in a secret URN segment.</summary>
    InvalidName,

    /// <summary>The supplied endpoint string could not be parsed, so nothing was ever dialled.</summary>
    InvalidEndpoint,

    /// <summary>The host could not be reached to re-observe its key, so the confirmed fingerprint could not be checked against anything.</summary>
    HostUnreachable,

    /// <summary>
    /// The fingerprint the caller claimed the operator confirmed does not match the key Servyx itself observed
    /// this host presenting. Nothing was pinned, stored, or persisted — see
    /// <see cref="IHostRegistrationService.RegisterAsync"/>'s remarks for why this check exists.
    /// </summary>
    FingerprintNotConfirmed,

    /// <summary>The supplied private key material is unusable (empty), so nothing was written.</summary>
    InvalidCredential,

    /// <summary>
    /// A pre-database step (storing the credential, or pinning the observed key) failed. Servyx's own host
    /// table is unchanged, so a retry is safe.
    /// </summary>
    Failed,
}

/// <summary>
/// The outcome of one <see cref="IHostRegistrationService.RegisterAsync"/> call. Every member of
/// <see cref="RegistrationOutcome"/> is an expected, non-exceptional outcome — see that method's remarks for
/// which conditions instead throw.
/// </summary>
/// <param name="Outcome">Which of the well-known outcomes this call landed on.</param>
/// <param name="HostId">The new row's id, when <paramref name="Outcome"/> is <see cref="RegistrationOutcome.Registered"/>; the existing row's id for <see cref="RegistrationOutcome.AlreadyExists"/>; otherwise null.</param>
/// <param name="PinnedFingerprint">
/// The fingerprint that was actually pinned, when <paramref name="Outcome"/> is
/// <see cref="RegistrationOutcome.Registered"/>. Always Servyx's own observation, never the caller's claim —
/// echoed back so a caller can display exactly what was trusted.
/// </param>
/// <param name="Detail">A human-readable explanation for the non-success outcomes; otherwise null.</param>
public sealed record RegistrationResult(
    RegistrationOutcome Outcome,
    HostId? HostId,
    string? PinnedFingerprint,
    string? Detail)
{
    /// <summary>A new host row was created; <paramref name="id"/> is its id and <paramref name="pinnedFingerprint"/> is what Servyx pinned.</summary>
    public static RegistrationResult Registered(HostId id, string pinnedFingerprint) =>
        new(RegistrationOutcome.Registered, id, pinnedFingerprint, null);

    /// <summary>A host is already registered under this name; no second row was created and no secret was written.</summary>
    public static RegistrationResult AlreadyExists(string name, HostId existingId) =>
        new(RegistrationOutcome.AlreadyExists, existingId, null,
            $"A host is already registered under the name '{name}'. Deregister it first, or choose another name.");

    /// <summary>The requested name is not usable as a host name.</summary>
    public static RegistrationResult InvalidName(string detail) =>
        new(RegistrationOutcome.InvalidName, null, null, detail);

    /// <summary>The endpoint string could not be parsed.</summary>
    public static RegistrationResult InvalidEndpoint(string? detail) =>
        new(RegistrationOutcome.InvalidEndpoint, null, null, detail ?? "The endpoint could not be parsed.");

    /// <summary>The host could not be reached to re-observe its key.</summary>
    public static RegistrationResult HostUnreachable(string endpoint, string? detail) =>
        new(RegistrationOutcome.HostUnreachable, null, null,
            $"'{endpoint}' could not be reached to confirm its host key{(string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}")}");

    /// <summary>
    /// The claimed fingerprint did not match the one Servyx observed. Deliberately does NOT include either
    /// fingerprint in <see cref="Detail"/>: the caller already has the value it claimed, and echoing the
    /// observed one back into a refusal would hand a caller that is guessing the very answer this check exists
    /// to withhold.
    /// </summary>
    public static RegistrationResult FingerprintNotConfirmed() =>
        new(RegistrationOutcome.FingerprintNotConfirmed, null, null,
            "The confirmed fingerprint does not match the host key this server observed. Nothing was trusted or "
            + "stored. Probe the host again and confirm the fingerprint it actually presents.");

    /// <summary>The supplied private key material is unusable.</summary>
    public static RegistrationResult InvalidCredential(string detail) =>
        new(RegistrationOutcome.InvalidCredential, null, null, detail);

    /// <summary>A pre-database step failed; the host table is unchanged.</summary>
    public static RegistrationResult Failed(string? detail) =>
        new(RegistrationOutcome.Failed, null, null, detail);
}

/// <summary>Which of the well-known outcomes <see cref="IHostRegistrationService.DeregisterAsync"/> landed on.</summary>
public enum DeregistrationOutcome
{
    /// <summary>The <see cref="Servyx.Domain.Entities.Host"/> row was removed and the connection set invalidated.</summary>
    Deregistered,

    /// <summary>No host was registered under the given name; nothing to remove.</summary>
    NotFound,
}

/// <summary>
/// The outcome of one <see cref="IHostRegistrationService.DeregisterAsync"/> call. Never implies any command
/// was issued to the host itself, and never implies its stored credential or pinned key was scrubbed — see
/// that method's remarks.
/// </summary>
/// <param name="Outcome">Which of the well-known outcomes this call landed on.</param>
/// <param name="Detail">A human-readable explanation for the non-success outcome; otherwise null.</param>
public sealed record DeregistrationResult(DeregistrationOutcome Outcome, string? Detail)
{
    /// <summary>The row was removed. Servyx stops reaching the host; the host itself was never touched.</summary>
    public static DeregistrationResult Deregistered() => new(DeregistrationOutcome.Deregistered, null);

    /// <summary>No row existed under <paramref name="name"/>.</summary>
    public static DeregistrationResult NotFound(string name) =>
        new(DeregistrationOutcome.NotFound, $"No host is registered under the name '{name}'.");
}
