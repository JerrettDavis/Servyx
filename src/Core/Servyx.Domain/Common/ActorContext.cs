namespace Servyx.Domain.Common;

/// <summary>
/// Distinguishes the kind of actor performing an action, for audit and authorization purposes.
/// </summary>
public enum ActorType
{
    /// <summary>
    /// A human operator authenticated through the web UI or CLI.
    /// </summary>
    User,

    /// <summary>
    /// Servyx itself, acting autonomously (e.g. a background reconciliation loop).
    /// </summary>
    System,

    /// <summary>
    /// An automated caller authenticated with a long-lived API key.
    /// </summary>
    ApiKey,

    /// <summary>
    /// An action triggered by a configured schedule (e.g. a nightly backup job).
    /// </summary>
    Schedule,
}

/// <summary>
/// Identifies who (or what) is performing an action, threaded through every mutating call so it can be
/// recorded on the resulting audit record and change receipt.
/// </summary>
/// <param name="ActorId">A stable identifier for the actor (user id, API key id, schedule id, or a system constant).</param>
/// <param name="ActorType">The kind of actor.</param>
/// <param name="DisplayName">A human-readable label for the actor, shown in the UI and audit log.</param>
/// <param name="CorrelationId">An identifier correlating this action with a broader request or trace, if any.</param>
public sealed record ActorContext(string ActorId, ActorType ActorType, string DisplayName, string? CorrelationId = null);
