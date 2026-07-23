using System.Collections.Concurrent;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// A minimal in-memory <see cref="ISecretStore"/> for integration tests, standing in for the real
/// DataProtection-backed store (which needs ASP.NET Core hosting infrastructure this test project doesn't
/// otherwise pull in). Behaves correctly with respect to the interface's contract (leases are independent
/// copies; disposing a returned lease must not corrupt the stored value for the next <see cref="GetAsync"/>).
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _values = new();

    public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default) =>
        Task.FromResult(_values.ContainsKey(urn.Value));

    public Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
    {
        if (!_values.TryGetValue(urn.Value, out var stored))
        {
            return Task.FromResult<SecretLease?>(null);
        }

        // Return a copy: SecretLease.Dispose() zeroes its buffer, which must never corrupt the stored value.
        var copy = (byte[])stored.Clone();
        return Task.FromResult<SecretLease?>(new SecretLease(copy));
    }

    public Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
    {
        _values[urn.Value] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
    {
        _values.TryRemove(urn.Value, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default)
    {
        IReadOnlyList<SecretUrn> result = _values.Keys
            .Select(k => SecretUrn.TryParse(k, out var urn) ? urn : (SecretUrn?)null)
            .Where(u => u is not null && u.Value.Scope == scope && u.Value.ScopeId == scopeId)
            .Select(u => u!.Value)
            .ToList();
        return Task.FromResult(result);
    }
}
