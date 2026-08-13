namespace Servyx.Web.Models;

/// <summary>
/// One panel's worth of application-level settings, as the <c>/settings</c> page renders it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a polymorphic collection rather than one flat settings DTO.</strong> Application settings are
/// not one subject: retention is a change-plan concern, host connections are a discovery concern, and the
/// operator password is an authentication concern. Each is backed by a different service, each can be
/// independently absent from a given process's composition, and each carries a payload the others have no
/// field in common with. A single flat record would force every future section — identity, RBAC, audit
/// retention — to widen the same type and every consumer to re-read it, which is exactly the reshaping this
/// shape exists to avoid: a new section is a new <see cref="SettingsSection"/> subtype plus one more entry in
/// <see cref="SettingsView.Sections"/>, and <see cref="Servyx.Web.Services.ISettingsDataService"/>'s read
/// member does not change.
/// </para>
/// <para>
/// Every section carries its own <c>Available</c> flag rather than being omitted when its backing service is
/// unregistered. Omission and "composed but empty" are different facts, and the page says which one it is —
/// the same honesty <c>RegisteredHostsResult.ListingFailed</c> draws between "no hosts" and "could not read
/// hosts".
/// </para>
/// </remarks>
/// <param name="Id">A stable identifier, used as the section's <c>data-testid</c> suffix and anchor.</param>
/// <param name="Title">The heading this section renders under.</param>
public abstract record SettingsSection(string Id, string Title);

/// <summary>Every settings section this process can describe, in the order the page renders them.</summary>
/// <param name="Sections">The sections. Never null; may be empty in a process that composed none of them.</param>
public sealed record SettingsView(IReadOnlyList<SettingsSection> Sections)
{
    /// <summary>A view with no sections at all.</summary>
    public static readonly SettingsView Empty = new([]);

    /// <summary>The single section of type <typeparamref name="T"/>, or <see langword="null"/> if absent.</summary>
    public T? Find<T>() where T : SettingsSection => Sections.OfType<T>().FirstOrDefault();
}

/// <summary>
/// How long an applied change plan's recorded configuration images — which hold secret values in plaintext —
/// are kept before the sweep discards them, after which that plan can no longer be reverted.
/// </summary>
/// <remarks>
/// Read-only by design. The window and the schedule are host configuration, resolved once at startup into the
/// immutable <c>ChangePlanRetentionOptions</c> the background sweeper was constructed with; there is no
/// runtime store behind them to write to, and inventing one here would create a second source of truth that
/// silently disagrees with <c>appsettings.json</c>. So this section reports the effective values and names the
/// exact keys to change them — the same treatment <c>WriteModeControl</c> gives
/// <c>Servyx:Provisioning:Enabled</c> — and offers the one thing that genuinely is a runtime action: running a
/// sweep now, through the already-composed sweeper.
/// </remarks>
/// <param name="Available">Whether this process composed a retention sweeper at all.</param>
/// <param name="Enabled">Whether the sweep runs. When false, nothing is ever purged and images are kept indefinitely.</param>
/// <param name="ImageRetention">How long a plan that took effect keeps its images after taking effect.</param>
/// <param name="SweepInterval">How often the sweep runs.</param>
/// <param name="ConfigurationSectionKey">The configuration section these values are read from.</param>
public sealed record RetentionSettingsSection(
    bool Available,
    bool Enabled,
    TimeSpan ImageRetention,
    TimeSpan SweepInterval,
    string ConfigurationSectionKey) : SettingsSection(SectionId, "Change plan retention")
{
    /// <summary>This section's stable id.</summary>
    public const string SectionId = "retention";

    /// <summary>No retention sweeper is composed in this process, so nothing here can be reported or run.</summary>
    public static RetentionSettingsSection Unavailable(string configurationSectionKey) =>
        new(Available: false, Enabled: false, TimeSpan.Zero, TimeSpan.Zero, configurationSectionKey);
}

/// <summary>
/// A read-only count of the SSH hosts Servyx has registered, so <c>/settings</c> can say whether any remote
/// host is wired up without duplicating a line of <c>/hosts</c>, which remains the only place they are
/// managed.
/// </summary>
/// <param name="Available">Whether this process composed a host registration service.</param>
/// <param name="RegisteredCount">How many hosts are registered. Always zero when <paramref name="ListingFailed"/>.</param>
/// <param name="EnabledCount">How many of those are enabled.</param>
/// <param name="ListingFailed">Whether the read itself failed, as distinct from finding nothing.</param>
/// <param name="FailureDetail">The failure's detail when <paramref name="ListingFailed"/>; otherwise null.</param>
public sealed record HostConnectionsSettingsSection(
    bool Available,
    int RegisteredCount,
    int EnabledCount,
    bool ListingFailed,
    string? FailureDetail) : SettingsSection(SectionId, "Host connections")
{
    /// <summary>This section's stable id.</summary>
    public const string SectionId = "hosts";

    /// <summary>No host registration service is composed in this process.</summary>
    public static readonly HostConnectionsSettingsSection Unavailable =
        new(Available: false, 0, 0, ListingFailed: false, FailureDetail: null);
}

