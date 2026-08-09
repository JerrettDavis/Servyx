using Servyx.Composition;
using Servyx.Domain.Entities;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The one interleaving in which a revocation can be silently lost: an invalidation that lands while a cache
/// load is already in flight.
/// </summary>
/// <remarks>
/// <para>
/// <c>Invalidate()</c> nulls the snapshot field. When a load has already read the pre-write rows but has not
/// yet published them, that null is applied to an <em>already-null</em> field and is therefore a no-op — and
/// the load then publishes the pre-revoke grant set, with no further invalidation scheduled. The loss
/// direction is unsafe (write access stays live), it is silent (the write returned success and the UI shows
/// ReadOnly), and it persists until an unrelated grant change or a process restart. The window opens on every
/// reload, which is exactly when grant churn happens.
/// </para>
/// <para>
/// <strong>This is deterministic, not timed.</strong> The interleaving is driven by
/// <c>WriteGrantCache.LoadInterleaveHookForTests</c> and two events, so the load is provably parked at the
/// publish point while the revoke commits. A version of this test built on <c>Task.Delay</c> would hit that
/// window only sometimes — and would therefore pass against the broken implementation most of the time,
/// which is a green check attesting to a property the code does not have.
/// </para>
/// </remarks>
public class WriteGrantCacheRaceTests : IDisposable
{
    private const string ContainerId = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ContainerName = "palworld-server";

    private readonly WriteGrantTestDatabase _database = new();
    private readonly ProvisioningGate _gate = new(enabled: true);

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task An_invalidation_that_lands_while_a_load_is_in_flight_does_not_lose_the_revocation()
    {
        var cache = new WriteGrantCache(_gate, _database.Factory);
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        using var loadParkedAtThePublishPoint = new ManualResetEventSlim(false);
        using var revokeCommitted = new ManualResetEventSlim(false);

        cache.LoadInterleaveHookForTests = () =>
        {
            loadParkedAtThePublishPoint.Set();
            revokeCommitted.Wait(TimeSpan.FromSeconds(30));
        };

        // The reader. Its load reads the pre-revoke row and then parks holding that snapshot, at exactly the
        // instant the publish race opens. It holds the cache's load lock while parked, which is the real
        // shape of the race and is why the revoke below must not need that lock.
        var reader = Task.Run(() => cache.ModeFor(ContainerId));

        loadParkedAtThePublishPoint.Wait(TimeSpan.FromSeconds(30))
            .Should().BeTrue("the load must actually reach the publish point, or this test proves nothing");

        // The operator's revoke, committed and invalidated while the reader is parked. Going through the
        // real service and the real repository, not a hand-rolled Invalidate(), so this is the sequence
        // SetWriteModeAsync actually performs.
        var grants = new WriteGrantService(
            _gate,
            new GrantInvalidatingServerRepository(_database.Repository, cache),
            cache,
            new RecordingLogger());

        (await grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator")).Applied.Should().BeTrue();

        revokeCommitted.Set();

        (await reader).Should().Be(ServerWriteMode.Enabled,
            "the racing caller asked before the change landed and is served the answer its own read produced; " +
            "what must not happen is that answer becoming the CACHED one");

        cache.LoadInterleaveHookForTests = null;

        cache.ModeFor(ContainerId).Should().Be(ServerWriteMode.ReadOnly,
            because: "the revoke returned success, so the very next command anywhere in this process must see " +
                "it — a snapshot that raced the invalidation must never be published, or write access stays " +
                "live with nothing scheduled to clear it");
    }

    [Fact]
    public async Task An_invalidation_that_lands_between_the_version_check_and_the_publish_is_not_lost_either()
    {
        // The narrower sibling of the test above, and the identical defect: the pre-publish version check
        // and the assignment it guards are two statements, not one atomic operation. An Invalidate() that
        // lands entirely between them increments the version and nulls an already-null field, and the
        // assignment then writes the stale snapshot straight over that null. A couple of instructions rather
        // than a whole database read — and exactly as silent, and exactly as unsafe, when it happens.
        var cache = new WriteGrantCache(_gate, _database.Factory);
        var id = _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        using var loadParkedBetweenCheckAndPublish = new ManualResetEventSlim(false);
        using var revokeCommitted = new ManualResetEventSlim(false);

        // Parks AFTER the pre-publish check has already passed, so that check cannot be what saves this —
        // only the re-check performed after the assignment can.
        cache.PublishInterleaveHookForTests = () =>
        {
            loadParkedBetweenCheckAndPublish.Set();
            revokeCommitted.Wait(TimeSpan.FromSeconds(30));
        };

        var reader = Task.Run(() => cache.ModeFor(ContainerId));

        loadParkedBetweenCheckAndPublish.Wait(TimeSpan.FromSeconds(30))
            .Should().BeTrue("the load must actually reach the window between the check and the publish");

        var grants = new WriteGrantService(
            _gate,
            new GrantInvalidatingServerRepository(_database.Repository, cache),
            cache,
            new RecordingLogger());

        (await grants.SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator")).Applied.Should().BeTrue();

        revokeCommitted.Set();

        (await reader).Should().Be(ServerWriteMode.Enabled);

        cache.PublishInterleaveHookForTests = null;

        cache.ModeFor(ContainerId).Should().Be(ServerWriteMode.ReadOnly,
            because: "the invalidation's own null had already landed when this load overwrote it, so the " +
                "load has to notice the version moved and retract what it just published — otherwise the " +
                "revoke is lost exactly as it was in the wider window");
    }

    [Fact]
    public void A_load_that_did_not_race_an_invalidation_is_still_published()
    {
        // Anti-vacuity for the test above: declining to publish must be scoped to the racing case. A version
        // check that suppressed every publish would also make it pass, while turning the cache into a
        // database round-trip per guarded command.
        var cache = new WriteGrantCache(_gate, _database.Factory);
        _database.AddServer(ContainerId, ContainerName, ServerWriteMode.Enabled);

        cache.ModeFor(ContainerId).Should().Be(ServerWriteMode.Enabled);

        // Deleted behind the cache's back — no repository, no invalidation — so the only way this still
        // answers Enabled is if the first load's snapshot was genuinely cached.
        using (var context = _database.CreateContext())
        {
            context.Servers.RemoveRange(context.Servers);
            context.SaveChanges();
        }

        cache.ModeFor(ContainerId).Should().Be(ServerWriteMode.Enabled,
            because: "an uncontended load publishes its snapshot; the write guard is consulted per command " +
                "and cannot pay for a query each time");
    }
}
