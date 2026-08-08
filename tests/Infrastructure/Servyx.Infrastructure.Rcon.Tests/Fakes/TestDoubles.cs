using System.Text;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Rcon.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ISecretStore"/> that records how many leases it handed out, so a test can assert
/// the credential was resolved at the point of use rather than cached.
/// </summary>
/// <remarks>
/// Returns a fresh copy each time: <see cref="SecretLease"/> takes ownership of the array it is given and
/// zeroes it on disposal, so handing out the stored array would blank this store's own record the first
/// time anyone used it.
/// </remarks>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    /// <summary>How many times a lease was resolved.</summary>
    internal int GetCalls { get; private set; }

    internal InMemorySecretStore With(SecretUrn urn, string value)
    {
        _values[urn.Value] = Encoding.UTF8.GetBytes(value);
        return this;
    }

    public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default) =>
        Task.FromResult(_values.ContainsKey(urn.Value));

    public Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
    {
        GetCalls++;
        return Task.FromResult(_values.TryGetValue(urn.Value, out var stored) ? new SecretLease([.. stored]) : null);
    }

    public Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
    {
        _values[urn.Value] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
    {
        _values.Remove(urn.Value);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SecretUrn>>([]);
}

/// <summary>Records every raw command the audited escape hatch issued.</summary>
internal sealed class RecordingAuditSink : IRconAuditSink
{
    internal List<string> Recorded { get; } = [];

    public Task RecordRawCommandAsync(RconEndpoint endpoint, string rawCommand, CancellationToken ct = default)
    {
        Recorded.Add(rawCommand);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IRconSession"/> that records what it was asked to do and answers however a test says,
/// including by throwing. Used where the subject is what the <em>caller</em> does with a control channel —
/// notably <c>DockerBackupProvider</c>'s quiesce ordering and its refusal to archive after a failure.
/// </summary>
internal sealed class ScriptedRconSession : IRconSession
{
    private readonly List<string> _journal;
    private readonly Func<string, RconResponse>? _respond;
    private readonly Exception? _throw;

    internal ScriptedRconSession(List<string>? journal = null, Func<string, RconResponse>? respond = null, Exception? fail = null)
    {
        _journal = journal ?? [];
        _respond = respond;
        _throw = fail;
    }

    /// <summary>The shared journal this session appends <c>quiesce:&lt;id&gt;</c> entries to.</summary>
    internal IReadOnlyList<string> Journal => _journal;

    /// <summary>The command ids invoked, in order.</summary>
    internal List<string> Invoked { get; } = [];

    public Task<RconResponse> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? args, CancellationToken ct = default)
    {
        Invoked.Add(commandId);
        _journal.Add($"control:{commandId}");

        if (_throw is not null)
        {
            return Task.FromException<RconResponse>(_throw);
        }

        return Task.FromResult(_respond?.Invoke(commandId) ?? new RconResponse("Complete Save", Success: true));
    }

    public Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default) =>
        throw new NotSupportedException("This double does not implement the raw escape hatch.");

    public Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default) =>
        Task.FromResult(new PlayerSnapshot(DateTimeOffset.UnixEpoch, PlayerListSnapshot.Roster([])));
}

/// <summary>A session that never answers, so a caller's own timeout is the thing under test.</summary>
internal sealed class HangingRconSession : IRconSession
{
    public async Task<RconResponse> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? args, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new System.Diagnostics.UnreachableException();
    }

    public Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
}
