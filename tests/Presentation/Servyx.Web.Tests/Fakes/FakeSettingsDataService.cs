using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// A controllable, call-recording <see cref="ISettingsDataService"/> for the <c>/settings</c> bUnit tests,
/// mirroring <see cref="FakeHostRegistrationService"/>'s shape: <see cref="Sections"/> seeds what
/// <see cref="GetSettingsAsync"/> returns, every mutating call is recorded, and the result each returns is a
/// settable property rather than something the fake decides.
/// </summary>
public sealed class FakeSettingsDataService : ISettingsDataService
{
    /// <summary>What <see cref="GetSettingsAsync"/> reports, in order. Mutable so a test can change it between reloads.</summary>
    public List<SettingsSection> Sections { get; } = [];

    /// <summary>How many times <see cref="GetSettingsAsync"/> was reached — a reload after a mutation is an assertion in its own right.</summary>
    public int ReadCalls { get; private set; }

    /// <summary>How many times <see cref="RunRetentionSweepAsync"/> was reached.</summary>
    public int SweepCalls { get; private set; }

    /// <summary>Every <c>(current, new)</c> pair <see cref="ChangeOperatorPasswordAsync"/> was called with, in order.</summary>
    public List<(string Current, string New)> PasswordChangeCalls { get; } = [];

    /// <summary>What <see cref="RunRetentionSweepAsync"/> returns.</summary>
    public RetentionSweepResult SweepResult { get; set; } = new(RetentionSweepOutcome.Swept, 1, 2, 3, null);

    /// <summary>What <see cref="ChangeOperatorPasswordAsync"/> returns.</summary>
    public OperatorPasswordChangeResult PasswordChangeResult { get; set; } = OperatorPasswordChangeResult.Changed;

    /// <summary>Seeds <see cref="Sections"/> fluently.</summary>
    public FakeSettingsDataService With(params SettingsSection[] sections)
    {
        Sections.AddRange(sections);
        return this;
    }

    /// <inheritdoc />
    public Task<SettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        ReadCalls++;
        return Task.FromResult(new SettingsView([.. Sections]));
    }

    /// <inheritdoc />
    public Task<RetentionSweepResult> RunRetentionSweepAsync(CancellationToken ct = default)
    {
        SweepCalls++;
        return Task.FromResult(SweepResult);
    }

    /// <inheritdoc />
    public Task<OperatorPasswordChangeResult> ChangeOperatorPasswordAsync(
        string currentPassword, string newPassword, CancellationToken ct = default)
    {
        PasswordChangeCalls.Add((currentPassword, newPassword));
        return Task.FromResult(PasswordChangeResult);
    }
}