/// <summary>
/// The state of the single operator credential, and what a rotation of it requires.
/// </summary>
/// <param name="Available">Whether this process composed the operator credential store.</param>
/// <param name="AuthenticationEnabled">Whether this process actually requires a login. A rotation still works when it does not, but changes nothing about who can reach the app.</param>
/// <param name="PasswordSet">Whether a password has ever been set. Rotation requires one; first-run bootstrap is <c>/login</c>'s job, never this page's.</param>
/// <param name="MinimumPasswordLength">The shortest new password the store accepts.</param>
/// <param name="AuthenticationConfigurationKey">The configuration key that turns the login requirement on and off.</param>
public sealed record OperatorCredentialSettingsSection(
    bool Available,
    bool AuthenticationEnabled,
    bool PasswordSet,
    int MinimumPasswordLength,
    string AuthenticationConfigurationKey) : SettingsSection(SectionId, "Operator password")
{
    /// <summary>This section's stable id.</summary>
    public const string SectionId = "operator-credential";

    /// <summary>No operator credential store is composed in this process.</summary>
    public static OperatorCredentialSettingsSection Unavailable(bool authenticationEnabled, string configurationKey) =>
        new(Available: false, authenticationEnabled, PasswordSet: false, 0, configurationKey);
}

/// <summary>Which of the well-known outcomes an operator-requested retention sweep landed on.</summary>
public enum RetentionSweepOutcome
{
    /// <summary>The sweep ran. The counts say what it did, and zeroes mean it genuinely found nothing to do.</summary>
    Swept,

    /// <summary>Retention is switched off in configuration, so nothing was swept and nothing was purged.</summary>
    Disabled,

    /// <summary>No retention sweeper is composed in this process.</summary>
    Unavailable,

    /// <summary>The sweep threw. Nothing was purged; the scheduled sweep will try again.</summary>
    Failed,
}

/// <summary>What an operator-requested retention sweep did.</summary>
/// <param name="Outcome">Which case this is.</param>
/// <param name="PlansMarkedStale">Expired plans promoted to stale.</param>
/// <param name="PlansPurged">Plans whose recorded images were discarded.</param>
/// <param name="ActionsPurged">Individual recorded images discarded.</param>
/// <param name="Detail">Failure detail, when there is one.</param>
public sealed record RetentionSweepResult(
    RetentionSweepOutcome Outcome,
    int PlansMarkedStale,
    int PlansPurged,
    int ActionsPurged,
    string? Detail)
{
    /// <summary>No retention sweeper is composed in this process.</summary>
    public static readonly RetentionSweepResult Unavailable =
        new(RetentionSweepOutcome.Unavailable, 0, 0, 0, null);

    /// <summary>Retention is switched off, so the sweeper declined to do anything.</summary>
    public static readonly RetentionSweepResult Disabled =
        new(RetentionSweepOutcome.Disabled, 0, 0, 0, null);
}

/// <summary>Which of the well-known outcomes an operator password rotation landed on.</summary>
public enum OperatorPasswordChangeOutcome
{
    /// <summary>The stored verifier was replaced.</summary>
    Changed,

    /// <summary>The supplied current password did not verify, or no password has been set yet. Nothing was written.</summary>
    CurrentPasswordIncorrect,

    /// <summary>The proposed new password was refused before anything was written — too short, or blank.</summary>
    NewPasswordRejected,

    /// <summary>No operator credential store is composed in this process.</summary>
    Unavailable,

    /// <summary>The write threw. The existing password is unchanged.</summary>
    Failed,
}

/// <summary>What an operator password rotation did.</summary>
/// <param name="Outcome">Which case this is.</param>
/// <param name="Detail">Why, when the outcome alone does not say it.</param>
public sealed record OperatorPasswordChangeResult(OperatorPasswordChangeOutcome Outcome, string? Detail)
{
    /// <summary>The verifier was replaced.</summary>
    public static readonly OperatorPasswordChangeResult Changed =
        new(OperatorPasswordChangeOutcome.Changed, null);

    /// <summary>No operator credential store is composed in this process.</summary>
    public static readonly OperatorPasswordChangeResult Unavailable =
        new(OperatorPasswordChangeOutcome.Unavailable, null);

    /// <summary>
    /// The current password did not verify. Deliberately the same result whether the password was wrong or
    /// no password has ever been set, so this form cannot be used to probe which of the two is true.
    /// </summary>
    public static readonly OperatorPasswordChangeResult CurrentPasswordIncorrect =
        new(OperatorPasswordChangeOutcome.CurrentPasswordIncorrect, null);
}
