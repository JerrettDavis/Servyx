using Servyx.Domain.Transport;

namespace Servyx.Web.Authentication;

/// <summary>
/// Every authentication decision Servyx makes, written to <see cref="ILogger"/> under one category and one
/// set of stable event ids.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole audit trail, and it is not durable.</strong> Servyx has no audit table, no
/// append-only event store, and no audit sink of any kind — the provisioning ledger records infrastructure
/// intent, not access — so nothing here is invented to look like one. These events go to whatever logging
/// providers the host has configured and live exactly as long as those do. An operator who needs a durable,
/// tamper-evident record of sign-ins must ship these logs somewhere that provides one.
/// </para>
/// <para>
/// <strong>No event in this class ever carries the submitted password, or any part of it.</strong> A failed
/// attempt records that a failure happened, the reason class, and the remote address — never the value that
/// was tried, because a rejected password is very often a correct password for something else.
/// </para>
/// </remarks>
public static class AuthenticationAudit
{
    /// <summary>A password was accepted and a session cookie was issued.</summary>
    public static readonly EventId SignInSucceeded = new(6001, nameof(SignInSucceeded));

    /// <summary>A password was submitted and rejected. No session was created.</summary>
    public static readonly EventId SignInFailed = new(6002, nameof(SignInFailed));

    /// <summary>The one-time first-run bootstrap ran and set the operator password.</summary>
    public static readonly EventId InitialPasswordSet = new(6003, nameof(InitialPasswordSet));

    /// <summary>
    /// A first-run "set password" submission arrived when a password already existed. Refused: the bootstrap
    /// flow is one-time, and this is what it looks like when someone tries to reuse it as a way in.
    /// </summary>
    public static readonly EventId InitialPasswordRefused = new(6004, nameof(InitialPasswordRefused));

    /// <summary>A session was ended by the operator.</summary>
    public static readonly EventId SignedOut = new(6005, nameof(SignedOut));

    /// <summary>A login submission failed antiforgery validation and was not evaluated at all.</summary>
    public static readonly EventId AntiforgeryRejected = new(6006, nameof(AntiforgeryRejected));

    /// <summary>
    /// Startup found the one combination that is worse than either of its halves: no authentication, and a
    /// provisioner that can create billable infrastructure.
    /// </summary>
    public static readonly EventId UnauthenticatedProvisioning = new(6007, nameof(UnauthenticatedProvisioning));

    /// <summary>Startup found authentication switched off, with or without provisioning.</summary>
    public static readonly EventId AuthenticationDisabled = new(6008, nameof(AuthenticationDisabled));

    /// <summary>Startup found at least one server granted a non-read-only <see cref="WriteMode"/>.</summary>
    public static readonly EventId WriteModeGranted = new(6009, nameof(WriteModeGranted));

    /// <summary>
    /// Startup found the write-mode equivalent of <see cref="UnauthenticatedProvisioning"/>: no
    /// authentication, and at least one server granted <see cref="WriteMode.Enabled"/> — an anonymous caller
    /// can mutate it.
    /// </summary>
    public static readonly EventId UnauthenticatedWriteAccess = new(6010, nameof(UnauthenticatedWriteAccess));

    /// <summary>
    /// Startup migrated the pre-multi-user shared operator password into a bootstrap <c>Admin</c> user account,
    /// on an install that had a legacy operator password but no <c>User</c> rows yet. See
    /// <c>UserBootstrapMigration</c>.
    /// </summary>
    public static readonly EventId LegacyOperatorPasswordMigrated =
        new(6011, nameof(LegacyOperatorPasswordMigrated));
}
