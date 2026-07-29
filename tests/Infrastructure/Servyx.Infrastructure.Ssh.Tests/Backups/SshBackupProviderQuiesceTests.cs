using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// Quiescing before an SSH backup: the flush the definition asked for happens before the host is asked to
/// archive anything, and a flush that does not happen produces no archive at all.
/// </summary>
/// <remarks>
/// <para>
/// The claim being defended here is narrower and sharper than "the provider calls save". An SSH archive is
/// built by the host's own <c>tar</c>, so by the time a "best effort" archive existed it would already be a
/// real <c>.tar.gz</c> on the operator's disk with a manifest beside it — indistinguishable from a good
/// backup until the day someone restores it and finds the world several minutes stale. So every failure
/// route below asserts the same thing: the artifact directory is empty afterwards, and not one command
/// reached the host.
/// </para>
/// <para>
/// The channel's <em>presence</em> is the opt-in. A scenario that sets neither <c>Control</c> nor
/// <c>Quiesce</c> is the shape every other test in this folder already runs, and
/// <see cref="CreateAsync_without_a_declared_quiesce_archives_on_disk_state_and_records_that_it_did"/>
/// pins that it stays that way — including the manifest field that keeps an un-flushed archive tellable
/// apart from a flushed one.
/// </para>
/// </remarks>
public class SshBackupProviderQuiesceTests
{
    private const string ArchiveName = "servyx-20260729T101500Z.tar.gz";
    private const string ArchivePath = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/{ArchiveName}";
    private const string StorePrefix = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/";
    private const string QuiesceCommandId = "save";

    private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CreateAsync_quiesces_before_it_asks_the_host_to_archive_anything()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Control = Quiescing(scenario, succeeds: true);
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var journal = scenario.Host.Journal;
        var quiesceIndex = journal.IndexOf("quiesce:" + QuiesceCommandId);
        var firstExec = journal.FindIndex(e => e.StartsWith("exec:", StringComparison.Ordinal));
        var firstWrite = journal.FindIndex(e => e.StartsWith("write:", StringComparison.Ordinal));
        var archiveExec = journal.IndexOf("exec:tar");

