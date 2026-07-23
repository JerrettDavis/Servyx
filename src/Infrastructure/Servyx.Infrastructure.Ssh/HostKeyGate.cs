using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// The single choke point through which a presented SSH host key must pass before anything privileged
/// (opening an exec or sftp channel) is allowed to happen. Used directly inside the
/// <c>HostKeyReceived</c> handler wired onto SSH.NET's <c>SshClient</c>/<c>SftpClient</c> in
/// <see cref="SshConnector"/> — see the remarks there for why that makes host-key rejection a structural
/// guarantee (SSH.NET aborts the handshake itself; no channel object is ever constructed) rather than a
/// runtime check callers must remember to perform.
/// </summary>
public static class HostKeyGate
{
    /// <summary>
    /// Verifies the presented host key via <paramref name="verifier"/> and invokes
    /// <paramref name="onTrusted"/> — expected to grant the follow-on privileged action (e.g. setting
    /// SSH.NET's <c>HostKeyEventArgs.CanTrust</c> to <see langword="true"/>) — only when the verdict is
    /// <see cref="HostKeyVerdict.Trusted"/>. For every other verdict, <paramref name="onTrusted"/> is never
    /// invoked: a spy passed as <paramref name="onTrusted"/> that recorded whether it was called is the
    /// direct way to assert "no privileged action occurred" for a rejected host key.
    /// </summary>
    /// <returns>The verdict, so callers can build diagnostics or a <see cref="HostKeyRejectedException"/> from it.</returns>
    public static async Task<HostKeyVerdict> EnforceAsync(
        IHostKeyVerifier verifier,
        string host,
        int port,
        string algorithm,
        byte[] publicKeyBlob,
        TrustPolicy policy,
        Action onTrusted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(onTrusted);

        var verdict = await verifier.VerifyAsync(host, port, algorithm, publicKeyBlob, policy, ct).ConfigureAwait(false);

        if (verdict == HostKeyVerdict.Trusted)
        {
            onTrusted();
        }

        return verdict;
    }
}

/// <summary>
/// Thrown when an SSH connection is refused because the remote host's presented key was not
/// <see cref="HostKeyVerdict.Trusted"/>. Carries the verdict so callers (and the UI) can distinguish "never
/// seen this host" (<see cref="HostKeyVerdict.Unknown"/>) from the security-relevant "this host's key
/// changed" (<see cref="HostKeyVerdict.Changed"/>) and "explicitly revoked" (<see cref="HostKeyVerdict.Revoked"/>) cases.
/// </summary>
public sealed class HostKeyRejectedException : Exception
{
    /// <summary>Creates a <see cref="HostKeyRejectedException"/> for a specific host, port, and verdict.</summary>
    public HostKeyRejectedException(string host, int port, HostKeyVerdict verdict)
        : base($"Refusing to connect to '{host}:{port}': host key verdict was {verdict}, not Trusted.")
    {
        Host = host;
        Port = port;
        Verdict = verdict;
    }

    /// <summary>Creates a <see cref="HostKeyRejectedException"/> wrapping an inner exception (e.g. SSH.NET's own connection-abort exception).</summary>
    public HostKeyRejectedException(string host, int port, HostKeyVerdict verdict, Exception innerException)
        : base($"Refusing to connect to '{host}:{port}': host key verdict was {verdict}, not Trusted.", innerException)
    {
        Host = host;
        Port = port;
        Verdict = verdict;
    }

    /// <summary>The host that presented the rejected key.</summary>
    public string Host { get; }

    /// <summary>The port the key was presented on.</summary>
    public int Port { get; }

    /// <summary>The verdict that caused the refusal. Never <see cref="HostKeyVerdict.Trusted"/>.</summary>
    public HostKeyVerdict Verdict { get; }
}

/// <summary>
/// Thrown when an atomic file write cannot preserve the target's original owner (uid/gid) on the remote
/// host, and the write is therefore refused entirely rather than proceeding with an incorrect owner. See
/// <c>docs/control-plane.md</c>, "Config write ladder", rung 1: "the write should be refused rather than
/// proceeding with the wrong owner."
/// </summary>
public sealed class OwnershipPreservationFailedException : Exception
{
    /// <summary>Creates an <see cref="OwnershipPreservationFailedException"/> for a specific path and original owner.</summary>
    public OwnershipPreservationFailedException(string path, int? originalUid, int? originalGid, Exception innerException)
        : base(
            $"Refusing to write '{path}': could not preserve its original owner (uid={originalUid?.ToString() ?? "?"}, gid={originalGid?.ToString() ?? "?"}). " +
            "Writing with the wrong owner risks leaving the game process unable to read its own config after restart.",
            innerException)
    {
        Path = path;
        OriginalUid = originalUid;
        OriginalGid = originalGid;
    }

    /// <summary>The path whose owner could not be preserved.</summary>
    public string Path { get; }

    /// <summary>The uid the file was originally owned by, if known.</summary>
    public int? OriginalUid { get; }

    /// <summary>The gid the file was originally owned by, if known.</summary>
    public int? OriginalGid { get; }
}
