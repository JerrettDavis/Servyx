using Servyx.Web.Models;

namespace Servyx.Web.Services;

/// <summary>
/// Everything the <c>/settings</c> page reads and writes. Deliberately separate from
/// <see cref="IDashboardDataService"/>: that interface is about servers, games and backups, and application
/// settings share none of its collaborators.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The read side is one member, and stays one member.</strong>
/// <see cref="GetSettingsAsync"/> returns a collection of <see cref="SettingsSection"/> rather than a flat
/// record of every setting, so a later increment adding an identity, RBAC or audit-retention panel adds a
/// section type and nothing else — see <see cref="SettingsSection"/> for why that shape was chosen.
/// </para>
/// <para>
/// <strong>The write side is per-action.</strong> A section that can be acted on gets its own member with its
/// own typed result, because "rotate the operator password" and "sweep retention now" have nothing in common
/// beyond both being buttons: they take different arguments, fail in different ways, and one of them handles
/// a plaintext password. Collapsing them behind a generic <c>ApplyAsync(sectionId, payload)</c> would trade
/// compile-time honesty for a uniformity nothing needs.
/// </para>
/// </remarks>
public interface ISettingsDataService
{
    /// <summary>
    /// Every settings section this process can describe. A section whose backing service is not composed here
    /// is still returned, with its <c>Available</c> flag false, so the page can say "this process did not
    /// wire that up" rather than silently rendering one panel fewer.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    Task<SettingsView> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs one change-plan retention sweep now, instead of waiting for the next scheduled tick.
    /// </summary>
    /// <remarks>
    /// Applies exactly the rules the scheduled sweep applies — this is the same sweeper, not a second
    /// implementation of the policy — so it can only ever discard what the next tick would have discarded
    /// anyway. It is still destructive: a plan whose images go can no longer be reverted.
    /// </remarks>
    /// <param name="ct">Cancels the sweep.</param>
    Task<RetentionSweepResult> RunRetentionSweepAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces <paramref name="username"/>'s own password, for a caller that can already produce the current
    /// one.
    /// </summary>
    /// <remarks>
    /// A thin pass-through to <c>Servyx.Application.Users.IUserService.ChangePasswordAsync</c>, which verifies
    /// the current password against the stored PBKDF2 verifier and writes a freshly derived one to the same
    /// <c>User</c> row. No hashing, no format, and no storage decision is made here — this member exists only
    /// so the page never has to hold the user service itself. Self-service only: this changes the caller's
    /// <em>own</em> account, identified by <paramref name="username"/>, never an arbitrary other account —
    /// resetting someone else's password is Increment 4's Users management surface, not this page.
    /// </remarks>
    /// <param name="username">The signed-in caller's own username — the account being changed.</param>
    /// <param name="currentPassword">The password in force now. Verified before anything is written.</param>
    /// <param name="newPassword">The replacement.</param>
    /// <param name="ct">Cancels the rotation.</param>
    Task<OperatorPasswordChangeResult> ChangeOperatorPasswordAsync(
        string username, string currentPassword, string newPassword, CancellationToken ct = default);
}
