using Servyx.Domain.Secrets;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ISecretStore"/> that keeps every byte sequence ever handed to
/// <see cref="SetAsync"/>, so a test can inspect exactly what would have been persisted rather than
/// inferring it from behaviour.
/// </summary>
/// <remarks>
/// Deliberately stores and returns copies. <see cref="SecretLease"/> takes ownership of the array it is
/// given and zeroes it on disposal, so handing out the stored array itself would blank this fake's own
/// records the first time a caller used one — and a test asserting "the plaintext is not in what was
/// written" would then pass for entirely the wrong reason.
/// </remarks>
public sealed class RecordingSecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly List<byte[]> _writes = [];

    /// <summary>Every value ever written, in order, including overwrites of the same URN.</summary>
    public IReadOnlyList<byte[]> Writes => _writes;

    /// <summary>The actor recorded on the most recent write, or null if nothing has been written.</summary>
    public string? LastActor { get; private set; }

    /// <summary>How many times <see cref="SetAsync"/> was reached.</summary>
    public int SetCalls { get; private set; }

    public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default)
        => Task.FromResult(_values.ContainsKey(urn.Value));

    public Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
        => Task.FromResult(_values.TryGetValue(urn.Value, out var stored)
            ? new SecretLease([.. stored])
            : null);

    public Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        SetCalls++;
        LastActor = actor;

        var copy = value.ToArray();
        _values[urn.Value] = copy;
        _writes.Add([.. copy]);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
    {
        _values.Remove(urn.Value);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default)
    {
        var results = new List<SecretUrn>();
        foreach (var key in _values.Keys)
        {
            if (SecretUrn.TryParse(key, out var urn)
                && string.Equals(urn.Scope, scope, StringComparison.Ordinal)
                && string.Equals(urn.ScopeId, scopeId, StringComparison.Ordinal))
            {
                results.Add(urn);
            }
        }

        return Task.FromResult<IReadOnlyList<SecretUrn>>(results);
    }
}
