using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// The answer to "can this container be rolled back, and to what?". Producing one reads the live container
/// and computes; it mutates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The refusals are separate cases on purpose.</strong> "There is no record of what this container
/// was" is a different fact from "the container is gone" and from "this is not my resource", and only the
/// first of them is a statement about Servyx's own bookkeeping. Collapsing them into a null or a bare
/// <see langword="false"/> would leave an operator unable to tell a container that was never updated from one
/// whose record could not be read.
/// </para>
/// <para>
/// <strong>There is no case that means "roll back anyway".</strong> A rollback with no recorded prior state
/// has nothing to restore, and this type gives no way to describe one. See the remarks on
/// <see cref="ServyxResourceTags.PreviousSpecLabel"/> for why nothing else in Servyx can supply the missing
/// record.
/// </para>
/// </remarks>
public abstract record DockerRollbackPlan
{
    // Private so the case set is closed to this file, matching UpdateExecutionResult's discipline.
    private DockerRollbackPlan()
    {
    }

    /// <summary>A human-readable statement of the answer, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>
    /// A rollback is possible, and this is exactly what it would do. Nothing has been applied: the plan is as
    /// inert as any other <see cref="UpdatePlan"/>, and running it requires
    /// <c>DockerContainerProvisioner.PrepareRollbackAsync</c> with this plan's hash and its data impact.
    /// </summary>
    public sealed record Planned : DockerRollbackPlan
    {
        /// <summary>Creates a planned rollback.</summary>
        /// <param name="plan">What the rollback would change, how, and what it would do to persistent data.</param>
        /// <param name="message">A one-line summary, shown to the user verbatim.</param>
        public Planned(UpdatePlan plan, string message)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            Plan = plan;
            Message = message;
        }

        /// <summary>
        /// The rollback as an ordinary <see cref="UpdatePlan"/> — the same shape, the same invariants, and the
        /// same honestly-asserted <see cref="UpdatePlan.DataImpact"/> an update to the recorded prior spec
        /// would carry, because that is precisely what it is.
        /// </summary>
        public UpdatePlan Plan { get; }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>
    /// The container exists and is Servyx's, but nothing recorded what it was before — so there is nothing to
    /// restore. <strong>This is the answer for a container that has never been updated</strong>, and for one
    /// that has already been rolled back.
    /// </summary>
    public sealed record NoRecordedPriorState : DockerRollbackPlan
    {
        /// <summary>Creates a "nothing recorded" answer.</summary>
        /// <param name="message">Why there is nothing to roll back to. Shown to the user verbatim.</param>
        public NoRecordedPriorState(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>The engine no longer knows the container, so there is nothing to roll back.</summary>
    public sealed record ResourceGone : DockerRollbackPlan
    {
        /// <summary>Creates a "resource gone" answer.</summary>
        /// <param name="message">What was looked for and not found. Shown to the user verbatim.</param>
        public ResourceGone(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>
    /// This adapter will not plan a rollback for the handle it was given — it belongs to another provisioner,
    /// or the recorded prior state could not be read. <strong>No engine call is made for the first case.</strong>
    /// </summary>
    public sealed record Refused : DockerRollbackPlan
    {
        /// <summary>Creates a refusal.</summary>
        /// <param name="message">Why nothing was planned. Shown to the user verbatim.</param>
        public Refused(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }
}

/// <summary>
/// The result of asking this adapter to confirm a recreate — an update or a rollback — against a plan the
/// caller has already approved. Either the caller is handed the operation to run, or it is told why nothing
/// will run.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Preparing is still not applying.</strong> Producing a <see cref="Ready"/> issues no mutating engine
/// call: it re-reads the live container, recomputes the plan from it, and checks the approval and the
/// data-impact acknowledgement against that recomputation. The returned
/// <see cref="IProvisioningOperation"/> is inert until <c>Servyx.Application</c>'s
/// <c>ProvisioningExecutor</c> drives it, which is what puts the write-ahead ledger row in front of the first
/// mutating call — so the rollback is recorded before it happens, exactly as a create is.
/// </para>
/// <para>
/// <strong>A <see cref="Refused"/> is a statement about the daemon's state.</strong> Every check runs before
/// any stop, remove, or create, so a refusal guarantees the container is exactly as it was.
/// </para>
/// <para>
/// <strong>There is no force parameter, here or below it.</strong> No argument turns a refusal into an
/// attempt, and there is no overload that skips the approved hash or the acknowledgement.
/// </para>
/// </remarks>
public abstract record DockerRecreateConfirmation
{
    private DockerRecreateConfirmation()
    {
    }

    /// <summary>A human-readable statement of the outcome, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>Every check passed. The operation is ready to be run through the plan executor.</summary>
    public sealed record Ready : DockerRecreateConfirmation
    {
        /// <summary>Creates a confirmed recreate.</summary>
        /// <param name="operation">The inert operation that carries the recreate out.</param>
        /// <param name="plan">The plan as recomputed from the live container, whose hash matched the approval.</param>
        /// <param name="message">What will run when the operation is executed. Shown to the user verbatim.</param>
        public Ready(IProvisioningOperation operation, UpdatePlan plan, string message)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            Operation = operation;
            Plan = plan;
            Message = message;
        }

        /// <summary>The operation to hand to the plan executor. Nothing has been created or removed yet.</summary>
        public IProvisioningOperation Operation { get; }

        /// <summary>The plan this operation will carry out, recomputed from the live container.</summary>
        public UpdatePlan Plan { get; }

        /// <inheritdoc />
        public override string Message { get; }
    }

    /// <summary>
    /// The adapter declined. <strong>No mutating engine call of any kind was made</strong>, so the container is
    /// exactly what it was before the attempt.
    /// </summary>
    public sealed record Refused : DockerRecreateConfirmation
    {
        /// <summary>Creates a refusal.</summary>
        /// <param name="message">Why nothing will run. Shown to the user verbatim.</param>
        public Refused(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            Message = message;
        }

        /// <inheritdoc />
        public override string Message { get; }
    }
}
