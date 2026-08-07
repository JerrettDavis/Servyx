using System.Runtime.CompilerServices;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// Test-only helpers for exercising <c>LiveDashboardDataService.GetServerSavesWithStatusAsync</c> without a
/// live Docker daemon: an in-memory <see cref="IExecutionTarget"/> filesystem, a scriptable
/// <see cref="ITransport"/> over it, and a way to build a minimal <see cref="GameDefinition"/> (and the
/// single-entry <see cref="GameDefinitionCatalog"/> that carries it) with an arbitrary <see cref="SavesLayout"/> —
/// including layouts a real <c>GameDefinitionYamlParser</c> would refuse to load (a catastrophic-backtracking
/// <c>worldIdPattern</c>), which is the whole point of constructing one directly rather than through YAML.
/// </summary>
public static class SavesFakes
{
    /// <summary>
    /// Builds the smallest <see cref="GameDefinition"/> that type-checks, with <paramref name="saves"/> as
    /// its <c>saves</c> block. Every other block is empty/default — nothing under test reads them.
    /// </summary>
    public static GameDefinition MinimalDefinition(SavesLayout? saves, string id = "test-game") => new(
        ApiVersion: "servyx.dev/v1",
        Metadata: new GameMetadata(id, "Test Game", "1.0.0", null, [], null, null, null, null, null, null),
        Capabilities: new Capabilities([], [], [], Shell: false, Privileged: false, HostNetwork: false),
        Deployments: [],
        Lifecycle: new LifecycleDefinition([], new StopPlan([]), []),
        Control: new ControlPlane([], null),
        Settings: [],
        Backup: new BackupPolicy([], [], [], [], null),
        Saves: saves,
        Mods: new ModsPolicy(Supported: false));

    /// <summary>Wraps a single <see cref="GameDefinition"/> in a refreshed, ready-to-query <see cref="GameDefinitionCatalog"/>.</summary>
    public static async Task<GameDefinitionCatalog> CatalogFor(GameDefinition definition)
    {
        var catalog = new GameDefinitionCatalog([new SingleDefinitionProvider(definition)]);
        await catalog.RefreshAsync();
        return catalog;
    }

    /// <summary>An <see cref="IGameDefinitionProvider"/> that always lists and loads exactly one, already-built definition.</summary>
    private sealed class SingleDefinitionProvider(GameDefinition definition) : IGameDefinitionProvider
    {
        public string SourceId => "test";

        public Task<IReadOnlyList<GameDefinitionRef>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameDefinitionRef>>(
                [new GameDefinitionRef(definition.Metadata.Id, "test-content-hash", SourceId)]);

        public Task<LoadedDefinition> LoadAsync(GameDefinitionRef reference, CancellationToken ct = default) =>
            Task.FromResult(new LoadedDefinition(reference, new TrustVerdict(TrustTier.Builtin, [], null), definition));

        public async IAsyncEnumerable<GameDefinitionRef> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

/// <summary>
/// A minimal, in-memory <see cref="IExecutionTarget"/> backed by a flat dictionary of root-relative paths.
/// Supports exactly the read-only surface saves-inspection needs (<see cref="ListDirectoryAsync"/>,
/// <see cref="StatAsync"/>, <see cref="ExistsAsync"/>); every mutating or exec member throws, since nothing
/// under test should ever reach them — a read-only feature reaching for a write is itself a bug worth
/// failing loudly over.
/// </summary>
public sealed class InMemoryExecutionTarget : IExecutionTarget
{
    private sealed record Node(bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    /// <summary>Adds a directory at <paramref name="path"/> (root-relative, '/'-separated).</summary>
    public InMemoryExecutionTarget AddDirectory(string path, DateTimeOffset? modifiedAt = null)
    {
        _nodes[Normalize(path)] = new Node(true, null, modifiedAt);
        return this;
    }

    /// <summary>Adds a file at <paramref name="path"/> with the given size.</summary>
    public InMemoryExecutionTarget AddFile(string path, long sizeBytes, DateTimeOffset? modifiedAt = null)
    {
        _nodes[Normalize(path)] = new Node(false, sizeBytes, modifiedAt);
        return this;
    }

    private static string Normalize(string path) => path.Trim('/');

    /// <inheritdoc />
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
        Task.FromResult(_nodes.ContainsKey(path.Value));

    /// <inheritdoc />
    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(path.Value, out var node))
        {
            return Task.FromResult(new FileStat(false, false, null, null, null));
        }

