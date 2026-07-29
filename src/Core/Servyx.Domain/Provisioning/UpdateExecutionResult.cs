namespace Servyx.Domain.Provisioning;

/// <summary>
/// What actually happened when an adapter was asked to carry out an already-approved
/// <see cref="UpdatePlan"/>. Exactly one case is returned, and the four cases are separate types on purpose:
/// an operator has to be able to tell "the provider is still working on it" from "the provider said no".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Submission is not success.</strong> Every provider whose update is asynchronous — DigitalOcean's
/// droplet actions are the first in this codebase — answers the mutating request long before the mutation
/// has happened. <see cref="Completed"/> may therefore only be returned once the provider has been observed
/// reporting the operation finished; an accepted request that is still running is <see cref="TimedOut"/>,
/// which is deliberately not <see cref="Failed"/> and deliberately not a success.
/// </para>
/// <para>
/// <strong><see cref="Refused"/> means nothing was sent.</strong> It is the answer for a plan this adapter
/// will not execute — a plan belonging to another provisioner, a plan whose hash does not match the approval
/// handed in, or a plan describing an operation this adapter does not implement. No provider call is made on
/// that path, so a refusal is a guarantee about the provider's state and not merely a report about this
/// process's.
/// </para>
/// <para>
/// <strong>There is no case that means "apply anyway".</strong> As everywhere else on this path, there is no
/// force flag, no override, and no argument that turns a <see cref="Refused"/> into an attempt.
/// </para>
/// </remarks>
public abstract record UpdateExecutionResult
{
    // Private so the case set is closed to this file, matching UpdatePlan's discipline of making the
    // misleading combinations inexpressible rather than merely discouraged.
    private UpdateExecutionResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>
    /// The provider was asked, the operation ran, and the provider was <em>observed</em> reporting it
    /// finished. The resource was then re-read so the caller gets the state that now exists rather than the
    /// state that was requested.
    /// </summary>
    public sealed record Completed : UpdateExecutionResult
    {
        /// <summary>Creates a completed result.</summary>
        /// <param name="resource">The resource as the provider describes it after the operation.</param>
        /// <param name="message">What ran, stated in the adapter's own words.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty, or whitespace.</exception>
        public Completed(ProvisionedResource resource, string message)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            Resource = resource;
            Message = message;
        }

        /// <summary>The resource as it exists now, re-read from the provider after the operation finished.</summary>
        public ProvisionedResource Resource { get; }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>
    /// The provider refused the operation, or reported it as errored. <strong>The operation is over</strong> —
    /// unlike <see cref="TimedOut"/>, nothing is still running.
    /// </summary>
    public sealed record Failed : UpdateExecutionResult
    {
        /// <summary>Creates a failed result.</summary>
        /// <param name="message">
        /// The failure, carrying the provider's own words wherever the provider supplied any. Shown to the
        /// user verbatim.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty, or whitespace.</exception>
        public Failed(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>
    /// The provider accepted the operation and was still reporting it in progress when this adapter stopped
    /// waiting. <strong>Nothing is known to have failed and nothing is known to have succeeded</strong>: the
    /// operation may well still complete at the provider after this result is returned.
    /// </summary>
    /// <remarks>
    /// Its own case rather than a <see cref="Failed"/> with a different message, because the two demand
    /// opposite responses from an operator: a failure is retried, whereas retrying something that is still
    /// running submits the same mutation twice. That distinction is exactly what this type exists to keep.
    /// </remarks>
    public sealed record TimedOut : UpdateExecutionResult
    {
        /// <summary>Creates a still-running result.</summary>
        /// <param name="message">What was submitted, and what to do about it. Shown to the user verbatim.</param>
        /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty, or whitespace.</exception>
        public TimedOut(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>
    /// The adapter declined to execute the plan. <strong>No provider call of any kind was made</strong>, so
    /// the provider's state is exactly what it was before the attempt.
    /// </summary>
    public sealed record Refused : UpdateExecutionResult
    {
        /// <summary>Creates a refusal.</summary>
        /// <param name="message">Why the plan was not executed. Shown to the user verbatim.</param>
        /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty, or whitespace.</exception>
        public Refused(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }
}
