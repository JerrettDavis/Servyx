using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

/// <summary>
/// An in-memory filesystem behind a substituted <see cref="IExecutionTarget"/>. The substitute is real
/// NSubstitute, so <c>Received</c>/<c>DidNotReceive</c> assertions work exactly as they do elsewhere in
/// this suite, while the backing dictionary makes archive round-trips readable. No Docker daemon is
/// involved anywhere.
/// </summary>
internal sealed class FakeTarget
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _times = new(StringComparer.Ordinal);

    public FakeTarget(string root, List<string>? journal = null)
    {
        Root = root;
        Journal = journal ?? [];
        Target = Substitute.For<IExecutionTarget>();
        Wire();
    }

    public string Root { get; }

    public IExecutionTarget Target { get; }

    public List<string> Journal { get; }

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public FakeTarget With(string path, string content, DateTimeOffset? modifiedAt = null) =>
        With(path, Encoding.UTF8.GetBytes(content), modifiedAt);

    public FakeTarget With(string path, byte[] content, DateTimeOffset? modifiedAt = null)
    {
        var key = Normalize(path);
        _files[key] = content;
        _times[key] = modifiedAt ?? DateTimeOffset.UnixEpoch;
        return this;
    }

    public bool Has(string path) => _files.ContainsKey(Normalize(path));

    public byte[] Read(string path) => _files[Normalize(path)];

    public IEnumerable<string> Paths => _files.Keys;

    public TargetPath Path(string relative) => new SandboxedPathResolver(Root).Resolve(relative);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private void Wire()
    {
        Target.ListDirectoryAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var dir = Normalize(ci.Arg<TargetPath>().Value ?? string.Empty);
                Journal.Add($"list:{dir}");
                return Task.FromResult(ListDirectory(dir));
            });

        Target.StatAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = Normalize(ci.Arg<TargetPath>().Value ?? string.Empty);
                Journal.Add($"stat:{path}");
                if (_files.TryGetValue(path, out var bytes))
                {
                    return Task.FromResult(new FileStat(true, false, bytes.LongLength, _times[path], null));
                }

                return Task.FromResult(DirectoryExists(path)
                    ? new FileStat(true, true, null, null, null)
                    : new FileStat(false, false, null, null, null));
            });

        Target.ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = Normalize(ci.Arg<TargetPath>().Value ?? string.Empty);
                Journal.Add($"exists:{path}");
                return Task.FromResult(_files.ContainsKey(path) || DirectoryExists(path));
            });

        Target.OpenReadAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = Normalize(ci.Arg<TargetPath>().Value ?? string.Empty);
                Journal.Add($"read:{path}");
                if (!_files.TryGetValue(path, out var bytes))
                {
                    throw new FileNotFoundException($"'{path}' not found.", path);
                }

                return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
            });

        Target.WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = Normalize(ci.Arg<TargetPath>().Value ?? string.Empty);
                Journal.Add($"write:{path}");

                using var buffer = new MemoryStream();
                ci.ArgAt<Stream>(1).CopyTo(buffer);
                _files[path] = buffer.ToArray();
                _times[path] = DateTimeOffset.UnixEpoch;

                return Task.FromResult(new FileWriteReceipt(null, "sha", DateTimeOffset.UnixEpoch));
            });

        Target.DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = Normalize(ci.Arg<TargetPath>().Value ?? string.Empty);
                Journal.Add($"delete:{path}");
                if (!_files.Remove(path))
                {
                    throw new FileNotFoundException($"'{path}' not found.", path);
                }

                _times.Remove(path);
                return Task.CompletedTask;
            });
    }

    private bool DirectoryExists(string dir) =>
        dir.Length == 0 || _files.Keys.Any(k => k.StartsWith(dir + "/", StringComparison.Ordinal));

    private IReadOnlyList<FileEntry> ListDirectory(string dir)
    {
        if (!DirectoryExists(dir))
        {
            throw new DirectoryNotFoundException($"'{dir}' not found.");
        }

        var prefix = dir.Length == 0 ? string.Empty : dir + "/";
        var children = new Dictionary<string, FileEntry>(StringComparer.Ordinal);

        foreach (var key in _files.Keys)
        {
            if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                children[rest] = new FileEntry(rest, false, _files[key].LongLength, _times[key]);
                continue;
            }

            var name = rest[..slash];
            children.TryAdd(name, new FileEntry(name, true, null, null));
        }

        return children.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A context source returning a single, pre-built context.</summary>
internal sealed class StaticContextSource(DockerBackupContext context) : IDockerBackupContextSource
{
    public int Calls { get; private set; }

    public Task<DockerBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(context);
    }
}

/// <summary>Builds the Palworld-shaped scenario the definition's <c>backup:</c> block describes.</summary>
internal sealed class BackupScenario
{
    public const string ServerId = "palworld-server";

