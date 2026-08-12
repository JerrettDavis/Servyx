using Renci.SshNet;
using Renci.SshNet.Common;
using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Observes the host key a remote SSH endpoint presents, without ever granting trust as a side effect.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the human-in-the-loop step <see cref="TrustPolicy.TrustOnFirstUse"/>'s remarks describe:
/// showing an operator a fingerprint so they can confirm it out of band <i>before</i> anything calls
/// <see cref="IHostKeyStore.PinAsync"/>. It is deliberately not built on top of <see cref="HostKeyGate"/> or
/// <see cref="IHostKeyVerifier"/> — this type has no dependency on <see cref="IHostKeyStore"/> at all, so
/// there is no store reference through which it could pin, revoke, or otherwise mutate trust state even by
/// accident. It structurally cannot do anything but look.
/// </para>
/// <para>
/// SSH presents the server's host key during transport-layer key exchange, before the client ever attempts
/// user authentication (see <see cref="SshConnector.ConnectWithHostKeyGateAsync"/>'s remarks for the same
/// point). This probe relies on exactly that ordering: it wires SSH.NET's <c>HostKeyReceived</c> event,
/// captures the offered algorithm and key blob, and leaves <c>HostKeyEventArgs.CanTrust</c> at its default
/// <see langword="false"/> — which makes SSH.NET abort the key exchange itself, before user authentication is
/// ever attempted. The <see cref="ConnectionInfo"/> below therefore needs *an* <see cref="AuthenticationMethod"/>
/// only because the constructor requires one; a <see cref="NoneAuthenticationMethod"/> is used because it is
/// never actually exercised against the remote host.
/// </para>
/// </remarks>
public static class SshHostKeyProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Probes <paramref name="endpoint"/> (parsed via <see cref="SshEndpoint.Parse"/>) with a 10-second timeout.</summary>
    public static Task<SshHostKeyProbeResult> ProbeAsync(string endpoint, CancellationToken ct = default) =>
        ProbeAsync(endpoint, DefaultTimeout, ct);

    /// <summary>
    /// Probes <paramref name="endpoint"/> (parsed via <see cref="SshEndpoint.Parse"/>), returning a result
    /// that distinguishes "couldn't reach the host" from "reached it, here is the fingerprint it offered".
    /// Never throws for ordinary connectivity failures (unreachable host, connection refused, timeout) — see
    /// <see cref="SshHostKeyProbeResult.Unreachable"/>.
    /// </summary>
    public static async Task<SshHostKeyProbeResult> ProbeAsync(string endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        var (parsedEndpoint, _) = SshEndpoint.Parse(endpoint);

        var connectionInfo = new ConnectionInfo(
            parsedEndpoint.Host,
            parsedEndpoint.Port,
            "servyx-hostkey-probe",
            new NoneAuthenticationMethod("servyx-hostkey-probe"))
        {
            Timeout = timeout,
        };

        string? algorithm = null;
        byte[]? publicKeyBlob = null;

        void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
        {
            algorithm = e.HostKeyName;
            publicKeyBlob = e.HostKey;

            // Fail closed, unconditionally: this probe must never grant trust, so unlike HostKeyGate there is
            // no verdict-driven branch here that could ever flip this to true.
            e.CanTrust = false;
        }

        using var client = new SshClient(connectionInfo);
        client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);

            // Unreachable in practice: CanTrust staying false makes SSH.NET abort the key exchange itself, so
            // ConnectAsync returning normally would mean no host key was ever offered.
            client.Disconnect();
            return SshHostKeyProbeResult.Unreachable(parsedEndpoint.Host, parsedEndpoint.Port, "Connected without the remote host presenting a host key.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The expected path: SSH.NET aborting the key exchange because CanTrust was left false, right
            // after OnHostKeyReceived captured the offer. Any other connection failure (refused, unreachable,
            // timed out) lands here too, distinguished from the expected path by algorithm/publicKeyBlob
            // never having been set.
            return algorithm is not null && publicKeyBlob is not null
                ? SshHostKeyProbeResult.Reached(parsedEndpoint.Host, parsedEndpoint.Port, algorithm, HostKeyFingerprint.ComputeSha256(publicKeyBlob), publicKeyBlob)
                : SshHostKeyProbeResult.Unreachable(parsedEndpoint.Host, parsedEndpoint.Port, ex.Message);
        }
        finally
        {
            client.HostKeyReceived -= OnHostKeyReceived;
        }
    }
}