        quiesceIndex.Should().BeGreaterThanOrEqualTo(0, "the declared quiesce must actually have been issued");
        firstExec.Should().BeGreaterThan(quiesceIndex, "no command may reach the host before the flush completes");
        archiveExec.Should().BeGreaterThan(quiesceIndex, "the archive command in particular must follow the flush");
        firstWrite.Should().BeGreaterThan(quiesceIndex);
    }

    [Fact]
    public async Task CreateAsync_records_a_successful_quiesce_in_the_manifest()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Control = Quiescing(scenario, succeeds: true);
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var manifest = BackupManifest.FromUtf8Json(scenario.Host.Read(ArchivePath + SshBackupProvider.ManifestSuffix));

        manifest.Should().NotBeNull();
        manifest!.QuiescedWith.Should().Be(QuiesceCommandId);
    }

    [Fact]
    public async Task CreateAsync_passes_the_declared_arguments_through_to_the_control_channel()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var control = Quiescing(scenario, succeeds: true);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = "backup" };

        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, arguments, QuiesceTimeout);

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        await control.Received(1).InvokeAsync(QuiesceCommandId, arguments, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_without_a_declared_quiesce_archives_on_disk_state_and_records_that_it_did()
    {
        // The pre-quiesce shape, unchanged: no channel configured, so no flush is asked for, so the host's
        // tar archives whatever the server last wrote — and the manifest says so, which is the only thing
        // that keeps this archive tellable apart from a flushed one after the fact.
        var scenario = new SshBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        artifact.Location.Should().Be(ArchivePath);
        scenario.Host.Has(ArchivePath).Should().BeTrue();

        var manifest = BackupManifest.FromUtf8Json(scenario.Host.Read(ArchivePath + SshBackupProvider.ManifestSuffix));

        manifest.Should().NotBeNull();
        manifest!.QuiescedWith.Should().BeNull("an archive taken without a flush must not look like one taken with it");
        scenario.Host.Journal.Should().NotContain(e => e.StartsWith("quiesce:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_with_a_channel_but_no_declared_step_never_touches_the_channel()
    {
        // A channel with no step is not a fallback: nothing is issued on it, and the manifest still records
        // the archive as un-quiesced. The step's presence is what asks for a flush, never the channel's.
        var scenario = new SshBackupScenario().WithGameLayout();
        var control = Substitute.For<IRconSession>();
        scenario.Control = control;

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        await control.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());

        BackupManifest.FromUtf8Json(scenario.Host.Read(ArchivePath + SshBackupProvider.ManifestSuffix))!
            .QuiescedWith.Should().BeNull();
    }

    [Fact]
    public async Task A_quiesce_that_reports_failure_produces_no_archive_and_no_manifest()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Control = Quiescing(scenario, succeeds: false);
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.CommandId.Should().Be(QuiesceCommandId);

        AssertNothingWasLeftBehind(scenario);
    }

    [Fact]
    public async Task A_quiesce_that_throws_produces_no_archive_and_no_manifest()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var control = Substitute.For<IRconSession>();
        control.InvokeAsync(QuiesceCommandId, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("rcon socket closed"));

        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.InnerException.Should().BeOfType<IOException>("the underlying transport failure is not swallowed");

        AssertNothingWasLeftBehind(scenario);
    }

    [Fact]
    public async Task A_quiesce_that_never_completes_times_out_and_produces_no_archive_and_no_manifest()
    {
        // The failure mode that matters most on a busy world: the server accepts the save and simply does not
        // answer. The step's own timeout has to end the backup, because a flush still in flight is exactly
        // the state an archive must not be taken in.
        var scenario = new SshBackupScenario().WithGameLayout();
        var control = Substitute.For<IRconSession>();
        control.InvokeAsync(QuiesceCommandId, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new RconResponse("unreachable", true);
            });

        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, TimeSpan.FromMilliseconds(50));

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.Message.Should().Contain("did not complete within");

        AssertNothingWasLeftBehind(scenario);
    }

    [Fact]
    public async Task A_declared_quiesce_with_no_control_channel_is_refused_rather_than_skipped()
    {
        // Naming a flush and having nowhere to issue it is a misconfiguration, not a licence to archive
        // un-flushed state. It is caught while resolving the context, so nothing runs at all.
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);
        scenario.Control = null;

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.Message.Should().Contain("no control channel to issue it on");

        AssertNothingWasLeftBehind(scenario);
    }

    [Fact]
    public async Task A_failed_quiesce_leaves_previous_archives_untouched()
    {
        // "Nothing left behind" must not be read as "nothing there". A refused backup adds no archive and
        // removes none: yesterday's remains exactly as it was.
        var yesterday = new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);
        var scenario = new SshBackupScenario().WithGameLayout().WithServyxArchives(yesterday);
        scenario.Control = Quiescing(scenario, succeeds: false);
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);

        var existing = $"{StorePrefix}servyx-20260728T030000Z.tar.gz";
        var before = scenario.Host.Read(existing);

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);
        await act.Should().ThrowAsync<BackupQuiesceFailedException>();

        scenario.Host.Read(existing).Should().Equal(before);
        scenario.Host.Has(ArchivePath).Should().BeFalse();
        scenario.Host.Has(ArchivePath + SshBackupProvider.ManifestSuffix).Should().BeFalse();
        scenario.Host.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_only_server_is_refused_before_its_control_channel_is_touched()
    {
        // The write guard already refuses `save` on a read-only server, so quiescing one could only ever end
        // in a failed backup. Refusing first means the operator gets the accurate reason — writes are off —
        // rather than a report that the flush failed.
        var scenario = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout();
        var control = Substitute.For<IRconSession>();
        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        await act.Should().ThrowAsync<WritesDisabledException>();

        await control.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());

        AssertNothingWasLeftBehind(scenario);
    }

    [Fact]
    public async Task An_invalid_include_set_is_refused_before_the_server_is_asked_to_stall()
    {
        // A quiesce stalls a live game server. Doing that for a backup whose include set was going to be
        // rejected on purely local grounds is a cost paid by players for nothing.
        var scenario = new SshBackupScenario().WithGameLayout();
        var control = Substitute.For<IRconSession>();
        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep(QuiesceCommandId, null, QuiesceTimeout);
        scenario.Include = ["worlds_local/*.db"];

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await control.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The claim the whole file exists for: after a refused quiesce the artifact directory holds nothing this
    /// call put there, and the host was never asked to run a command.
    /// </summary>
    private static void AssertNothingWasLeftBehind(SshBackupScenario scenario)
    {
        scenario.Host.Paths
            .Where(p => p.StartsWith(StorePrefix, StringComparison.Ordinal))
            .Should().BeEmpty("a backup that could not be flushed must leave no archive and no manifest");

        scenario.Host.Commands.Should().BeEmpty("the flush precedes every command, so a failed flush issues none");

        scenario.Host.Journal
            .Where(e => e.StartsWith("write:", StringComparison.Ordinal) || e.StartsWith("delete:", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    /// <summary>A control channel that records the flush into the host's journal, so ordering is assertable.</summary>
    private static IRconSession Quiescing(SshBackupScenario scenario, bool succeeds)
    {
        var control = Substitute.For<IRconSession>();
        control.InvokeAsync(QuiesceCommandId, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                scenario.Host.Journal.Add("quiesce:" + QuiesceCommandId);
                return Task.FromResult(succeeds
                    ? new RconResponse("Complete Save", true)
                    : new RconResponse("Unknown command", false));
            });

        return control;
    }
}
