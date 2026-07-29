using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Backups;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>A clock the tests move by hand.</summary>
internal sealed class FrozenClock(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// An <see cref="ICompositeExecutionTarget"/> that routes exec one way and files the other.
/// </summary>
/// <remarks>
/// <c>Servyx.Infrastructure.Ssh.CompositeExecutionTarget</c> is the production shape of this, but
/// infrastructure projects may not reference one another, so the local tests need their own. It exists only
/// to prove <c>LocalProcessBackupProvider</c> looks <em>through</em> a composite for a write guard rather
/// than shrugging at an unfamiliar target type.
/// </remarks>
internal sealed class CompositeTargetDouble(IExecutionTarget? execTarget, IExecutionTarget? fileTarget) : ICompositeExecutionTarget
{
    public IExecutionTarget? ExecTarget => execTarget;

    public IExecutionTarget? FileTarget => fileTarget;

    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        Exec().ExecuteAsync(spec, ct);

    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        Exec().ExecuteStreamingAsync(spec, ct);

    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) => File().ExistsAsync(path, ct);

    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) => File().StatAsync(path, ct);

    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        File().ListDirectoryAsync(path, ct);

    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) => File().OpenReadAsync(path, ct);

    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        File().WriteFileAsync(path, content, options, ct);

    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) => File().DeleteAsync(path, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IExecutionTarget Exec() => execTarget ?? throw new InvalidOperationException("No exec target.");

    private IExecutionTarget File() => fileTarget ?? throw new InvalidOperationException("No file target.");
}

/// <summary>A context source returning a single, pre-built context.</summary>
internal sealed class StaticLocalContextSource(LocalBackupContext context) : ILocalBackupContextSource
{
    internal int Calls { get; private set; }

    public Task<LocalBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(context);
    }
}

/// <summary>
/// A stand-in for the adopter a host with a known layout would register.
/// </summary>
/// <remarks>
/// This project ships no adopter (see <see cref="ForeignLocalBackupDirectory"/>), so foreign artifacts can
/// only reach <see cref="LocalProcessBackupProvider"/> through one a composition root supplied. This is that
/// shape: pure read-only discovery, reporting <see cref="BackupOwnership.Foreign"/> and nothing else.
/// </remarks>
internal sealed class StubForeignAdopter(string deploymentKind, params string[] locations) : IBackupAdopter
{
    /// <summary>Ownership every discovered artifact is reported with. Overridable to prove the provider rejects a lie.</summary>
    internal BackupOwnership Ownership { get; set; } = BackupOwnership.Foreign;

    public string AdapterId => "stub-local-cron";

    public bool Supports(string kind) => string.Equals(kind, deploymentKind, StringComparison.Ordinal);

    public Task<IReadOnlyList<BackupArtifact>> DiscoverAsync(string serverId, CancellationToken ct = default)
    {
        IReadOnlyList<BackupArtifact> discovered = locations
            .Select(location => new BackupArtifact(
                BackupArtifactId.Format(serverId, location),
                Ownership,
                new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero),
                64,
                location))
            .ToList();

        return Task.FromResult(discovered);
    }
}

/// <summary>
/// The scenario every test in this folder builds on: a real directory on this machine holding a game
/// server's data, a Servyx artifact directory beside it, and a scheduled task's own archive directory beside
/// that.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here is substituted.</strong> The SSH backup tests need an elaborate host double, because
/// that provider delegates archiving to a machine the test does not have. This one archives in-process
/// against a real <see cref="LocalExecutionTarget"/> rooted at a real temp directory, so every claim in this
/// folder — "the archive excludes the store", "a restore puts the bytes back", "the dry run touched nothing"
/// — is a statement about files that actually existed.
/// </para>
/// <para>
/// <strong>Cross-platform by construction.</strong> Every path is composed with
/// <see cref="System.IO.Path.Combine(string[])"/> from <see cref="System.IO.Path.GetTempPath"/>; there is not
/// one OS-specific path literal in this folder, and no test asserts on a separator character. The one place
/// separators appear is inside archive entry names, which are forward-slash by the tar format's own
/// definition on every platform.
/// </para>
/// </remarks>
internal sealed class LocalBackupScenario : IDisposable
{
    internal const string ServerId = "valheim-local";
    internal const string DeploymentKind = "local-process";
    internal const string StoreDirectory = "servyx-backups";
    internal const string ForeignDirectoryName = "cron-backups";

    private readonly TempDirectory _temp;
    private readonly LocalExecutionTarget _inner;

    internal LocalBackupScenario(WriteMode? writeMode = Domain.Transport.WriteMode.Enabled)
    {
        _temp = new TempDirectory("servyx-backup");
        _inner = new LocalExecutionTarget(_temp.Root);
        WriteMode = writeMode;
        Clock = new FrozenClock(new DateTimeOffset(2026, 7, 29, 10, 15, 0, TimeSpan.Zero));
        Foreign = [new ForeignLocalBackupDirectory("stub-local-cron", At(ForeignDirectoryName), "*.tar.gz")];
    }

    /// <summary>The canonicalised data root the context is built over.</summary>
    internal string Root => _inner.RootPath;

    internal FrozenClock Clock { get; }

    /// <summary>When set, the context's target is wrapped in a <see cref="WriteGuardedExecutionTarget"/>.</summary>
    internal WriteMode? WriteMode { get; }

