using System.Text;
using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

/// <summary>
/// The payoff for the write guard, and the reason M4 exists at all: <see cref="DockerBackupProvider"/> is
/// driven here through <see cref="WriteGuardedExecutionTarget"/>s rather than raw ones, which is how a real
/// composition root will hand it targets. Every scenario runs twice in spirit — once against a server whose
/// write mode is <see cref="WriteMode.Enabled"/>, where creating, restoring and pruning genuinely happen,
/// and once against a read-only one, where each of those is refused with nothing written.
/// </summary>
public class DockerBackupProviderWriteGuardTests
{
    private const string ServerId = "palworld-server";
    private const string ArchiveName = "servyx-20260727T101500Z.tar.gz";

    /// <summary>
    /// The Palworld-shaped backup context, with every execution target wrapped in the write guard exactly as
    /// a transport hands them out.
    /// </summary>
    private sealed class GuardedScenario
    {
        public GuardedScenario(WriteMode mode)
        {
            Journal = [];
            Data = new FakeTarget("/palworld", Journal);
            Compose = new FakeTarget("/srv/palworld", Journal);
            Clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 27, 10, 15, 0, TimeSpan.Zero));
            GuardedData = new WriteGuardedExecutionTarget(Data.Target, mode, ServerId);
            GuardedCompose = new WriteGuardedExecutionTarget(Compose.Target, mode, ServerId);
        }

        public List<string> Journal { get; }

        public FakeTarget Data { get; }

        public FakeTarget Compose { get; }

        public TestTimeProvider Clock { get; }

        public IExecutionTarget GuardedData { get; }

        public IExecutionTarget GuardedCompose { get; }

        public string? ForeignRestoreSourceId { get; init; }

        public GuardedScenario WithPalworldLayout()
        {
            Data.With("Pal/Saved/SaveGames/0/Level.sav", "level");
            Data.With("Pal/Saved/SaveGames/0/LevelMeta.sav", "meta");
            Data.With("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", "[settings]");
            Data.With("Pal/Saved/Logs/Pal.log", "noisy");
            Compose.With(".env", "SERVER_NAME=test");
            Compose.With("compose.yaml", "services: {}");
            return this;
        }

        public GuardedScenario WithForeignArchives(params string[] names)
        {
            foreach (var name in names)
            {
                Data.With(
                    $"backups/{name}",
                    BackupScenario.ForeignArchiveBytes(),
                    new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero));
            }

            return this;
        }

        public DockerBackupContext Build() => new(
            ServerId,
            "docker",
            [
                new BackupSource(
                    "data",
                    GuardedData,
                    Data.Root,
                    ["Pal/Saved/SaveGames/**", "Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"],
                    ["Pal/Saved/Logs/**", "backups/**"]),
                new BackupSource("compose", GuardedCompose, Compose.Root, [".env", "compose.yaml"], []),
            ],
            new BackupStore(GuardedData, Data.Root, "servyx-backups"),
            [
                new ForeignBackupSource(
                    PalworldCronBackupAdopter.Id,
                    GuardedData,
                    Data.Root,
                    "backups",
                    "*.tar.gz",
                    ForeignRestoreSourceId),
            ],
            new RetentionPolicy(6, 7, 4));

        public DockerBackupProvider Provider()
        {
            var source = new StaticContextSource(Build());
            return new DockerBackupProvider(source, [new PalworldCronBackupAdopter(source)], Clock);
        }
    }

    private static readonly RetentionPolicy KeepNothing = new(0, 0, 0);

    // ── Enabled: the capability that was previously unreachable ──────────────────────────────────────

    [Fact]
    public async Task CreateAsync_writes_a_real_archive_and_manifest_through_the_guard()
    {
        var scenario = new GuardedScenario(WriteMode.Enabled).WithPalworldLayout();

        var artifact = await scenario.Provider().CreateAsync(ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.Location.Should().Be($"/palworld/servyx-backups/{ArchiveName}");

        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeTrue();
        scenario.Data.Has($"servyx-backups/{ArchiveName}.manifest.json").Should().BeTrue();

        // And the archive really carries the saves and the compose-side files, from two different filesystems.
        BackupScenario.EntryNamesOf(scenario.Data.Read($"servyx-backups/{ArchiveName}")).Should().Contain([
            "data/Pal/Saved/SaveGames/0/Level.sav",
            "compose/.env",
        ]);
    }

    [Fact]
    public async Task PruneAsync_removes_servyx_archives_through_the_guard_and_still_spares_foreign_ones()
    {
        var scenario = new GuardedScenario(WriteMode.Enabled).WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        var provider = scenario.Provider();
        await provider.CreateAsync(ServerId);

        var result = await provider.PruneAsync(ServerId, KeepNothing, dryRun: false);

        result.Removed.Should().HaveCount(1);
        result.SkippedForeign.Should().Be(1);
        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeFalse();
        scenario.Data.Has($"servyx-backups/{ArchiveName}.manifest.json").Should().BeFalse();
        scenario.Data.Has("backups/cron.tar.gz").Should().BeTrue("foreign archives survive every prune path");
    }

    [Fact]
    public async Task The_full_create_plan_restore_round_trip_works_through_the_guard()
    {
        var scenario = new GuardedScenario(WriteMode.Enabled).WithPalworldLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        scenario.Data.With("Pal/Saved/SaveGames/0/Level.sav", "corrupted");
        scenario.Compose.With(".env", "SERVER_NAME=wrong");

        await provider.RestoreAsync(plan.Id);

        Encoding.UTF8.GetString(scenario.Data.Read("Pal/Saved/SaveGames/0/Level.sav")).Should().Be("level");
        Encoding.UTF8.GetString(scenario.Compose.Read(".env")).Should().Be("SERVER_NAME=test");
    }

    // ── ReadOnly / PreviewOnly: the same code, refused with nothing written ──────────────────────────

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task CreateAsync_is_refused_and_writes_nothing(WriteMode mode)
    {
        var scenario = new GuardedScenario(mode).WithPalworldLayout();

        var act = async () => await scenario.Provider().CreateAsync(ServerId);

        await act.Should().ThrowAsync<WritesDisabledException>();
        scenario.Data.Paths.Should().NotContain(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal));
        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAsync_is_refused_and_deletes_nothing()
    {
        // Seeded by an enabled run, then pruned by a read-only one: the archives exist, retention says remove
        // them all, and the guard is the only thing standing in the way.
        var enabled = new GuardedScenario(WriteMode.Enabled).WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        await enabled.Provider().CreateAsync(ServerId);

        var readOnly = new GuardedScenario(WriteMode.ReadOnly).WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        foreach (var path in enabled.Data.Paths.Where(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal)).ToList())
        {
            readOnly.Data.With(path, enabled.Data.Read(path));
        }

        var act = async () => await readOnly.Provider().PruneAsync(ServerId, KeepNothing, dryRun: false);

        await act.Should().ThrowAsync<WritesDisabledException>();
        readOnly.Data.Has($"servyx-backups/{ArchiveName}").Should().BeTrue();
        await readOnly.Data.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Listing_inspecting_and_planning_a_restore_all_still_work_on_a_read_only_server()
    {
        // This is the M1 shape the backups page already had, and it must survive the guard untouched: reads
        // pass through, and only the apply step is refused.
        var enabled = new GuardedScenario(WriteMode.Enabled).WithPalworldLayout();
        await enabled.Provider().CreateAsync(ServerId);

        var readOnly = new GuardedScenario(WriteMode.ReadOnly).WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        foreach (var path in enabled.Data.Paths.Where(p => p.StartsWith("servyx-backups/", StringComparison.Ordinal)).ToList())
        {
            readOnly.Data.With(path, enabled.Data.Read(path));
        }

        var provider = readOnly.Provider();

        var artifacts = await provider.ListAsync(ServerId);
        artifacts.Should().Contain(a => a.Ownership == BackupOwnership.Servyx);
        artifacts.Should().Contain(a => a.Ownership == BackupOwnership.Foreign);

        var servyxOwned = artifacts.First(a => a.Ownership == BackupOwnership.Servyx);
        (await provider.InspectAsync(servyxOwned.Id)).Should().NotBeEmpty();

        var plan = await provider.PlanRestoreAsync(servyxOwned.Id);
        plan.AffectedPaths.Should().NotBeEmpty();

        // Planning is a read. Applying the plan is where the refusal lands.
        readOnly.Data.With("Pal/Saved/SaveGames/0/Level.sav", "corrupted");
        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<WritesDisabledException>();
        Encoding.UTF8.GetString(readOnly.Data.Read("Pal/Saved/SaveGames/0/Level.sav")).Should().Be("corrupted");
    }
}
