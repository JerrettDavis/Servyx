using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests;

/// <summary>
/// <see cref="FileSystemGameDefinitionProvider.WatchAsync"/>'s debounce-and-hash-compare behaviour.
/// </summary>
/// <remarks>
/// <strong>Deterministic waiting, no bare <see cref="Thread.Sleep"/> races.</strong> Each test obtains the
/// watch sequence's <see cref="IAsyncEnumerator{T}"/> directly and issues (but does not yet await) its
/// first <c>MoveNextAsync()</c> call before touching any file. An async-iterator method's body runs
/// synchronously, on the caller's thread, from its start up to its first genuine suspension point — here,
/// the as-yet-empty channel read — so by the time that call returns control (even though the returned
/// <see cref="ValueTask{Boolean}"/> is not yet complete), <see cref="FileSystemGameDefinitionProvider"/>'s
/// <see cref="System.IO.FileSystemWatcher"/> is already attached and listening. Only then is the fixture
/// file written. Waiting itself uses <see cref="Task.WhenAny(Task,Task)"/> against a bounded
/// <see cref="Task.Delay(TimeSpan)"/> as an explicit timeout guard, never an unconditional sleep.
/// </remarks>
public class FileSystemGameDefinitionProviderWatchTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    // Anchored on the line ending so this never also matches the "id: palworldsettings" surface id declared
    // by both deployment profiles — see the identical comment in FileSystemGameDefinitionProviderTests.
    private static string ValidYamlWithId(string id) =>
        DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {id}\n");

    [Fact]
    public async Task WatchAsync_RealContentChange_EmitsExactlyOneEventAfterDebounce()
    {
        using var dir = new TempDefinitionsDirectory();
        var path = dir.WriteFlat("watched.yaml", ValidYamlWithId("watch-game"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        using var cts = new CancellationTokenSource();

        await using var enumerator = provider.WatchAsync(cts.Token).GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask(); // Watcher is attached by the time this returns.

        File.WriteAllText(path, ValidYamlWithId("watch-game-renamed"));

        var winner = await Task.WhenAny(moveNextTask, Task.Delay(EventTimeout, cts.Token));
        winner.Should().Be(moveNextTask, "a real content change should be reported within the debounce window plus a margin");

        (await moveNextTask).Should().BeTrue();
        enumerator.Current.SourceId.Should().Be("directory");

        cts.Cancel();
    }

    [Fact]
    public async Task WatchAsync_NoOpRewrite_SameBytes_EmitsNoEvent()
    {
        using var dir = new TempDefinitionsDirectory();
        var yaml = ValidYamlWithId("watch-noop-game");
        var path = dir.WriteFlat("watched.yaml", yaml);

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        using var cts = new CancellationTokenSource();

        await using var enumerator = provider.WatchAsync(cts.Token).GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask(); // Watcher is attached by the time this returns.

        // Rewrite the exact same bytes — an editor's "save" that changed nothing.
        File.WriteAllText(path, yaml);

        var winner = await Task.WhenAny(moveNextTask, Task.Delay(QuietPeriod, cts.Token));
        winner.Should().NotBe(moveNextTask, "identical bytes must never produce a hot-reload event");

        // Resolve the still-pending MoveNextAsync before the enumerator is disposed below — a compiler-
        // generated async iterator does not support disposal while a MoveNextAsync call is still in flight.
        cts.Cancel();
        var act = async () => await moveNextTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
