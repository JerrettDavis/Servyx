namespace Servyx.Application.Provisioning;

/// <summary>
/// Thrown by <see cref="ProvisioningExecutor"/> when applying a provisioning operation fails. Carries the
/// ledger row the failure is recorded against so a caller — or a later orphan sweep — can pick the work
/// up. A provisioning failure is never swallowed or downgraded to a null/empty result: a partially
/// created billable resource must always be visible to the caller.
/// </summary>
public sealed class ProvisioningExecutionException : Exception
{
    /// <summary>Creates a <see cref="ProvisioningExecutionException"/> with a default message.</summary>
    public ProvisioningExecutionException()
        : base("Provisioning execution failed.")
    {
    }

    /// <summary>Creates a <see cref="ProvisioningExecutionException"/> with the given message.</summary>
    public ProvisioningExecutionException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ProvisioningExecutionException"/> with the given message and inner exception.</summary>
    public ProvisioningExecutionException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// Creates a <see cref="ProvisioningExecutionException"/> describing a failed execution against a
    /// specific ledger row.
    /// </summary>
    /// <param name="message">Human-readable description of what failed.</param>
    /// <param name="ledgerRowId">The write-ahead ledger row the failed attempt was recorded against.</param>
    /// <param name="compensated">
    /// Whether compensation (removal of the partial resource) completed without error. When
    /// <see langword="false"/>, the resource may still exist at the provider and the ledger row is
    /// deliberately left in <c>Intended</c> for a later sweep.
    /// </param>
    /// <param name="innerException">The underlying failure.</param>
    public ProvisioningExecutionException(string message, Guid ledgerRowId, bool compensated, Exception innerException)
        : base(message, innerException)
    {
        LedgerRowId = ledgerRowId;
        Compensated = compensated;
    }

    /// <summary>The write-ahead ledger row the failed attempt was recorded against, if known.</summary>
    public Guid LedgerRowId { get; }

    /// <summary>Whether compensation completed without error. <see langword="false"/> means an orphan may remain.</summary>
    public bool Compensated { get; }
}
