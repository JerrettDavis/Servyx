using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// A substituted SSH host with an in-memory filesystem <em>and a working <c>tar</c></em>.
/// </summary>
/// <remarks>
/// <para>
/// This is the same seam <c>SshHostDouble</c> uses for provisioning — an NSubstitute
/// <see cref="IExecutionTarget"/>, so <c>Received</c>/<c>DidNotReceive</c> assertions work as they do
/// everywhere else, and no SSH server is involved. The difference is that <see cref="SshBackupProvider"/>
/// delegates the archiving itself to the host, so a double that answered every command "exit 0" would leave
/// the interesting claims — "the archive excludes the artifact directory", "a restore puts the bytes back" —
/// asserted only against an argv array.
/// </para>
/// <para>
/// So this double really archives: <c>tar --create</c> reads the in-memory filesystem, honours
/// <c>--directory</c>, the member list and every <c>--exclude</c>, and writes a genuine <c>.tar.gz</c> back
/// into the filesystem; <c>tar --list</c> reads that archive's headers; <c>tar --extract</c> writes its
/// entries back out; and <c>sha256sum</c> hashes the real bytes. Every assertion about archive
/// <em>content</em> in this folder is therefore a statement about what the provider's command actually
/// produces, not about the string it sent.
/// </para>
/// <para>
/// Paths follow <see cref="SftpFileChannel"/>'s convention that a <see cref="TargetPath"/>'s value is the
/// absolute remote path minus its leading slash, so the keys here are directly comparable to the absolute
/// paths the provider is configured with.
/// </para>
/// </remarks>
internal sealed class SshBackupHost
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _times = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    internal SshBackupHost()
    {
        Target = Substitute.For<IExecutionTarget>();
        Wire();
    }

    /// <summary>The substituted session the backup context is built over.</summary>
    internal IExecutionTarget Target { get; }

    /// <summary>Every command the provider ran, in order.</summary>
    internal List<CommandSpec> Commands { get; } = [];

    /// <summary>Exec, write and delete operations interleaved, so orderings can be asserted directly.</summary>
    internal List<string> Journal { get; } = [];

    /// <summary>Overridable answer for commands this double does not model. Defaults to success.</summary>
    internal Func<CommandSpec, CommandResult?> ExecOverride { get; set; } = _ => null;

    /// <summary>Every absolute path currently on the host.</summary>
    internal IEnumerable<string> Paths => _files.Keys;

    internal SshBackupHost With(string absolutePath, string content, DateTimeOffset? modifiedAt = null) =>
        With(absolutePath, Encoding.UTF8.GetBytes(content), modifiedAt);

    internal SshBackupHost With(string absolutePath, byte[] content, DateTimeOffset? modifiedAt = null)
    {
        _files[absolutePath] = content;
        _times[absolutePath] = modifiedAt ?? DateTimeOffset.UnixEpoch;

        var slash = absolutePath.LastIndexOf('/');
        if (slash > 0)
        {
            _directories.Add(absolutePath[..slash]);
        }

        return this;
    }

    internal bool Has(string absolutePath) => _files.ContainsKey(absolutePath);

    internal byte[] Read(string absolutePath) => _files[absolutePath];

    internal string ReadText(string absolutePath) => Encoding.UTF8.GetString(_files[absolutePath]);

    internal void MakeDirectory(string absolutePath) => _directories.Add(absolutePath.TrimEnd('/'));

    /// <summary>Removes a file behind the provider's back, modelling something outside Servyx moving files.</summary>
    internal void Remove(string absolutePath)
    {
        _files.Remove(absolutePath);
        _times.Remove(absolutePath);
    }

    private static string Absolute(NSubstitute.Core.CallInfo call) => "/" + ((TargetPath)call[0]!).Value;

    private void Wire()
    {
        Target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var spec = (CommandSpec)call[0]!;
                Commands.Add(spec);
                Journal.Add($"exec:{spec.Executable}");
                return Task.FromResult(Execute(spec));
            });

        Target.ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                return Task.FromResult(_files.ContainsKey(path) || DirectoryExists(path));
            });

        Target.StatAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                if (_files.TryGetValue(path, out var bytes))
                {
                    return Task.FromResult(new FileStat(true, false, bytes.LongLength, _times[path], null));
                }

                return Task.FromResult(DirectoryExists(path)
                    ? new FileStat(true, true, null, null, null)
                    : new FileStat(false, false, null, null, null));
            });

        Target.OpenReadAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                Journal.Add($"read:{path}");
                return _files.TryGetValue(path, out var bytes)
                    ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                    : Task.FromException<Stream>(new FileNotFoundException($"No such file '{path}'.", path));
            });

        Target.ListDirectoryAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var directory = Absolute(call).TrimEnd('/');
                Journal.Add($"list:{directory}");

                if (!DirectoryExists(directory))
                {
                    return Task.FromException<IReadOnlyList<FileEntry>>(
                        new DirectoryNotFoundException($"No such directory '{directory}'."));
                }

                var prefix = directory + "/";
                var children = new Dictionary<string, FileEntry>(StringComparer.Ordinal);

                foreach (var (key, bytes) in _files)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var rest = key[prefix.Length..];
                    var slash = rest.IndexOf('/');
                    if (slash < 0)
                    {
                        children[rest] = new FileEntry(rest, false, bytes.LongLength, _times[key]);
                        continue;
                    }

                    var name = rest[..slash];
                    children.TryAdd(name, new FileEntry(name, true, null, null));
                }

                IReadOnlyList<FileEntry> entries = children.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
                return Task.FromResult(entries);
            });

        Target.WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                Journal.Add($"write:{path}");

                using var buffer = new MemoryStream();
                ((Stream)call[1]!).CopyTo(buffer);
                With(path, buffer.ToArray());

                return Task.FromResult(new FileWriteReceipt(null, "sha", DateTimeOffset.UnixEpoch));
            });

        Target.DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                Journal.Add($"delete:{path}");

                if (!_files.Remove(path))
                {
                    return Task.FromException(new FileNotFoundException($"No such file '{path}'.", path));
                }

                _times.Remove(path);
                return Task.CompletedTask;
            });
    }

    private bool DirectoryExists(string directory) =>
        directory is "/" or ""
        || _directories.Contains(directory)
        || _files.Keys.Any(k => k.StartsWith(directory + "/", StringComparison.Ordinal));

    private CommandResult Execute(CommandSpec spec)
    {
        var overridden = ExecOverride(spec);
        if (overridden is not null)
        {
            return overridden;
        }

        return spec.Executable switch
        {
            "mkdir" => Mkdir(spec),
            "tar" => Tar(spec),
            "sha256sum" => Sha256Sum(spec),
            _ => new CommandResult(127, string.Empty, $"{spec.Executable}: not found", TimeSpan.Zero),
        };
    }

    private CommandResult Mkdir(CommandSpec spec)
    {
        foreach (var argument in spec.Arguments.Where(a => !a.StartsWith('-')))
        {
            MakeDirectory(argument);
        }

        return Ok();
    }

    private CommandResult Sha256Sum(CommandSpec spec)
    {
        var path = spec.Arguments[0];
        return _files.TryGetValue(path, out var bytes)
            ? new CommandResult(0, $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {path}\n", string.Empty, TimeSpan.Zero)
            : new CommandResult(1, string.Empty, $"sha256sum: {path}: No such file or directory", TimeSpan.Zero);
    }

    private CommandResult Tar(CommandSpec spec)
    {
        var mode = string.Empty;
        string? file = null;
        var directory = "/";
        var excludes = new List<string>();
        var members = new List<string>();

        for (var i = 0; i < spec.Arguments.Count; i++)
        {
            var argument = spec.Arguments[i];
            switch (argument)
            {
                case "--create" or "--extract" or "--list":
                    mode = argument;
                    break;
                case "--gzip":
                    break;
                case "--file":
                    file = spec.Arguments[++i];
                    break;
                case "--directory":
                    directory = spec.Arguments[++i].TrimEnd('/');
                    break;
                default:
                    if (argument.StartsWith("--exclude=", StringComparison.Ordinal))
                    {
                        excludes.Add(argument["--exclude=".Length..]);
                    }
                    else if (argument.StartsWith('-'))
                    {
                        return new CommandResult(2, string.Empty, $"tar: unrecognized option '{argument}'", TimeSpan.Zero);
                    }
                    else
                    {
                        members.Add(argument);
                    }

                    break;
            }
        }

        if (file is null)
        {
            return new CommandResult(2, string.Empty, "tar: no archive named", TimeSpan.Zero);
        }

        return mode switch
        {
            "--create" => TarCreate(file, directory, members, excludes),
            "--list" => TarList(file),
            "--extract" => TarExtract(file, directory),
            _ => new CommandResult(2, string.Empty, "tar: no operation given", TimeSpan.Zero),
        };
    }

    private CommandResult TarCreate(string file, string directory, List<string> members, List<string> excludes)
    {
        var prefix = directory.Length == 0 ? "/" : directory + "/";

        var selected = new List<(string Relative, byte[] Content)>();
        foreach (var (path, content) in _files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = path[prefix.Length..];
            if (!members.Any(m => Covers(m, relative)) || excludes.Any(x => Excludes(x, relative)))
            {
                continue;
            }

            selected.Add((relative, content));
        }

        // Directory members, exactly as real tar emits them, so the provider's "skip trailing slash" filter
        // is genuinely exercised rather than assumed.
        var directories = selected
            .SelectMany(s => AncestorsOf(s.Relative))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var name in directories)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./" + name + "/"));
            }

            foreach (var (relative, content) in selected)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./" + relative)
                {
                    DataStream = new MemoryStream(content, writable: false),
                });
            }
        }

        With(file, buffer.ToArray());
        return Ok();
    }

    private CommandResult TarList(string file)
    {
        if (!_files.TryGetValue(file, out var archive))
        {
            return new CommandResult(2, string.Empty, $"tar: {file}: Cannot open", TimeSpan.Zero);
        }

        var names = new StringBuilder();
        foreach (var entry in EntriesOf(archive))
        {
            names.Append(entry.Name).Append('\n');
        }

        return new CommandResult(0, names.ToString(), string.Empty, TimeSpan.Zero);
    }

    private CommandResult TarExtract(string file, string directory)
    {
        if (!_files.TryGetValue(file, out var archive))
        {
            return new CommandResult(2, string.Empty, $"tar: {file}: Cannot open", TimeSpan.Zero);
        }

        var prefix = directory.Length == 0 ? "/" : directory + "/";
        foreach (var entry in EntriesOf(archive))
        {
            if (entry.Content is null)
            {
                MakeDirectory(prefix + entry.Name.TrimEnd('/'));
                continue;
            }

            With(prefix + entry.Name, entry.Content);
        }

        return Ok();
    }

    private static IEnumerable<string> AncestorsOf(string relative)
    {
        var segments = relative.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            yield return string.Join('/', segments[..i]);
        }
    }

    /// <summary>Whether a <c>tar</c> member argument covers a root-relative path.</summary>
    private static bool Covers(string member, string relative) =>
        member is "." or "./"
        || string.Equals(member, relative, StringComparison.Ordinal)
        || relative.StartsWith(member.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>
    /// GNU <c>tar</c>'s <c>--exclude</c>, modelled closely enough for these tests: the pattern is matched
    /// against the member name with <c>*</c>/<c>?</c> wildcards, and a directory match takes its subtree with
    /// it (tar's leading-directory behaviour).
    /// </summary>
    private static bool Excludes(string pattern, string relative) =>
        GlobMatches(pattern, relative)
        || relative.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static bool GlobMatches(string pattern, string value)
    {
        var translated = new StringBuilder("^");
        foreach (var c in pattern)
        {
            translated.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(c.ToString()),
            });
        }

        translated.Append('$');
        return Regex.IsMatch(value, translated.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static IEnumerable<(string Name, byte[]? Content)> EntriesOf(byte[] archive)
    {
        using var raw = new MemoryStream(archive, writable: false);
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: true);

        var results = new List<(string, byte[]?)>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;

            if (entry.EntryType == TarEntryType.Directory)
            {
                results.Add((name, null));
                continue;
            }

            using var content = new MemoryStream();
            entry.DataStream?.CopyTo(content);
            results.Add((name, content.ToArray()));
        }

        return results;
    }

    private static CommandResult Ok() => new(0, string.Empty, string.Empty, TimeSpan.Zero);
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class FrozenClock(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A context source returning a single, pre-built context.</summary>
internal sealed class StaticSshContextSource(SshBackupContext context) : ISshBackupContextSource
{
    internal int Calls { get; private set; }

    public Task<SshBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(context);
    }
}

