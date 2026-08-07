using System.Runtime.CompilerServices;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests.Support;

/// <summary>
/// A fully scriptable <see cref="IGameDefinitionProvider"/> for exercising catalog/refresh-service
/// behaviour that a real <c>FileSystemGameDefinitionProvider</c> cannot be made to hit deterministically —
/// most notably the list-then-delete TOCTOU race and a <c>WatchAsync</c> stream that dies mid-flight. Every
/// member defaults to a harmless no-op so a test only has to configure the delegate(s) it actually cares
/// about.
/// </summary>
internal sealed class FakeGameDefinitionProvider : IGameDefinitionProvider, IDefinitionCatalogDiagnostics
{
    public required string SourceId { get; init; }

    public Func<CancellationToken, Task<IReadOnlyList<GameDefinitionRef>>>? OnList { get; set; }

    public Func<GameDefinitionRef, CancellationToken, Task<LoadedDefinition>>? OnLoad { get; set; }

    public Func<CancellationToken, IAsyncEnumerable<GameDefinitionRef>>? OnWatch { get; set; }

    public IReadOnlyList<DefinitionFault> Faults { get; set; } = Array.Empty<DefinitionFault>();

    public int ListCallCount { get; private set; }

    public Task<IReadOnlyList<GameDefinitionRef>> ListAsync(CancellationToken ct = default)
    {
        ListCallCount++;
        return OnList?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<GameDefinitionRef>>(Array.Empty<GameDefinitionRef>());
    }

    public Task<LoadedDefinition> LoadAsync(GameDefinitionRef reference, CancellationToken ct = default) =>
        OnLoad?.Invoke(reference, ct) ?? throw new NotSupportedException($"{nameof(OnLoad)} was not configured on this fake.");

    public IAsyncEnumerable<GameDefinitionRef> WatchAsync(CancellationToken ct = default) =>
        OnWatch?.Invoke(ct) ?? EmptyAsync(ct);

    private static async IAsyncEnumerable<GameDefinitionRef> EmptyAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