        return Task.FromResult(new FileStat(true, node.IsDirectory, node.SizeBytes, node.ModifiedAt, null));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default)
    {
        var key = path.Value;
        if (key.Length > 0 && !_nodes.ContainsKey(key))
        {
            throw new DirectoryNotFoundException($"'{key}' does not exist.");
        }

        var prefix = key.Length == 0 ? "" : key + "/";
        var children = new Dictionary<string, FileEntry>(StringComparer.Ordinal);

        foreach (var (candidate, node) in _nodes)
        {
            if (candidate == key || !candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = candidate[prefix.Length..];
            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            var isDescendant = slash >= 0;

            if (children.TryGetValue(name, out var existing))
            {
                if (isDescendant && !existing.IsDirectory)
                {
                    children[name] = existing with { IsDirectory = true };
                }

                continue;
            }

            children[name] = new FileEntry(
                name,
                isDescendant || node.IsDirectory,
                isDescendant ? null : node.SizeBytes,
                isDescendant ? null : node.ModifiedAt);
        }

        return Task.FromResult<IReadOnlyList<FileEntry>>(children.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList());
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException($"{nameof(InMemoryExecutionTarget)} does not support reading file content.");

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException($"{nameof(InMemoryExecutionTarget)} does not support command execution.");

    /// <inheritdoc />
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException($"{nameof(InMemoryExecutionTarget)} does not support streaming execution.");

    /// <inheritdoc />
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException($"{nameof(InMemoryExecutionTarget)} is read-only.");

    /// <inheritdoc />
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException($"{nameof(InMemoryExecutionTarget)} is read-only.");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A scriptable <see cref="ITransport"/> that hands back a fixed <see cref="IExecutionTarget"/> (or throws a
/// fixed exception) from <see cref="ConnectAsync"/>, and records the last <see cref="TargetDescriptor"/> it
/// was asked to connect.
/// </summary>
public sealed class FakeSavesTransport : ITransport
{
    /// <summary>The target <see cref="ConnectAsync"/> returns. Must be set before use unless <see cref="ConnectThrows"/> is set.</summary>
    public IExecutionTarget? Target { get; set; }

    /// <summary>When set, <see cref="ConnectAsync"/> throws this instead of returning <see cref="Target"/>.</summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>The most recent descriptor passed to <see cref="ConnectAsync"/>.</summary>
    public TargetDescriptor? LastDescriptor { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Settable — defaults to <c>"docker"</c>, matching the real <c>DockerTransport</c> this fake stands in
    /// for. Used only for the failure-detail message text; the refusal gate itself is driven by
    /// <see cref="Capabilities"/>, not this id.
    /// </remarks>
    public string TransportId { get; set; } = "docker";

    /// <inheritdoc />
    /// <remarks>
    /// Settable — defaults to what the real <c>DockerTransport</c> declares, including
    /// <see cref="TransportCapabilities.ContainerScopedFiles"/>. Clear that flag (e.g.
    /// <c>TransportCapabilities.FileRead | TransportCapabilities.DirectoryList</c>, matching
    /// <c>SshDockerTransport</c>) to exercise the refusal
    /// <c>LiveDashboardDataService.GetServerSavesWithStatusAsync</c> applies before any session is opened.
    /// </remarks>
    public TransportCapabilities Capabilities { get; set; } =
        TransportCapabilities.FileRead | TransportCapabilities.DirectoryList | TransportCapabilities.ContainerScopedFiles;

    /// <inheritdoc />
    public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
        Task.FromResult(new TargetHealth(true, TimeSpan.Zero, null));

    /// <inheritdoc />
    public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        LastDescriptor = target;

        return ConnectThrows is not null
            ? Task.FromException<IExecutionTarget>(ConnectThrows)
            : Task.FromResult(Target ?? throw new InvalidOperationException($"{nameof(FakeSavesTransport)}.{nameof(Target)} was not set."));
    }
}
