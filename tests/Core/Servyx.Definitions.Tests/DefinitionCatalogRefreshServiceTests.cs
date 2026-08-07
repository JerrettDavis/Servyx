using System.Threading.Channels;
using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// <see cref="DefinitionCatalogRefreshService"/>'s pump-isolation guarantee: one provider's
/// <c>WatchAsync</c> stream dying with an unexpected exception must not silence the service, must be
/// recorded as a visible fault promptly (not only discovered at shutdown), and must not affect any other
/// provider's own pump.
/// </summary>
public class DefinitionCatalogRefreshServiceTests
{
    private static string ValidYamlWithId(string id) =>
        DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {id}\n");

    private static GameDefinition ParseValidDefinition(string id)
    {
        var result = new GameDefinitionYamlParser().Parse(ValidYamlWithId(id));
        return result.Definition ?? throw new InvalidOperationException(
            "Fixture YAML failed to parse: " + string.Join("; ", result.Report.Issues.Select(i => i.Message)));
    }

    // A synchronous throw, not an async-iterator with an unreachable `yield break` after it — this is
    // exactly equivalent from PumpAsync's point of view (the exception surfaces from evaluating
    // `provider.WatchAsync(ct)` itself, inside the same try block that wraps the `await foreach`), and it
    // sidesteps having to write an iterator whose only path never actually yields.
    private static IAsyncEnumerable<GameDefinitionRef> ThrowingWatch(CancellationToken ct) =>
        throw new InvalidOperationException("Simulated watch failure.");

    [Fact]
    public async Task ExecuteAsync_OneProviderWatchThrows_ServiceSurvives_FaultRecorded_OtherProviderKeepsPumping()
    {
        const string faultyProviderId = "faulty-provider";
        const string healthyProviderId = "healthy-provider";

        var faultyProvider = new FakeGameDefinitionProvider
        {
            SourceId = faultyProviderId,
            OnList = _ => Task.FromResult<IReadOnlyList<GameDefinitionRef>>(Array.Empty<GameDefinitionRef>()),
            OnWatch = ThrowingWatch,
        };

        var healthyRefV1 = new GameDefinitionRef("healthy-game", "hash-v1", healthyProviderId);
        var healthyRefV2 = new GameDefinitionRef("healthy-game", "hash-v2", healthyProviderId);
        var definitionV1 = ParseValidDefinition("healthy-game");
        var definitionV2 = ParseValidDefinition("healthy-game");
        var trust = new TrustVerdict(TrustTier.Unverified, Array.Empty<string>(), null);

        var currentHealthyRef = healthyRefV1;
        var watchSignal = Channel.CreateUnbounded<GameDefinitionRef>();

        var healthyProvider = new FakeGameDefinitionProvider
        {
            SourceId = healthyProviderId,
            OnList = _ => Task.FromResult<IReadOnlyList<GameDefinitionRef>>([currentHealthyRef]),
            OnLoad = (reference, _) => Task.FromResult(new LoadedDefinition(
                reference,
                trust,
                string.Equals(reference.ContentHash, healthyRefV1.ContentHash, StringComparison.Ordinal) ? definitionV1 : definitionV2)),
            OnWatch = ct => watchSignal.Reader.ReadAllAsync(ct),
        };

        var catalog = new GameDefinitionCatalog([faultyProvider, healthyProvider]);
        var service = new DefinitionCatalogRefreshService(catalog, [faultyProvider, healthyProvider], watch: true);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The unconditional initial refresh (regardless of Watch) populates the healthy provider's
            // definition — proves startup itself is unaffected by the faulty provider even existing.
            await PollUntilAsync(
                () => catalog.TryGetById("healthy-game") is not null,
                "the initial refresh should populate the healthy provider's definition");

            // The faulty provider's WatchAsync fails essentially immediately once watching begins; the
            // fault must surface promptly, not only discovered via a crash at shutdown.
            await PollUntilAsync(
                () => catalog.Faults.Any(f => f.Path == faultyProviderId),
                "the faulty provider's dead watch stream should be recorded as a fault while the service keeps running");

            // The healthy provider's own pump is unaffected by the other one dying: a hot-reload signal on
            // it must still trigger a refresh that picks up the new content.
            currentHealthyRef = healthyRefV2;
            await watchSignal.Writer.WriteAsync(healthyRefV2);

            await PollUntilAsync(
                () => catalog.TryGetById("healthy-game")?.Ref.ContentHash == healthyRefV2.ContentHash,
                "the healthy provider's watch stream must keep triggering refreshes after the other provider's stream died");
        }
        finally
        {
            // The service must still be stoppable cleanly — proof it never crashed/faulted internally
            // despite the faulty provider's pump task failing.
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task PollUntilAsync(Func<bool> condition, string because, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        condition().Should().BeTrue(because);
    }
}