    internal IReadOnlyList<string> Include { get; set; } = ["."];

    internal IReadOnlyList<string> Exclude { get; set; } = ["logs", "logs/**"];

    internal RetentionPolicy Retention { get; set; } = new(6, 7, 4);

    internal IReadOnlyList<ForeignLocalBackupDirectory> Foreign { get; set; }

    internal string StoreDirectoryName { get; set; } = StoreDirectory;

    /// <summary>The bare, unguarded target — used only to prove the guard is what refuses.</summary>
    internal IExecutionTarget BareTarget => _inner;

    /// <summary>The target handed to the provider — guarded when <see cref="WriteMode"/> is set, bare otherwise.</summary>
    internal IExecutionTarget ContextTarget => WriteMode is { } mode
        ? new WriteGuardedExecutionTarget(_inner, mode, ServerId)
        : _inner;

    /// <summary>Composes an absolute path under the data root.</summary>
    internal string At(params string[] segments) => System.IO.Path.Combine([Root, .. segments]);

    /// <summary>
    /// A stable, comparable description of everything under the data root. Two snapshots comparing equal
    /// means nothing was created, removed, rewritten, or touched in between.
    /// </summary>
    internal IReadOnlyList<string> Snapshot() => _temp.Snapshot();

    /// <summary>Writes a file (and any missing parents) at a root-relative path built from its segments.</summary>
    internal string Write(byte[] content, params string[] segments)
    {
        var full = At(segments);
        var parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Writes a UTF-8 text file at a root-relative path built from its segments.</summary>
    internal string Write(string content, params string[] segments) => Write(Encoding.UTF8.GetBytes(content), segments);

    /// <summary>Seeds the file layout a dedicated server actually produces, including one genuinely binary file.</summary>
    internal LocalBackupScenario WithGameLayout()
    {
        Write("world", "worlds_local", "Dedicated.db");
        Write("meta", "worlds_local", "Dedicated.fwl");
        Write("name=test", "config", "server.cfg");
        Write("noisy", "logs", "server.log");
        Write(BinaryPayload, "saves", "world.bin");
        return this;
    }

    /// <summary>Bytes chosen to survive nothing but an honest byte-for-byte round trip.</summary>
    internal static byte[] BinaryPayload { get; } =
        [.. Enumerable.Range(0, 512).Select(i => (byte)((i * 37) % 256))];

    /// <summary>Adds archives some scheduled task created, in the declared foreign directory.</summary>
    internal LocalBackupScenario WithForeignArchives(params string[] names)
    {
        foreach (var name in names)
        {
            Write("not-a-real-archive-and-servyx-never-opens-it", ForeignDirectoryName, name);
        }

        return this;
    }

    /// <summary>Adds Servyx-owned archives (plus their sidecar manifests) at the given timestamps.</summary>
    internal LocalBackupScenario WithServyxArchives(params DateTimeOffset[] timestamps)
    {
        foreach (var at in timestamps)
        {
            var name = ArchiveNameFor(at);
            var archive = Encoding.UTF8.GetBytes("seeded-archive-" + name);
            var manifest = new BackupManifest(
                BackupManifest.CurrentSchemaVersion,
                ServerId,
                at,
                name,
                "0",
                archive.LongLength,
                Root,
                ["worlds_local/Dedicated.db"]);

            Write(archive, StoreDirectoryName, name);
            Write(manifest.ToUtf8Json(), StoreDirectoryName, name + LocalProcessBackupProvider.ManifestSuffix);
        }

        return this;
    }

    internal static string ArchiveNameFor(DateTimeOffset at) =>
        $"{LocalProcessBackupProvider.ArchivePrefix}{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}{LocalProcessBackupProvider.ArchiveSuffix}";

    internal LocalBackupContext Build() => new(
        ServerId,
        DeploymentKind,
        ContextTarget,
        Root,
        Include,
        Exclude,
        StoreDirectoryName,
        Foreign,
        Retention);

    internal StaticLocalContextSource Source() => new(Build());

    internal LocalProcessBackupProvider Provider(IEnumerable<IBackupAdopter>? adopters = null, TimeSpan? planTtl = null) =>
        new(Source(), adopters, Clock, planTtl);

    /// <summary>A provider whose only adopter reports the seeded foreign archives as foreign.</summary>
    internal LocalProcessBackupProvider ProviderWithForeign(params string[] names) =>
        Provider([new StubForeignAdopter(DeploymentKind, names.Select(n => At(ForeignDirectoryName, n)).ToArray())]);

    internal string ServyxBackupId(string fileName) =>
        BackupArtifactId.Format(ServerId, At(StoreDirectoryName, fileName));

    internal string ForeignBackupId(string fileName) =>
        BackupArtifactId.Format(ServerId, At(ForeignDirectoryName, fileName));

    /// <summary>Reads back the regular-file entry names of an archive the provider produced.</summary>
    internal static IReadOnlyList<string> EntryNamesOf(byte[] archive)
    {
        using var raw = new MemoryStream(archive, writable: false);
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: true);

        var names = new List<string>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            names.Add(entry.Name);
        }

        return names;
    }

    public void Dispose()
    {
        _inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _temp.Dispose();
    }
}
