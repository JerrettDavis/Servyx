namespace Servyx.Domain.Hosts;

/// <summary>Whether a host-key observation actually saw a key.</summary>
public enum HostKeyObservationStatus
{
    /// <summary>The host was reached and presented a key; every key-describing property is populated.</summary>
    Observed,

    /// <summary>The host could not be reached at all; see <see cref="HostKeyObservation.FailureReason"/>.</summary>
    Unreachable,

    /// <summary>The endpoint string could not be parsed at all, so nothing was ever dialled.</summary>
    InvalidEndpoint,
}

/// <summary>
/// What a single <see cref="IHostKeyProbe.ObserveAsync"/> call saw: either the key a remote endpoint actually
/// presented, or an honest reason why nothing was seen. Producing one of these NEVER grants trust — see
/// <see cref="IHostKeyProbe"/>.
/// </summary>
/// <remarks>
/// Deliberately carries <see cref="PublicKeyBlob"/> as well as <see cref="Sha256Fingerprint"/>. The blob is
/// what <see cref="Servyx.Domain.Connectors.HostKeyRecord"/> requires, so a caller that has confirmed a
/// fingerprint with a human can pin exactly the key that was observed rather than reconstructing a record
/// around a fingerprint string of unknown provenance.
/// </remarks>
public sealed record HostKeyObservation
{
    private HostKeyObservation(
        HostKeyObservationStatus status,
        string host,
        int port,
        string? algorithm,
        string? sha256Fingerprint,
        byte[]? publicKeyBlob,
        string? failureReason)
    {
        Status = status;
        Host = host;
        Port = port;
        Algorithm = algorithm;
        Sha256Fingerprint = sha256Fingerprint;
        PublicKeyBlob = publicKeyBlob;
        FailureReason = failureReason;
    }

    /// <summary>Whether a key was actually observed.</summary>
    public HostKeyObservationStatus Status { get; }

    /// <summary>The host that was probed, as the probe resolved it from the endpoint string.</summary>
    public string Host { get; }

    /// <summary>The port that was probed. Zero when <see cref="Status"/> is <see cref="HostKeyObservationStatus.InvalidEndpoint"/>.</summary>
    public int Port { get; }

    /// <summary>The key algorithm the host offered (e.g. <c>"ssh-ed25519"</c>), or <see langword="null"/> unless <see cref="Status"/> is <see cref="HostKeyObservationStatus.Observed"/>.</summary>
    public string? Algorithm { get; }

    /// <summary>
    /// The observed key's fingerprint in OpenSSH's <c>SHA256:...</c> display form, or <see langword="null"/>
    /// unless <see cref="Status"/> is <see cref="HostKeyObservationStatus.Observed"/>. This is the value a
    /// human confirms out of band.
    /// </summary>
    public string? Sha256Fingerprint { get; }

    /// <summary>The raw public key blob the host presented, or <see langword="null"/> unless <see cref="Status"/> is <see cref="HostKeyObservationStatus.Observed"/>.</summary>
    public byte[]? PublicKeyBlob { get; }

    /// <summary>Why nothing was observed. Null when <see cref="Status"/> is <see cref="HostKeyObservationStatus.Observed"/>.</summary>
    public string? FailureReason { get; }

    /// <summary>Creates an <see cref="HostKeyObservationStatus.Observed"/> observation.</summary>
    public static HostKeyObservation Observed(string host, int port, string algorithm, string sha256Fingerprint, byte[] publicKeyBlob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Fingerprint);
        ArgumentNullException.ThrowIfNull(publicKeyBlob);

        return new HostKeyObservation(HostKeyObservationStatus.Observed, host, port, algorithm, sha256Fingerprint, publicKeyBlob, failureReason: null);
    }

    /// <summary>Creates an <see cref="HostKeyObservationStatus.Unreachable"/> observation.</summary>
    public static HostKeyObservation Unreachable(string host, int port, string failureReason) =>
        new(HostKeyObservationStatus.Unreachable, host, port, algorithm: null, sha256Fingerprint: null, publicKeyBlob: null, failureReason);

    /// <summary>Creates an <see cref="HostKeyObservationStatus.InvalidEndpoint"/> observation for an unparseable endpoint string.</summary>
    public static HostKeyObservation InvalidEndpoint(string endpoint, string failureReason) =>
        new(HostKeyObservationStatus.InvalidEndpoint, endpoint, port: 0, algorithm: null, sha256Fingerprint: null, publicKeyBlob: null, failureReason);
}

/// <summary>
/// Observes the host key a remote endpoint presents, without ever granting trust as a side effect.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> The only implementation that can honour the word
/// "observes" is one that speaks the wire protocol, and every infrastructure project references
/// <c>Servyx.Domain</c> and nothing else, by design. An abstraction infrastructure must <em>implement</em>
/// therefore has to be declared here — exactly the reasoning <see cref="IHostRepository"/> already spells out.
/// <c>Servyx.Infrastructure.Ssh</c> supplies the real implementation (<c>SshHostKeyProbeAdapter</c>, over
/// <c>SshHostKeyProbe</c>).
/// </para>
/// <para>
/// <strong>Looking is not trusting.</strong> An implementation must never pin, revoke, or otherwise mutate
/// <see cref="Servyx.Domain.Connectors.IHostKeyStore"/> state. This interface exists so a human can be shown a
/// fingerprint and confirm it out of band <em>before</em> anything pins it.
/// </para>
/// </remarks>
public interface IHostKeyProbe
{
    /// <summary>
    /// Observes whatever host key <paramref name="endpoint"/> presents. Never throws for an ordinary
    /// connectivity failure or a malformed endpoint string — both are reported through
    /// <see cref="HostKeyObservation.Status"/>.
    /// </summary>
    Task<HostKeyObservation> ObserveAsync(string endpoint, CancellationToken ct = default);
}
