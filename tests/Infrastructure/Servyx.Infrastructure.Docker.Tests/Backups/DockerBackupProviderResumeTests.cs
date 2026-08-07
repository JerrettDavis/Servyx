using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

/// <summary>
/// Covers <c>DockerBackupContext.Resume</c> — the steps that undo a quiesce once capture is over.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What these tests are actually protecting.</strong> The canonical safe-backup sequence for a live
/// game server is "stop writing to disk, flush, copy the files, start writing again". Without a guaranteed
/// last step, a definition that quiesced by turning saving <em>off</em> and then hit any failure at all
/// would leave a running server silently discarding every player action until someone restarted it. The
/// archive is not the fragile thing here; the server is. So the interesting cases below are all the ones
/// where something went wrong.
/// </para>
/// <para>
/// Each failure mode gets its own test rather than being folded into one parameterised case, because they
/// fail through genuinely different mechanisms — a thrown quiesce, a thrown capture, a cancelled token —
/// and a single test that passed for two of the three would be worse than no test.
/// </para>
/// </remarks>
public class DockerBackupProviderResumeTests
{
    private const string ArchiveName = "servyx-20260727T101500Z.tar.gz";

    private static readonly QuiesceStep Quiesce = new("save-off", null, TimeSpan.FromSeconds(30));
    private static readonly QuiesceStep ResumeStep = new("save-on", null, TimeSpan.FromSeconds(30));

    [Fact]
    public async Task CreateAsync_AfterSuccessfulCapture_RunsEveryResumeStepInDeclaredOrder()
    {
        var control = new RecordingControl();
        var (provider, scenario) = Build(
            control,
            quiesce: Quiesce,
            resume: [ResumeStep, new QuiesceStep("announce", null, TimeSpan.FromSeconds(5))]);

        await provider.CreateAsync(BackupScenario.ServerId);

        control.Invoked.Should().Equal("save-off", "save-on", "announce");
        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeTrue();
    }

