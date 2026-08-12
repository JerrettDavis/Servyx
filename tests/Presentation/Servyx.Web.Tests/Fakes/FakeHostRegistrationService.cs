using Servyx.Application.Hosts;
using Servyx.Domain.Common;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// A controllable, state-carrying <see cref="IHostRegistrationService"/> fake for <c>HostRegistrationPanel</c>/
/// <c>RegisteredHostsPanel</c> bUnit tests, mirroring <see cref="FakeServerAdoptionService"/>'s shape:
/// <see cref="Hosts"/> seeds the registered-hosts list, every call is recorded for assertions, and — by
/// default — <see cref="RegisterAsync"/>/<see cref="DeregisterAsync"/> mutate <see cref="Hosts"/> the same
/// honest way the real service would, so a test can assert against re-rendered state without
/// re-implementing the service.
/// </summary>
public sealed class FakeHostRegistrationService : IHostRegistrationService
{
    /// <summary>Seed/backing list for <see cref="ListAsync"/>.</summary>
    public List<RegisteredHost> Hosts { get; } = [];

    /// <summary>Every endpoint <see cref="ProbeAsync"/> was called with, in call order.</summary>
    public List<string> ProbeCalls { get; } = [];

    /// <summary>Every registration attempt <see cref="RegisterAsync"/> was called with, in call order.</summary>
    public List<RegisterCall> RegisterCalls { get; } = [];

    /// <summary>Every <c>(name, actor)</c> pair <see cref="DeregisterAsync"/> was called with, in call order.</summary>
    public List<(string Name, string Actor)> DeregisterCalls { get; } = [];

    /// <summary>Overrides the result <see cref="ProbeAsync"/> returns; defaults to a fixed "Reached" observation.</summary>
    public Func<string, HostProbeResult>? ProbeResultFactory { get; set; }

    /// <summary>Overrides the result <see cref="RegisterAsync"/> returns; defaults to always succeeding.</summary>
    public Func<RegisterCall, RegistrationResult>? RegisterResultFactory { get; set; }

    /// <summary>Overrides the result <see cref="DeregisterAsync"/> returns; defaults to always succeeding.</summary>
    public Func<string, DeregistrationResult>? DeregisterResultFactory { get; set; }

    /// <summary>
    /// When set, <see cref="ListAsync"/> returns <see cref="RegisteredHostsResult.Failed"/> with this detail
    /// instead of reading <see cref="Hosts"/> — for tests proving <c>RegisteredHostsPanel</c> renders an
    /// honest "could not be read" state rather than an empty one when the read fails.
    /// </summary>
    public string? ListingFailureDetail { get; set; }

    /// <summary>One recorded <see cref="RegisterAsync"/> call, capturing everything a test might assert on — including the private key's byte length rather than its content, since the content must never be retained by a test double any more than by the real UI.</summary>
    public sealed record RegisterCall(
        string Name, string Endpoint, string ConfirmedFingerprint, int PrivateKeyByteCount, string? Passphrase, string Actor);

    /// <inheritdoc />
    public Task<HostProbeResult> ProbeAsync(string endpoint, CancellationToken ct = default)
    {
        ProbeCalls.Add(endpoint);

        var result = ProbeResultFactory?.Invoke(endpoint)
            ?? new HostProbeResult(HostProbeOutcome.Reached, "10.0.0.4", 22, "ssh-ed25519", "SHA256:AAAABBBBCCCCDDDDEEEEFFFF0000111122223333444", null);

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RegistrationResult> RegisterAsync(
        string name,
        string endpoint,
        string confirmedFingerprint,
        ReadOnlyMemory<byte> privateKeyBytes,
        string? passphrase,
        string actor,
        CancellationToken ct = default)
    {
        var call = new RegisterCall(name, endpoint, confirmedFingerprint, privateKeyBytes.Length, passphrase, actor);
        RegisterCalls.Add(call);

        var result = RegisterResultFactory?.Invoke(call) ?? RegistrationResult.Registered(HostId.New(), confirmedFingerprint);

        if (result.Outcome == RegistrationOutcome.Registered)
        {
            Hosts.Add(new RegisteredHost(
                result.HostId!.Value, name, endpoint, "requirePinned", confirmedFingerprint, Enabled: true, actor, DateTimeOffset.UtcNow));
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RegisteredHostsResult> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(ListingFailureDetail is not null
            ? RegisteredHostsResult.Failed(ListingFailureDetail)
            : RegisteredHostsResult.Ok(Hosts.ToList()));

    /// <inheritdoc />
    public Task<DeregistrationResult> DeregisterAsync(string name, string actor, CancellationToken ct = default)
    {
        DeregisterCalls.Add((name, actor));

        var result = DeregisterResultFactory?.Invoke(name) ?? DeregistrationResult.Deregistered();
        if (result.Outcome == DeregistrationOutcome.Deregistered)
        {
            Hosts.RemoveAll(h => h.Name == name);
        }

        return Task.FromResult(result);
    }
}