/// <summary>The outcome of <see cref="SshHostKeyProbe.ProbeAsync(string,CancellationToken)"/>.</summary>
public enum SshHostKeyProbeStatus
{
    /// <summary>The host was reached and offered a host key; <see cref="SshHostKeyProbeResult.Algorithm"/> and <see cref="SshHostKeyProbeResult.Sha256Fingerprint"/> are populated.</summary>
    Reached,

    /// <summary>The host could not be reached at all; see <see cref="SshHostKeyProbeResult.FailureReason"/>.</summary>
    Unreachable,
}

/// <summary>
/// The result of probing a remote SSH endpoint's host key. Exactly one of "reached, with a fingerprint" or
/// "unreachable, with a reason" — see <see cref="Status"/>.
/// </summary>
public sealed record SshHostKeyProbeResult
{
    private SshHostKeyProbeResult(SshHostKeyProbeStatus status, string host, int port, string? algorithm, string? sha256Fingerprint, byte[]? publicKeyBlob, string? failureReason)
    {
        Status = status;
        Host = host;
        Port = port;
        Algorithm = algorithm;
        Sha256Fingerprint = sha256Fingerprint;
        PublicKeyBlob = publicKeyBlob;
        FailureReason = failureReason;
    }

    /// <summary>Whether the host was reached.</summary>
    public SshHostKeyProbeStatus Status { get; }

    /// <summary>The host that was probed.</summary>
    public string Host { get; }

    /// <summary>The port that was probed.</summary>
    public int Port { get; }

    /// <summary>The key algorithm the host offered (e.g. <c>"ssh-ed25519"</c>), or <see langword="null"/> when <see cref="Status"/> is <see cref="SshHostKeyProbeStatus.Unreachable"/>.</summary>
    public string? Algorithm { get; }

    /// <summary>
    /// The offered key's fingerprint, in the same <c>SHA256:...</c> display form as
    /// <see cref="HostKeyFingerprint.ComputeSha256"/> and <see cref="HostKeyRecord.Sha256Fingerprint"/> — so it
    /// can be shown to an operator for out-of-band confirmation, and later fed directly into a
    /// <see cref="HostKeyRecord"/> for <see cref="IHostKeyStore.PinAsync"/> once they confirm it. Null when
    /// <see cref="Status"/> is <see cref="SshHostKeyProbeStatus.Unreachable"/>.
    /// </summary>
    public string? Sha256Fingerprint { get; }

    /// <summary>
    /// The raw public key blob the host presented, in the wire format the transport received it in — the same
    /// bytes <see cref="HostKeyRecord.PublicKeyBlob"/> requires, so a caller that has had the fingerprint above
    /// confirmed by a human can pin exactly the key that was observed rather than reconstructing a record
    /// around a fingerprint string alone. Null when <see cref="Status"/> is
    /// <see cref="SshHostKeyProbeStatus.Unreachable"/>. Carrying it grants no trust: this type is inert data,
    /// and the probe still holds no <see cref="IHostKeyStore"/> reference through which it could pin anything.
    /// </summary>
    public byte[]? PublicKeyBlob { get; }

    /// <summary>Why the host could not be reached. Null when <see cref="Status"/> is <see cref="SshHostKeyProbeStatus.Reached"/>.</summary>
    public string? FailureReason { get; }

    /// <summary>Creates a <see cref="SshHostKeyProbeStatus.Reached"/> result.</summary>
    public static SshHostKeyProbeResult Reached(string host, int port, string algorithm, string sha256Fingerprint, byte[] publicKeyBlob) =>
        new(SshHostKeyProbeStatus.Reached, host, port, algorithm, sha256Fingerprint, publicKeyBlob, failureReason: null);

    /// <summary>Creates a <see cref="SshHostKeyProbeStatus.Unreachable"/> result.</summary>
    public static SshHostKeyProbeResult Unreachable(string host, int port, string failureReason) =>
        new(SshHostKeyProbeStatus.Unreachable, host, port, algorithm: null, sha256Fingerprint: null, publicKeyBlob: null, failureReason);
}
