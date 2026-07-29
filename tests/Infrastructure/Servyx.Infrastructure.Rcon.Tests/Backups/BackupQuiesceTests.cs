using System.Text;
using Servyx.Domain.Backups;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests.Backups;

/// <summary>
/// The reason RCON exists in Servyx at all: a backup of a running Palworld server has to flush the world to
/// disk before it captures anything, and must not produce an archive if that flush did not happen.
/// </summary>
/// <remarks>
/// These drive the real <c>DockerBackupProvider</c> against an in-memory filesystem, with the control
/// channel supplied by a real <see cref="WriteGuardedRconSession"/> where the write guard is the subject.
/// No Docker daemon, no game server.
/// </remarks>
public class BackupQuiesceTests
{
    private const string ServerId = "palworld-server";

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
    ]);

    private static (DockerBackupProvider Provider, InMemoryTarget Target, List<string> Journal) Build(
        IRconSession? control,
        QuiesceStep? quiesce)
    {
        var journal = new List<string>();
        var target = new InMemoryTarget("/palworld", journal)
            .With("Pal/Saved/SaveGames/0/Level.sav", "level")
            .With("Pal/Saved/SaveGames/0/LevelMeta.sav", "meta");

        var context = new DockerBackupContext(
            ServerId,
            "docker",
            [new BackupSource("data", target, target.Root, ["Pal/Saved/SaveGames/**"], [])],
            new BackupStore(target, target.Root, "servyx-backups"),
            [],
            new RetentionPolicy(6, 7, 4),
            quiesce,
            control);

        return (new DockerBackupProvider(new StaticContextSource(context), [], TimeProvider.System), target, journal);
    }

    [Fact]
    public async Task The_quiesce_command_is_issued_before_a_single_file_is_read()
    {
        var journal = new List<string>();
        var control = new ScriptedRconSession(journal);

        var target = new InMemoryTarget("/palworld", journal)
            .With("Pal/Saved/SaveGames/0/Level.sav", "level");

        var context = new DockerBackupContext(
            ServerId,
            "docker",
            [new BackupSource("data", target, target.Root, ["Pal/Saved/SaveGames/**"], [])],
            new BackupStore(target, target.Root, "servyx-backups"),
            [],
            new RetentionPolicy(6, 7, 4),
            new QuiesceStep("save", null, TimeSpan.FromSeconds(30)),
            control);

        var provider = new DockerBackupProvider(new StaticContextSource(context), [], TimeProvider.System);

        await provider.CreateAsync(ServerId);

        // The whole point: had the read happened first, the archive would hold pre-flush bytes.
        journal.Should().NotBeEmpty();
        journal[0].Should().Be("control:save");
        journal.Should().Contain(e => e.StartsWith("read:", StringComparison.Ordinal));
        control.Invoked.Should().ContainSingle().Which.Should().Be("save");
    }

    [Fact]
    public async Task The_manifest_records_which_quiesce_command_was_issued()
    {
        var (provider, target, _) = Build(
            new ScriptedRconSession(),
            new QuiesceStep("save", null, TimeSpan.FromSeconds(30)));

        var artifact = await provider.CreateAsync(ServerId);

        var manifest = Encoding.UTF8.GetString(target.Read(RelativeOf(artifact) + ".manifest.json"));
        manifest.Should().Contain("save");
    }

    [Fact]
    public async Task An_archive_taken_without_a_quiesce_says_so_in_its_manifest()
    {
        // The pre-M2 behaviour, still reachable and still honest: no control channel means no flush, and
        // the manifest carries a null quiesce command rather than implying one happened.
        var (provider, target, journal) = Build(control: null, quiesce: null);

        var artifact = await provider.CreateAsync(ServerId);

        journal.Should().NotContain(e => e.StartsWith("control:", StringComparison.Ordinal));
        Encoding.UTF8.GetString(target.Read(RelativeOf(artifact) + ".manifest.json"))
            .Should().Contain("\"QuiescedWith\": null");
    }

    [Fact]
    public async Task A_quiesce_that_reports_failure_produces_no_archive_at_all()
    {
        var (provider, target, _) = Build(
            new ScriptedRconSession(respond: _ => new RconResponse("Save failed: disk full", Success: false)),
            new QuiesceStep("save", null, TimeSpan.FromSeconds(30)));

        var act = async () => await provider.CreateAsync(ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();

        // Not a smaller archive, not an archive flagged as suspect: none. An un-flushed archive is
        // indistinguishable from a good one until the day someone restores it.
        target.Paths.Should().NotContain(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_quiesce_that_throws_produces_no_archive_at_all()
    {
        var (provider, target, _) = Build(
            new ScriptedRconSession(fail: new RconUnreachableException("The RCON endpoint 127.0.0.1:25575 could not be reached.")),
            new QuiesceStep("save", null, TimeSpan.FromSeconds(30)));

        var act = async () => await provider.CreateAsync(ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.CommandId.Should().BeNull("the inner-exception overload carries the cause instead");

        target.Paths.Should().NotContain(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_quiesce_the_write_guard_refuses_produces_no_archive_at_all()
    {
        // A read-only server cannot be flushed, so it cannot produce a flushed backup either. The refusal
        // surfaces as a failed backup rather than as a silently un-flushed archive.
        var guarded = new WriteGuardedRconSession(new ScriptedRconSession(), Palworld(), WriteMode.ReadOnly, ServerId);

        var (provider, target, _) = Build(guarded, new QuiesceStep("save", null, TimeSpan.FromSeconds(30)));

        var act = async () => await provider.CreateAsync(ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();
        target.Paths.Should().NotContain(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_quiesce_that_never_answers_times_out_and_produces_no_archive()
    {
        var (provider, target, _) = Build(
            new HangingRconSession(),
            new QuiesceStep("save", null, TimeSpan.FromMilliseconds(250)));

        var act = async () => await provider.CreateAsync(ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.Message.Should().Contain("did not complete");

        target.Paths.Should().NotContain(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_context_declaring_a_quiesce_with_no_control_channel_is_refused_outright()
    {
        var (provider, target, _) = Build(control: null, quiesce: new QuiesceStep("save", null, TimeSpan.FromSeconds(30)));

        var act = async () => await provider.CreateAsync(ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();
        target.Paths.Should().NotContain(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal));
    }

    private static string RelativeOf(BackupArtifact artifact) =>
        artifact.Location.Replace("/palworld/", string.Empty, StringComparison.Ordinal);
}

/// <summary>A context source that hands back one pre-built context.</summary>
internal sealed class StaticContextSource(DockerBackupContext context) : IDockerBackupContextSource
{
    public Task<DockerBackupContext> GetAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(context);
}