    public BackupScenario()
    {
        Journal = [];
        Data = new FakeTarget("/palworld", Journal);
        Compose = new FakeTarget("/srv/palworld", Journal);
        Clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 27, 10, 15, 0, TimeSpan.Zero));
    }

    public List<string> Journal { get; }

    public FakeTarget Data { get; }

    public FakeTarget Compose { get; }

    public TestTimeProvider Clock { get; }

    public IRconSession? Control { get; set; }

    public QuiesceStep? Quiesce { get; set; }

    public RetentionPolicy Retention { get; set; } = new(6, 7, 4);

    public string? ForeignRestoreSourceId { get; set; }

    /// <summary>Seeds the file layout the Palworld image actually produces.</summary>
    public BackupScenario WithPalworldLayout()
    {
        Data.With("Pal/Saved/SaveGames/0/Level.sav", "level");
        Data.With("Pal/Saved/SaveGames/0/LevelMeta.sav", "meta");
        Data.With("Pal/Saved/SaveGames/0/Players/abc.sav", "player");
        Data.With("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", "[settings]");
        Data.With("Pal/Saved/Logs/Pal.log", "noisy");
        Compose.With(".env", "SERVER_NAME=test");
        Compose.With("compose.yaml", "services: {}");
        return this;
    }

    /// <summary>Adds archives created by the image's own cron job.</summary>
    public BackupScenario WithForeignArchives(params string[] names)
    {
        foreach (var name in names)
        {
            Data.With($"backups/{name}", ForeignArchiveBytes(), new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero));
        }

        return this;
    }

    /// <summary>Adds Servyx-owned archives (plus their sidecar manifests) at the given timestamps.</summary>
    public BackupScenario WithServyxArchives(params DateTimeOffset[] timestamps)
    {
        foreach (var at in timestamps)
        {
            var name = $"servyx-{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.tar.gz";
            var archive = ForeignArchiveBytes("data/Pal/Saved/SaveGames/0/Level.sav");
            var manifest = new BackupManifest(
                BackupManifest.CurrentSchemaVersion,
                ServerId,
                at,
                name,
                "0",
                archive.LongLength,
                null,
                ["data/Pal/Saved/SaveGames/0/Level.sav"]);

            Data.With($"servyx-backups/{name}", archive, at);
            Data.With($"servyx-backups/{name}.manifest.json", manifest.ToUtf8Json(), at);
        }

        return this;
    }

    public DockerBackupContext Build() => new(
        ServerId,
        "docker",
        [
            new BackupSource(
                "data",
                Data.Target,
                Data.Root,
                ["Pal/Saved/SaveGames/**", "Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"],
                ["Pal/Saved/Logs/**", "backups/**"]),
            new BackupSource(
                "compose",
                Compose.Target,
                Compose.Root,
                [".env", "compose.yaml"],
                []),
        ],
        new BackupStore(Data.Target, Data.Root, "servyx-backups"),
        [
            new ForeignBackupSource(
                PalworldCronBackupAdopter.Id,
                Data.Target,
                Data.Root,
                "backups",
                "*.tar.gz",
                ForeignRestoreSourceId),
        ],
        Retention,
        Quiesce,
        Control);

    public StaticContextSource Source() => new(Build());

    public DockerBackupProvider Provider(IEnumerable<IBackupAdopter>? adopters = null, TimeSpan? planTtl = null)
    {
        var source = Source();
        return new DockerBackupProvider(
            source,
            adopters ?? [new PalworldCronBackupAdopter(source)],
            Clock,
            planTtl);
    }

    public string ForeignBackupId(string name) =>
        BackupArtifactId.Format(ServerId, $"{Data.Root}/backups/{name}");

    public string ServyxBackupId(string fileName) =>
        BackupArtifactId.Format(ServerId, $"{Data.Root}/servyx-backups/{fileName}");

    /// <summary>A minimal, valid <c>.tar.gz</c> standing in for a cron-produced archive.</summary>
    public static byte[] ForeignArchiveBytes(params string[] entryNames)
    {
        var names = entryNames.Length == 0
            ? new[] { "Pal/Saved/SaveGames/0/Level.sav" }
            : entryNames;

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var name in names)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("payload:" + name)),
                });
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Reads back the entry names of an archive this provider wrote.</summary>
    public static IReadOnlyList<string> EntryNamesOf(byte[] archive)
    {
        using var raw = new MemoryStream(archive);
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

    /// <summary>Reads back the content of a single entry of an archive this provider wrote.</summary>
    public static string EntryContentOf(byte[] archive, string entryName)
    {
        using var raw = new MemoryStream(archive);
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: true);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            if (!string.Equals(entry.Name, entryName, StringComparison.Ordinal) || entry.DataStream is null)
            {
                continue;
            }

            using var content = new MemoryStream();
            entry.DataStream.CopyTo(content);
            return Encoding.UTF8.GetString(content.ToArray());
        }

        throw new InvalidOperationException($"Entry '{entryName}' not present.");
    }
}