/// <summary>
/// A stand-in for the adopter a host with a known layout would register.
/// </summary>
/// <remarks>
/// This project ships no adopter (see <see cref="ForeignSshBackupDirectory"/>), so foreign artifacts can only
/// reach <see cref="SshBackupProvider"/> through one a composition root supplied. This is that shape: pure
/// read-only discovery, reporting <see cref="BackupOwnership.Foreign"/> and nothing else.
/// </remarks>
internal sealed class StubForeignAdopter(string deploymentKind, params string[] locations) : IBackupAdopter
{
    /// <summary>Ownership every discovered artifact is reported with. Overridable to prove the provider rejects a lie.</summary>
    internal BackupOwnership Ownership { get; set; } = BackupOwnership.Foreign;

    public string AdapterId => "stub-ssh-cron";

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
/// The scenario every test in this folder builds on: one SSH host holding a game server's data directory,
/// a Servyx artifact directory beside it, and a cron job's own archive directory beside that.
/// </summary>
internal sealed class SshBackupScenario
{
    internal const string ServerId = "valheim-server";
    internal const string DeploymentKind = "ssh-process";
    internal const string Root = "/srv/valheim";
    internal const string StoreDirectory = "servyx-backups";
    internal const string ForeignDirectory = "/srv/valheim/cron-backups";

