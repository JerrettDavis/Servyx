using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// The SSH half of the resume guarantee: whatever happens to the archive, the commands that undo the
/// quiesce still run.
/// </summary>
/// <remarks>
/// A quiesce that turns saving <em>off</em> is only safe if something turns it back on. Over SSH the risk is
/// the same as it is over Docker and the failure modes are wider — the host's <c>tar</c> can exit non-zero,
/// the connection can drop mid-archive — so the resume sits in a <c>finally</c> around both the quiesce and
/// the capture, and is issued on an uncancelled token bounded only by each step's own timeout.
/// </remarks>
public class SshBackupProviderResumeTests
{
    private const string QuiesceCommandId = "save-off";
    private const string ResumeCommandId = "save-on";
    private const string ArchiveName = "servyx-20260729T101500Z.tar.gz";
    private const string ArchivePath = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/{ArchiveName}";

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly QuiesceStep Quiesce = new(QuiesceCommandId, null, StepTimeout);
    private static readonly QuiesceStep Resume = new(ResumeCommandId, null, StepTimeout);

    [Fact]
    public async Task CreateAsync_AfterSuccessfulCapture_RunsTheResumeStep()
    {
        var control = new RecordingSshControl();
        var (provider, scenario) = Build(control, Quiesce, [Resume]);

        await provider.CreateAsync(SshBackupScenario.ServerId);

        control.Invoked.Should().Equal(QuiesceCommandId, ResumeCommandId);
        scenario.Host.Has(ArchivePath).Should().BeTrue();
    }

    /// <summary>
    /// The core guarantee: the host's <c>tar</c> exits non-zero, the archive is cleaned up, and the server is
    /// still handed back able to write to disk.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenTheArchiveCommandFails_StillRunsTheResumeStep()
    {
        var control = new RecordingSshControl();
        var (provider, scenario) = Build(control, Quiesce, [Resume]);

        scenario.Host.ExecOverride = spec => spec.Executable == "tar" && spec.Arguments.Contains("--create")
            ? new CommandResult(2, string.Empty, "tar: Cannot write: No space left on device", TimeSpan.Zero)
            : null;

        var act = () => provider.CreateAsync(SshBackupScenario.ServerId);

        await act.Should().ThrowAsync<SshBackupCommandFailedException>();
        control.Invoked.Should().Equal(QuiesceCommandId, ResumeCommandId);
    }

    /// <summary>
    /// A quiesce list can fail having already disabled saving, which is why the try starts before the
    /// quiesce rather than after it.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenTheQuiescePhaseFails_StillRunsTheResumeStep()
    {
        var control = new RecordingSshControl
        {
            Reply = id => id == QuiesceCommandId ? new RconResponse("refused", false) : new RconResponse("ok", true),
        };

        var (provider, scenario) = Build(control, Quiesce, [Resume]);

        var act = () => provider.CreateAsync(SshBackupScenario.ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();
        control.Invoked.Should().Equal(QuiesceCommandId, ResumeCommandId);
        scenario.Host.Has(ArchivePath).Should().BeFalse("a failed quiesce still writes no archive");
    }

    [Fact]
    public async Task CreateAsync_WhenTheCallerCancels_StillRunsTheResumeStepOnAnUncancelledToken()
    {
        var control = new RecordingSshControl();
        var (provider, _) = Build(control, Quiesce, [Resume]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => provider.CreateAsync(SshBackupScenario.ServerId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        control.Invoked.Should().Contain(ResumeCommandId);
        control.CancelledTokenSeenOn.Should().NotContain(ResumeCommandId);
    }

    [Fact]
    public async Task CreateAsync_WhenAResumeStepFailsAfterASuccessfulCapture_ThrowsAndKeepsTheArchive()
    {
        var control = new RecordingSshControl { Reply = _ => new RconResponse("refused", false) };
        var (provider, scenario) = Build(control, quiesce: null, resume: [Resume]);

        var act = () => provider.CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupResumeFailedException>())
            .Which.CommandId.Should().Be(ResumeCommandId);

        scenario.Host.Has(ArchivePath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WhenResumeIsDeclaredWithNoControlChannel_RefusesBeforeIssuingAnything()
    {
        var (provider, scenario) = Build(control: null, quiesce: null, resume: [Resume]);

        var act = () => provider.CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupResumeFailedException>())
            .Which.CommandId.Should().Be(ResumeCommandId);

        scenario.Host.Journal.Should().NotContain(e => e.StartsWith("exec:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_WithNoResumeDeclared_IssuesNothingExtra()
    {
        var control = new RecordingSshControl();
        var (provider, scenario) = Build(control, Quiesce, []);

        await provider.CreateAsync(SshBackupScenario.ServerId);

        control.Invoked.Should().Equal(QuiesceCommandId);
        scenario.Host.Has(ArchivePath).Should().BeTrue();
    }

    private static (SshBackupProvider Provider, SshBackupScenario Scenario) Build(
        RecordingSshControl? control,
        QuiesceStep? quiesce,
        IReadOnlyList<QuiesceStep> resume)
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Control = control;
        scenario.Quiesce = quiesce;

        var source = new StaticSshContextSource(scenario.Build() with { Resume = resume });
        return (new SshBackupProvider(source, null, scenario.Clock), scenario);
    }
}

/// <summary>
/// An <see cref="IRconSession"/> that records the command ids it was asked for, in order, and which of them
/// arrived on an already-cancelled token.
/// </summary>
internal sealed class RecordingSshControl : IRconSession
{
    public List<string> Invoked { get; } = [];

    public List<string> CancelledTokenSeenOn { get; } = [];

    /// <summary>How to answer each command id. Defaults to a successful reply.</summary>
    public Func<string, RconResponse> Reply { get; set; } = _ => new RconResponse("ok", true);

    public Task<RconResponse> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? args, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            CancelledTokenSeenOn.Add(commandId);
        }

        Invoked.Add(commandId);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Reply(commandId));
    }

    public Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
}
