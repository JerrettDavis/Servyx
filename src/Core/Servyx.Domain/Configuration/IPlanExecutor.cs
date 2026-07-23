namespace Servyx.Domain.Configuration;

/// <summary>
/// The single funnel through which every mutation in the product passes. No other interface applies a
/// configuration write directly; a code path that mutates without a receipt is a bug.
/// </summary>
public interface IPlanExecutor
{
    /// <summary>
    /// Read-only. Produces a unified diff with secrets masked, a reversibility flag per action, the
    /// capabilities required, and any restart/recreate consequences.
    /// </summary>
    Task<ConfigChangePlan> PreviewAsync(string serverId, IReadOnlyDictionary<string, string> desiredValues, CancellationToken ct = default);

    /// <summary>
    /// Applies a previously previewed and approved plan by id. Throws <see cref="PlanStaleException"/> if
    /// any bound surface has drifted since preview, and <c>WritesDisabledException</c> if the server's
    /// write mode does not permit it.
    /// </summary>
    Task<ChangeReceipt> ApplyAsync(string planId, CancellationToken ct = default);

    /// <summary>Reverts a previously applied plan using its recorded pre-images.</summary>
    Task RevertAsync(string planId, CancellationToken ct = default);
}

/// <summary>
/// Thrown when <see cref="IPlanExecutor.ApplyAsync"/> is called against a plan whose bound surfaces have
/// drifted since preview.
/// </summary>
public sealed class PlanStaleException : Exception
{
    /// <summary>Creates a <see cref="PlanStaleException"/> with a default message.</summary>
    public PlanStaleException()
        : base("The plan's bound surfaces have drifted since it was previewed.")
    {
    }

    /// <summary>Creates a <see cref="PlanStaleException"/> with the given message.</summary>
    public PlanStaleException(string message) : base(message) { }

    /// <summary>Creates a <see cref="PlanStaleException"/> with the given message and inner exception.</summary>
    public PlanStaleException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="PlanStaleException"/> carrying the id of the stale plan.</summary>
    public PlanStaleException(string message, string planId) : base(message)
    {
        PlanId = planId;
    }

    /// <summary>The id of the plan that was found to be stale, if known.</summary>
    public string? PlanId { get; }
}

/// <summary>Record of a successfully applied plan.</summary>
/// <param name="PlanId">The plan that was applied.</param>
/// <param name="AppliedAt">When the plan was applied.</param>
/// <param name="Actions">The actions that were applied.</param>
public sealed record ChangeReceipt(string PlanId, DateTimeOffset AppliedAt, IReadOnlyList<PlannedAction> Actions);