    internal SshBackupScenario(WriteMode? writeMode = null)
    {
        Host = new SshBackupHost();
        Clock = new FrozenClock(new DateTimeOffset(2026, 7, 29, 10, 15, 0, TimeSpan.Zero));
        WriteMode = writeMode;
    }

    internal SshBackupHost Host { get; }

    internal FrozenClock Clock { get; }

    /// <summary>When set, the context's target is wrapped in a <see cref="WriteGuardedExecutionTarget"/>.</summary>
    internal WriteMode? WriteMode { get; }

    internal IReadOnlyList<string> Include { get; set; } = ["."];

    internal IReadOnlyList<string> Exclude { get; set; } = ["logs", "logs/*"];

    internal RetentionPolicy Retention { get; set; } = new(6, 7, 4);

    internal IReadOnlyList<ForeignSshBackupDirectory> Foreign { get; set; } =
        [new ForeignSshBackupDirectory("stub-ssh-cron", ForeignDirectory, "*.tar.gz")];

    /// <summary>
    /// The control channel a quiesce is issued through, or null when the operator configured none. Null is
    /// the pre-quiesce shape and must stay byte-for-byte equivalent to it.
    /// </summary>
    internal IRconSession? Control { get; set; }

    /// <summary>The pre-archive flush, or null when the context declares none.</summary>
    internal QuiesceStep? Quiesce { get; set; }

