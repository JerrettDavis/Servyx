using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// The outcome of <see cref="IProvisioningDashboard.DetectDriftAsync"/>: either the maintainer compared the
/// live resource against the recorded handle and produced a <see cref="DriftResult"/>, or the provisioner
/// cannot answer drift questions at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why not just <c>DriftResult?</c>.</strong> For exactly the reason
/// <see cref="PlanUpdateResult"/> is a closed hierarchy rather than a nullable plan: "this adapter does not
/// implement <see cref="IMaintainer"/>" and "this resource has drifted" are different answers, and a null
/// would let a UI render the first as an absence of drift. <see cref="DriftResult.Matches"/> is defined as
/// "nothing diverged", so a caller that treated a missing answer as a default-constructed one would be
/// reporting a clean resource it never looked at.
/// </para>
/// <para>
/// Nothing here mutates. <see cref="IMaintainer.DetectDriftAsync"/> is a read, so a caller may compute one
/// freely and show it before anyone has approved anything.
/// </para>
/// </remarks>
public abstract record DriftCheckResult
{
    // Private so the case set is closed to this file, matching PlanUpdateResult and UpdateApplyResult.
    private DriftCheckResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>The maintainer compared the live resource against the recorded handle. Nothing was changed.</summary>
    public sealed record Checked : DriftCheckResult
    {
        /// <summary>Creates a checked result.</summary>
        /// <param name="result">The drift result the maintainer produced.</param>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        public Checked(DriftResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            Result = result;
        }

        /// <summary>Every divergence found, and therefore whether the resource still matches.</summary>
        public DriftResult Result { get; }

        /// <inheritdoc />
        public override string Message => Result.Summary;
    }

    /// <summary>
    /// The provisioner is registered, but does not implement <see cref="IMaintainer"/>, so it cannot say
    /// whether the resource has drifted. Nothing was read and nothing was changed.
    /// </summary>
    /// <remarks>
    /// Established by the same type test <see cref="IProvisioningDashboard.PlanUpdateAsync"/> uses, and
    /// reported rather than thrown for the same reason: capability is a question a UI legitimately asks
    /// about every provisioner it lists, not an exceptional condition.
    /// </remarks>
    public sealed record Unsupported : DriftCheckResult
    {
        /// <summary>Creates an unsupported result.</summary>
        /// <param name="provisionerId">The provisioner that does not implement <see cref="IMaintainer"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="provisionerId"/> is null, empty, or whitespace.</exception>
        public Unsupported(string provisionerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);

            ProvisionerId = provisionerId;
        }

        /// <summary>The provisioner that cannot answer drift questions.</summary>
        public string ProvisionerId { get; }

        /// <inheritdoc />
        public override string Message =>
            $"Provisioner '{ProvisionerId}' does not support maintenance: it implements no drift detection, so "
            + "whether this resource still matches what Servyx provisioned is unknown — not confirmed clean.";
    }
}
