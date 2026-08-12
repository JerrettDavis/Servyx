namespace Servyx.Application.Hosts;

/// <summary>
/// Lets an operator register a remote SSH host with Servyx from the UI, see what is registered, and
/// deregister one — the machine-level counterpart to <c>IServerAdoptionService</c>'s container-level adopt /
/// view / forget.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three explicit steps, not one call.</strong> Registration is deliberately split into
/// <see cref="ProbeAsync"/> and <see cref="RegisterAsync"/> because the confirmation of a host key fingerprint
/// is a decision a <em>human</em> makes, out of band, between those two calls. Collapsing them into a single
/// "register this host" method would leave nothing but trust-on-first-use dressed up as a wizard: whatever key
/// answered the connection would be pinned, and nobody would have checked it against the value printed on the
/// server's console.
/// </para>
/// <para>
/// <strong>Every write here touches only Servyx's own state.</strong> The host key store, the secret store,
/// and the <c>Hosts</c> table. Nothing in this service issues a command to the remote machine — the only
/// network traffic it ever generates is the read-only key-exchange probe in <see cref="ProbeAsync"/>, which
/// structurally cannot authenticate (see <c>IHostKeyProbe</c>).
/// </para>
/// </remarks>
public interface IHostRegistrationService
{
    /// <summary>
    /// Step one: reach <paramref name="endpoint"/> and report the host key it presents, so an operator can
    /// confirm the fingerprint out of band. Grants no trust, writes nothing, and never throws for an ordinary
    /// connectivity failure or a malformed endpoint — both are reported through
    /// <see cref="HostProbeResult.Outcome"/>.
    /// </summary>
    /// <remarks>
    /// A successful probe is also recorded server-side, keyed by endpoint, for a short window. That record —
    /// not the fingerprint string a caller later hands back — is what <see cref="RegisterAsync"/> compares
    /// against. See its remarks.
    /// </remarks>
    Task<HostProbeResult> ProbeAsync(string endpoint, CancellationToken ct = default);

    /// <summary>
    /// Step two: register <paramref name="name"/> at <paramref name="endpoint"/>, but only if
    /// <paramref name="confirmedFingerprint"/> matches a host key <em>this server</em> observed
    /// <paramref name="endpoint"/> presenting. On success: pins the observed key, stores the private key (and
    /// passphrase, if any) in the secret store, persists the <c>Host</c> row, and invalidates the cached
    /// connection set so the host becomes discoverable without a restart.
    /// </summary>
    /// <param name="name">The operator-chosen host name. Must be unique, and must be a valid secret URN segment because it becomes the credential's scope id.</param>
    /// <param name="endpoint">The address to reach the host at, e.g. <c>"ssh:steam@10.0.0.4:22"</c> or <c>"10.0.0.4:2222"</c>.</param>
    /// <param name="confirmedFingerprint">The <c>SHA256:...</c> fingerprint the operator confirmed out of band. Checked, never trusted — see the remarks.</param>
    /// <param name="privateKeyBytes">The SSH private key's exact bytes.</param>
    /// <param name="passphrase">The private key's passphrase, if it has one.</param>
    /// <param name="actor">The authenticated operator's identity. Recorded against the secret write, the host key pin, and the host row.</param>
    /// <param name="ct">Cancels the registration.</param>
    /// <remarks>
    /// <para>
    /// <strong>The fingerprint is verified against a server-side observation, never taken on faith.</strong>
    /// This is the security property the whole feature rests on. <paramref name="confirmedFingerprint"/>
    /// arrives from a caller — a browser form, an HTTP handler, anything — and a caller that is compromised,
    /// buggy, or simply hostile could send any string it likes. If that string were pinned directly, an
    /// attacker who could influence the request would be handing Servyx a fingerprint of <em>their</em> key,
    /// and every future connection to that host would be silently accepted from a machine-in-the-middle. So
    /// this method never pins what it is given. It resolves an observation of the host key by its own means —
    /// the short-lived record left by <see cref="ProbeAsync"/> if one is still valid, otherwise a fresh probe
    /// performed right here — and uses <paramref name="confirmedFingerprint"/> for exactly one thing: a
    /// fixed-time equality check against that observation. What gets pinned is the observation's own
    /// algorithm, fingerprint, and public key blob. A mismatch is refused with
    /// <see cref="RegistrationOutcome.FingerprintNotConfirmed"/> and writes nothing at all.
    /// </para>
    /// <para>
    /// <strong>Expected outcomes are results; genuine faults are different.</strong> A duplicate name, an
    /// unreachable host, a mismatched fingerprint, an empty key — all are reported through
    /// <see cref="RegistrationResult"/>, the same convention <c>ServerAdoptionService</c> follows. A failure of
    /// the credential write or the key pin is reported as <see cref="RegistrationOutcome.Failed"/>, because
    /// those happen before any row exists and leave Servyx's own tables untouched. A failure of the final
    /// database write propagates as an exception: by then side effects exist, and the caller needs to know
    /// loudly rather than read a tidy result record.
    /// </para>
    /// <para>
    /// <strong>Failure after a partial write.</strong> Steps run in the order pin → store credential →
    /// persist row → invalidate, and the row is deliberately last, because the row is the only thing that
    /// makes the other two reachable. If the row write fails, what is left behind is an encrypted secret at a
    /// URN nothing references (inert, and overwritten by a retry under the same name) and a host key pin for a
    /// fingerprint the operator did confirm out of band. Neither is scrubbed: the credential importer is
    /// write-only by design, and <c>IHostKeyStore</c> offers no un-pin — only <c>RevokeAsync</c>, which would
    /// leave a durable revocation tombstone that actively blocks a subsequent legitimate registration of the
    /// same host. Leaving both in place is the smaller harm, and it is logged.
    /// </para>
    /// </remarks>
    Task<RegistrationResult> RegisterAsync(
        string name,
        string endpoint,
        string confirmedFingerprint,
        ReadOnlyMemory<byte> privateKeyBytes,
        string? passphrase,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Every host Servyx currently has registered, for display. Reports whether the read itself failed rather
    /// than flattening that into an indistinguishable empty list — see <see cref="RegisteredHostsResult"/>.
    /// </summary>
    Task<RegisteredHostsResult> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes Servyx's registration of <paramref name="name"/> — the <c>Host</c> row — and nothing else, then
    /// invalidates the cached connection set so the host stops being discovered.
    /// </summary>
    /// <remarks>
    /// Issues no command to the remote machine, and deliberately does not scrub the host's stored SSH
    /// credential or its pinned host key, mirroring what <c>ServerAdoptionService.ForgetAsync</c> does for an
    /// adopted container: "forget" means Servyx stops tracking it, never that Servyx reaches out and changes
    /// something. Concretely, both leftovers are inert and both are load-bearing on a re-register: an
    /// unreferenced secret cannot be resolved by any transport (nothing points at its URN once the row is
    /// gone) and is overwritten by a later registration under the same name, while the pinned key is a record
    /// of a trust decision a human made out of band — dropping it would mean the only mechanism available
    /// (<c>IHostKeyStore.RevokeAsync</c>) writes a revocation tombstone that says "this host is compromised",
    /// which is a materially different, and false, claim to make just because an operator tidied up a list.
    /// An operator who genuinely wants the key untrusted has revocation available as its own explicit act.
    /// </remarks>
    Task<DeregistrationResult> DeregisterAsync(string name, string actor, CancellationToken ct = default);
}