    /// <summary>The target handed to the provider — guarded when <see cref="WriteMode"/> is set, bare otherwise.</summary>
    internal IExecutionTarget ContextTarget => WriteMode is { } mode
        ? new WriteGuardedExecutionTarget(Host.Target, mode, ServerId)
        : Host.Target;

    /// <summary>Seeds the file layout a Valheim dedicated server actually produces.</summary>
    internal SshBackupScenario WithGameLayout()
    {
        Host.With($"{Root}/worlds_local/Dedicated.db", "world");
        Host.With($"{Root}/worlds_local/Dedicated.fwl", "meta");
        Host.With($"{Root}/config/server.cfg", "name=test");
        Host.With($"{Root}/logs/server.log", "noisy");
        return this;
    }

    /// <summary>Adds archives the host's own cron job created.</summary>
    internal SshBackupScenario WithForeignArchives(params string[] names)
    {
        foreach (var name in names)
        {
            Host.With(
                $"{ForeignDirectory}/{name}",
                Encoding.UTF8.GetBytes("not-a-real-archive-and-servyx-never-opens-it"),
                new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero));
        }

        return this;
    }

    /// <summary>Adds Servyx-owned archives (plus their sidecar manifests) at the given timestamps.</summary>
    internal SshBackupScenario WithServyxArchives(params DateTimeOffset[] timestamps)
    {
        foreach (var at in timestamps)
        {
            var name = $"servyx-{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.tar.gz";
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

            Host.With($"{Root}/{StoreDirectory}/{name}", archive, at);
            Host.With($"{Root}/{StoreDirectory}/{name}{SshBackupProvider.ManifestSuffix}", manifest.ToUtf8Json(), at);
        }

        return this;
    }

    internal SshBackupContext Build() => new(
        ServerId,
        DeploymentKind,
        ContextTarget,
        Root,
        Include,
        Exclude,
        StoreDirectory,
        Foreign,
        Retention,
        Quiesce,
        Control);

    internal StaticSshContextSource Source() => new(Build());

    internal SshBackupProvider Provider(IEnumerable<IBackupAdopter>? adopters = null, TimeSpan? planTtl = null) =>
        new(Source(), adopters, Clock, planTtl);

    /// <summary>A provider whose only adopter reports the seeded cron archives as foreign.</summary>
    internal SshBackupProvider ProviderWithForeign(params string[] names) =>
        Provider([new StubForeignAdopter(DeploymentKind, names.Select(n => $"{ForeignDirectory}/{n}").ToArray())]);

    internal static string ServyxBackupId(string fileName) =>
        BackupArtifactId.Format(ServerId, $"{Root}/{StoreDirectory}/{fileName}");

    internal static string ForeignBackupId(string fileName) =>
        BackupArtifactId.Format(ServerId, $"{ForeignDirectory}/{fileName}");

    /// <summary>Reads back the entry names of an archive the provider's tar produced.</summary>
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
}