    /// <summary>
    /// The core guarantee. Capture blows up mid-write; the resume still runs, and the original failure — not
    /// a downstream symptom of it — is what the caller sees.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenCaptureThrows_StillRunsResumeStepsAndPropagatesTheCaptureFailure()
    {
        var control = new RecordingControl();
        var (provider, scenario) = Build(control, quiesce: Quiesce, resume: [ResumeStep]);

        scenario.Data.Target
            .WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("no space left on device"));

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<IOException>().WithMessage("no space left on device");
        control.Invoked.Should().Equal("save-off", "save-on");
        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeFalse();
    }

    /// <summary>
    /// A quiesce list can fail partway through, having already disabled saving with an earlier step. The
    /// resume must still run — which is why the try block starts <em>before</em> the quiesce, not after it.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenTheQuiescePhaseFails_StillRunsResumeSteps()
    {
        var control = new RecordingControl
        {
            Reply = id => id == "save-off" ? new RconResponse("refused", false) : new RconResponse("ok", true),
        };

        var (provider, _) = Build(control, quiesce: Quiesce, resume: [ResumeStep]);

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();
        control.Invoked.Should().Equal("save-off", "save-on");
    }

    /// <summary>
    /// An operator cancelling a backup is asking Servyx to stop copying files. They are never asking it to
    /// leave the server unable to write to disk — so the resume is issued on an uncancelled token, bounded
    /// only by its own timeout.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenTheCallerCancels_StillRunsResumeStepsOnAnUncancelledToken()
    {
        var control = new RecordingControl();
        var (provider, _) = Build(control, quiesce: Quiesce, resume: [ResumeStep]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => provider.CreateAsync(BackupScenario.ServerId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        control.Invoked.Should().Contain("save-on");
        control.CancelledTokenSeenOn.Should().NotContain("save-on");
    }

    /// <summary>
    /// A resume list is a sequence of undos, and the one that actually re-enables saving may not be first.
    /// One failing step must not skip the rest.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenAnEarlyResumeStepFails_StillRunsTheLaterOnes()
    {
        var control = new RecordingControl
        {
            Reply = id => id == "announce" ? new RconResponse("nope", false) : new RconResponse("ok", true),
        };

        var (provider, _) = Build(
            control,
            quiesce: null,
            resume: [new QuiesceStep("announce", null, TimeSpan.FromSeconds(5)), ResumeStep]);

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<BackupResumeFailedException>();
        control.Invoked.Should().Equal("announce", "save-on");
    }

    /// <summary>
    /// The archive is fine and still on disk; the running server may not be. That is worth throwing over,
    /// which is why a resume failure is raised even on the success path.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenAResumeStepFailsAfterASuccessfulCapture_ThrowsAndKeepsTheArchive()
    {
        var control = new RecordingControl { Reply = _ => new RconResponse("refused", false) };
        var (provider, scenario) = Build(control, quiesce: null, resume: [ResumeStep]);

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupResumeFailedException>())
            .Which.CommandId.Should().Be("save-on");

        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeTrue();
    }

    /// <summary>
    /// When both halves fail, the capture failure is the one that explains what happened; replacing it with
    /// the resume failure during unwinding would hide the cause. The resume failure is not lost — it goes to
    /// the logger, asserted separately below.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenBothCaptureAndResumeFail_DoesNotMaskTheCaptureFailure()
    {
        var control = new RecordingControl { Reply = _ => new RconResponse("refused", false) };
        var (provider, scenario) = Build(control, quiesce: null, resume: [ResumeStep]);

        scenario.Data.Target
            .WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("no space left on device"));

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<IOException>().WithMessage("no space left on device");
        control.Invoked.Should().Equal("save-on");
    }

    /// <summary>A resume failure is never silent, even on the path where it is deliberately not thrown.</summary>
    [Fact]
    public async Task CreateAsync_WhenAResumeStepFailsWhileACaptureFailureUnwinds_LogsItAsAnError()
    {
        var control = new RecordingControl { Reply = _ => new RconResponse("refused", false) };
        var logger = new CapturingLogger();
        var (provider, scenario) = Build(control, quiesce: null, resume: [ResumeStep], logger: logger);

        scenario.Data.Target
            .WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("no space left on device"));

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<IOException>();

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
        logger.Entries[0].Exception.Should().BeOfType<BackupResumeFailedException>();
        logger.Entries[0].Message.Should().Contain(BackupScenario.ServerId);
    }

    /// <summary>
    /// Refused up front, while the server is still writing normally — not discovered in the finally block
    /// after the quiesce has already stopped it.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenResumeIsDeclaredWithNoControlChannel_RefusesBeforeIssuingAnything()
    {
        var (provider, _) = Build(control: null, quiesce: null, resume: [ResumeStep]);

        var act = () => provider.CreateAsync(BackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupResumeFailedException>())
            .Which.CommandId.Should().Be("save-on");
    }

    /// <summary>A context declaring no resume behaves exactly as it did before the phase existed.</summary>
    [Fact]
    public async Task CreateAsync_WithNoResumeDeclared_IssuesNothingExtra()
    {
        var control = new RecordingControl();
        var (provider, scenario) = Build(control, quiesce: Quiesce, resume: []);

        await provider.CreateAsync(BackupScenario.ServerId);

        control.Invoked.Should().Equal("save-off");
        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeTrue();
    }

    private static (DockerBackupProvider Provider, BackupScenario Scenario) Build(
        RecordingControl? control,
        QuiesceStep? quiesce,
        IReadOnlyList<QuiesceStep> resume,
        CapturingLogger? logger = null)
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        scenario.Control = control;
        scenario.Quiesce = quiesce;

        var source = new StaticContextSource(scenario.Build() with { Resume = resume });
        var provider = new DockerBackupProvider(source, [], scenario.Clock, null, logger);

        return (provider, scenario);
    }
}

/// <summary>
/// An <see cref="IRconSession"/> that records the command ids it was asked for, in order, and — crucially
/// for the cancellation test — which of them arrived on an already-cancelled token.
/// </summary>
internal sealed class RecordingControl : IRconSession
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

/// <summary>Captures what the provider logged, so "never swallowed silently" can be asserted rather than assumed.</summary>
internal sealed class CapturingLogger : ILogger<DockerBackupProvider>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
