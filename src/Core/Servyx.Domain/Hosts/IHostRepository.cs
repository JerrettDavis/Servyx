using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Domain.Hosts;

/// <summary>
/// Durable storage for the <see cref="Host"/> rows Servyx's own registration path reads and writes — the
/// read/write surface behind "register a remote host", "view it", and "deregister it".
/// </summary>
/// <remarks>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> The only implementation that can honour the
/// word "durable" is one backed by a store, and every infrastructure project references
/// <c>Servyx.Domain</c> and nothing else, by design (see the defending comments in those projects' csproj
/// files). An abstraction infrastructure must <em>implement</em> therefore has to be declared here, exactly
/// the same reasoning <see cref="Servers.IServerRepository"/> already follows.
/// <c>Servyx.Infrastructure.Persistence</c> supplies the real, EF-backed implementation
/// (<c>EfHostRepository</c>, over the <c>Hosts</c> table).
/// </remarks>
public interface IHostRepository
{
    /// <summary>Every currently-tracked <see cref="Host"/> row, in no particular order.</summary>
    Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default);

    /// <summary>The tracked row for <paramref name="id"/>, or <see langword="null"/> if none exists.</summary>
    Task<Host?> TryGetAsync(HostId id, CancellationToken ct = default);

    /// <summary>The tracked row for <paramref name="name"/>, or <see langword="null"/> if none exists.</summary>
    Task<Host?> TryGetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Persists a newly-registered <see cref="Host"/> row.</summary>
    Task AddAsync(Host host, CancellationToken ct = default);

    /// <summary>
    /// Removes the tracked row for <paramref name="id"/>, if one exists. Returns <see langword="true"/> when
    /// a row was actually removed, <see langword="false"/> when none existed to remove. This method touches
    /// only Servyx's own storage — it has no way to reach, and must never be asked to reach, the host itself.
    /// </summary>
    Task<bool> RemoveAsync(HostId id, CancellationToken ct = default);
}
