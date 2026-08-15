using Servyx.Application.Servers;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// A controllable, state-carrying <see cref="IServerAdoptionService"/> fake for
/// <c>AdoptionPanel</c>/<c>ServersList</c> bUnit tests. <see cref="Candidates"/>/<see cref="Tracked"/> seed
/// the initial lists; <see cref="AdoptAsync"/>/<see cref="ForgetAsync"/> record every call in
/// <see cref="AdoptCalls"/>/<see cref="ForgetCalls"/> and, by default, mutate the two lists the same honest
/// way the real service would (a successful adopt moves a candidate into tracked; a successful forget
/// removes it) — so a test can assert against re-rendered state without re-implementing the service.
/// </summary>
public sealed class FakeServerAdoptionService : IServerAdoptionService
{
    /// <summary>Seed/backing list for <see cref="ListCandidatesAsync"/>.</summary>
    public List<AdoptionCandidate> Candidates { get; } = [];

    /// <summary>Seed/backing list for <see cref="ListTrackedAsync"/>.</summary>
    public List<TrackedServer> Tracked { get; } = [];

    /// <summary>Every <c>(containerId, gameDefinitionId)</c> pair <see cref="AdoptAsync"/> was called with, in call order.</summary>
    public List<(string ContainerId, string GameDefinitionId)> AdoptCalls { get; } = [];

    /// <summary>Every id <see cref="ForgetAsync"/> was called with, in call order.</summary>
    public List<ServerId> ForgetCalls { get; } = [];

    /// <summary>Overrides the result <see cref="AdoptAsync"/> returns; defaults to always succeeding.</summary>
    public Func<string, string, AdoptionResult>? AdoptResultFactory { get; set; }

    /// <summary>Overrides the result <see cref="ForgetAsync"/> returns; defaults to always succeeding.</summary>
    public Func<ServerId, ForgetResult>? ForgetResultFactory { get; set; }

    /// <summary>Every <c>(containerId, actor)</c> pair <see cref="RebindAsync"/> was called with, in call order.</summary>
    public List<(string ContainerId, string? Actor)> RebindCalls { get; } = [];

    /// <summary>Overrides the result <see cref="RebindAsync"/> returns; defaults to always succeeding as "palworld".</summary>
    public Func<string, RebindResult>? RebindResultFactory { get; set; }

    /// <summary>
    /// When set, <see cref="ListTrackedAsync"/> returns <see cref="TrackedServersResult.Failed"/> with this
    /// detail instead of reading <see cref="Tracked"/> — for tests proving <c>AdoptionPanel</c> renders an
    /// honest "tracking unavailable" state rather than an empty one when the read fails.
    /// </summary>
    public string? TrackedFailureDetail { get; set; }

    /// <summary>
    /// When set, <see cref="ListCandidatesAsync"/> returns <see cref="CandidatesResult.Failed"/> with this
    /// detail instead of reading <see cref="Candidates"/> — for tests proving <c>AdoptionPanel</c> renders an
    /// honest "adoption candidates unavailable" state rather than an empty one when discovery fails.
    /// </summary>
    public string? CandidatesFailureDetail { get; set; }

    /// <inheritdoc />
    public Task<CandidatesResult> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult(CandidatesFailureDetail is not null
            ? CandidatesResult.Failed(CandidatesFailureDetail)
            : CandidatesResult.Ok(Candidates.ToList()));

    /// <inheritdoc />
    public Task<TrackedServersResult> ListTrackedAsync(CancellationToken ct = default) =>
        Task.FromResult(TrackedFailureDetail is not null
            ? TrackedServersResult.Failed(TrackedFailureDetail)
            : TrackedServersResult.Ok(Tracked.ToList()));

    /// <inheritdoc />
    public Task<AdoptionResult> AdoptAsync(
        string containerId, string gameDefinitionId, string? actor = null, CancellationToken ct = default)
    {
        AdoptCalls.Add((containerId, gameDefinitionId));

        var result = AdoptResultFactory?.Invoke(containerId, gameDefinitionId) ?? AdoptionResult.Adopted(ServerId.New());
        if (result.Outcome == AdoptionOutcome.Adopted)
        {
            var candidate = Candidates.FirstOrDefault(c => c.ContainerId == containerId);
            Candidates.RemoveAll(c => c.ContainerId == containerId);
            Tracked.Add(new TrackedServer(
                result.ServerId!.Value, candidate?.Name ?? containerId, gameDefinitionId, AdoptionMode.Adopted, ServerWriteMode.ReadOnly));
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<ForgetResult> ForgetAsync(ServerId id, string? actor = null, CancellationToken ct = default)
    {
        ForgetCalls.Add(id);

        var result = ForgetResultFactory?.Invoke(id) ?? ForgetResult.Forgotten();
        if (result.Outcome == ForgetOutcome.Forgotten)
        {
            Tracked.RemoveAll(t => t.Id == id);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RebindResult> RebindAsync(string containerId, string? actor = null, CancellationToken ct = default)
    {
        RebindCalls.Add((containerId, actor));

        var result = RebindResultFactory?.Invoke(containerId) ?? RebindResult.Rebound("palworld", "Palworld Dedicated Server");
        return Task.FromResult(result);
    }
}
