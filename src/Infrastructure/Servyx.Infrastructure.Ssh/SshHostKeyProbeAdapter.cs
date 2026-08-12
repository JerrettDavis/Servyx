using Servyx.Domain.Hosts;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// The <see cref="IHostKeyProbe"/> implementation, over <see cref="SshHostKeyProbe"/>.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter and nothing more: it exists so an Application-layer use case can depend on the capability
/// ("observe whatever key this endpoint presents") without depending on SSH.NET, on this project, or on
/// <see cref="SshEndpoint"/>'s parsing rules. All the actual probing behaviour — including the structural
/// guarantee that the probe can never grant trust — lives in <see cref="SshHostKeyProbe"/>; see its remarks.
/// </para>
/// <para>
/// <strong>Turns an unparseable endpoint into a result, not an exception.</strong>
/// <see cref="SshEndpoint.Parse"/> throws <see cref="ArgumentException"/> for a malformed endpoint string, and
/// <see cref="SshHostKeyProbe"/> calls it before it has anything to report against. A malformed address typed
/// into a registration form is an ordinary, expected mistake, so it is mapped to
/// <see cref="HostKeyObservationStatus.InvalidEndpoint"/> here rather than being allowed to surface as a
/// crash — the same "expected outcomes are results" convention the rest of this path follows. Genuine faults
/// still propagate.
/// </para>
/// </remarks>
public sealed class SshHostKeyProbeAdapter : IHostKeyProbe
{
    private readonly TimeSpan? _timeout;

    /// <summary>Creates an adapter using <see cref="SshHostKeyProbe"/>'s own default timeout.</summary>
    public SshHostKeyProbeAdapter()
    {
    }

    /// <summary>Creates an adapter that probes with an explicit <paramref name="timeout"/>.</summary>
    public SshHostKeyProbeAdapter(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The probe timeout must be positive.");
        }

        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<HostKeyObservation> ObserveAsync(string endpoint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        SshHostKeyProbeResult result;
        try
        {
            result = _timeout is null
                ? await SshHostKeyProbe.ProbeAsync(endpoint, ct).ConfigureAwait(false)
                : await SshHostKeyProbe.ProbeAsync(endpoint, _timeout.Value, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return HostKeyObservation.InvalidEndpoint(endpoint, ex.Message);
        }

        // Reached implies both are populated (see SshHostKeyProbeResult), but the null checks make that a
        // compile-time fact rather than a comment: a future change that reported Reached without a key would
        // degrade to an honest "unreachable" here instead of throwing inside the caller's trust check.
        if (result.Status == SshHostKeyProbeStatus.Reached
            && result.Algorithm is not null
            && result.Sha256Fingerprint is not null
            && result.PublicKeyBlob is not null)
        {
            return HostKeyObservation.Observed(
                result.Host, result.Port, result.Algorithm, result.Sha256Fingerprint, result.PublicKeyBlob);
        }

        return HostKeyObservation.Unreachable(
            result.Host, result.Port, result.FailureReason ?? "The host did not present a usable host key.");
    }
}
