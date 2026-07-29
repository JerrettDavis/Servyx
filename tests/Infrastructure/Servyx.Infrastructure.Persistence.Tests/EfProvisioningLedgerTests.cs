using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Persistence.Provisioning;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// Tests for <see cref="EfProvisioningLedger"/> — the durable half of the write-ahead ledger.
/// </summary>
/// <remarks>
/// Every assertion here reads back through a <em>second, separate</em> <c>ServyxDbContext</c> obtained from
/// <see cref="SqliteDatabaseFixture.CreateContext"/>. That is not ceremony: re-reading through the context
/// that performed the write would be answered from its identity map, which would let an implementation that
/// never actually committed pass. Durability is the only thing this type is for, so the tests must cross a
/// context boundary to observe it.
/// </remarks>
public class EfProvisioningLedgerTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 1, 12, 0, 42, TimeSpan.Zero);

    [Fact]
    public async Task RecordedIntent_SurvivesIntoACompletelyFreshDbContext()
    {
        using var fixture = new SqliteDatabaseFixture();

        var intent = NewIntent();

        // The writing context is disposed entirely — nothing of it, tracked or cached, can serve the read.
        await using (var writeContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(writeContext).RecordIntentAsync(intent);
        }

        await using var freshContext = fixture.CreateContext();
        var loaded = await freshContext.ProvisionedResources.SingleAsync();

        loaded.Id.Should().Be(intent.LedgerRowId);
        loaded.State.Should().Be(ResourceLifecycleState.Intended);

        // Null because the provider has not been asked yet. This is the shape that must be on disk *before*
        // the billable call, and it is why ProviderResourceId is nullable.
        loaded.ProviderResourceId.Should().BeNull();
        loaded.ProvisionerId.Should().Be("hetzner");
        loaded.Region.Should().Be("fsn1");
        loaded.JobId.Should().Be("job-1");
        loaded.CreatedAt.Should().Be(RecordedAt);
        loaded.UpdatedAt.Should().Be(RecordedAt);
        loaded.Tags.Should().BeEquivalentTo(intent.Tags);
    }

    [Fact]
    public async Task MarkCreated_PersistsTheProviderAssignedId_ReadBackThroughAFreshDbContext()
    {
        using var fixture = new SqliteDatabaseFixture();

        var intent = NewIntent();

        await using (var writeContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(writeContext).RecordIntentAsync(intent);
        }

        // ...the billable provider call happens here, out of process...

        await using (var promoteContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(promoteContext)
                .MarkCreatedAsync(intent.LedgerRowId, "vm-991823", ObservedAt);
        }

        await using var freshContext = fixture.CreateContext();
        var loaded = await freshContext.ProvisionedResources.SingleAsync();

        loaded.State.Should().Be(ResourceLifecycleState.Created);
        loaded.ProviderResourceId.Should().Be("vm-991823");

        // CreatedAt records the intent, UpdatedAt the confirmation; the gap between them is the window a
        // crash would have left the row Intended in.
        loaded.CreatedAt.Should().Be(RecordedAt);
        loaded.UpdatedAt.Should().Be(ObservedAt);
    }

    [Fact]
    public async Task MarkCreated_ThrowsWhenNoIntentWasEverRecorded()
    {
        using var fixture = new SqliteDatabaseFixture();

        await using var context = fixture.CreateContext();
        var ledger = new EfProvisioningLedger(context);

        // Creating without a committed intent is the exact ordering violation the ledger exists to prevent,
        // so it must be surfaced rather than papered over with a late insert.
        var act = async () => await ledger.MarkCreatedAsync(Guid.NewGuid(), "vm-1", ObservedAt);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListIntended_ReturnsOnlyRowsStillInTheIntendedState()
    {
        using var fixture = new SqliteDatabaseFixture();

        var stillIntended = NewIntent();
        var promoted = NewIntent();
        var otherProvisioner = NewIntent() with { ProvisionerId = "digitalocean" };

        await using (var writeContext = fixture.CreateContext())
        {
            var ledger = new EfProvisioningLedger(writeContext);
            await ledger.RecordIntentAsync(stillIntended);
            await ledger.RecordIntentAsync(promoted);
            await ledger.RecordIntentAsync(otherProvisioner);
            await ledger.MarkCreatedAsync(promoted.LedgerRowId, "vm-1", ObservedAt);
        }

        await using var freshContext = fixture.CreateContext();

        // The orphan sweep's entry query: only resources that may exist at the provider without Servyx
        // having confirmed them, and only for the provisioner being swept.
        var intended = await new EfProvisioningLedger(freshContext).ListIntendedAsync("hetzner");

        intended.Should().ContainSingle();
        intended[0].LedgerRowId.Should().Be(stillIntended.LedgerRowId);
        intended[0].ProvisionerId.Should().Be("hetzner");
        intended[0].JobId.Should().Be("job-1");
        intended[0].RecordedAt.Should().Be(RecordedAt);
    }

    [Fact]
    public async Task ListCreated_ReturnsTheConfirmedRow_WithTheProviderAssignedId_ThroughAFreshDbContext()
    {
        using var fixture = new SqliteDatabaseFixture();

        var intent = NewIntent();

        // Both writes happen in contexts that are then disposed entirely, so nothing tracked or cached can
        // serve the read below. Only what is genuinely on disk can answer.
        await using (var writeContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(writeContext).RecordIntentAsync(intent);
        }

        await using (var promoteContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(promoteContext)
                .MarkCreatedAsync(intent.LedgerRowId, "vm-991823", ObservedAt);
        }

        await using var freshContext = fixture.CreateContext();
        var created = await new EfProvisioningLedger(freshContext).ListCreatedAsync("hetzner");

        created.Should().ContainSingle();
        created[0].LedgerRowId.Should().Be(intent.LedgerRowId);

        // The whole reason this enumeration exists: a complete, non-nullable handle naming the resource by
        // the id the provider itself assigned, rather than parts a caller has to reassemble and null-check.
        created[0].Handle.ProviderResourceId.Should().Be("vm-991823");
        created[0].Handle.ProvisionerId.Should().Be("hetzner");
        created[0].Handle.Region.Should().Be("fsn1");
        created[0].Handle.Tags.Should().BeEquivalentTo(intent.Tags);

        created[0].JobId.Should().Be("job-1");
        created[0].RecordedAt.Should().Be(RecordedAt);
        created[0].ConfirmedAt.Should().Be(ObservedAt);
    }

    [Fact]
    public async Task ListCreated_NeverReturnsARowThatIsStillIntended()
    {
        using var fixture = new SqliteDatabaseFixture();

        var stillIntended = NewIntent();
        var promoted = NewIntent();
        var otherProvisioner = NewIntent() with { ProvisionerId = "digitalocean" };

        await using (var writeContext = fixture.CreateContext())
        {
            var ledger = new EfProvisioningLedger(writeContext);
            await ledger.RecordIntentAsync(stillIntended);
            await ledger.RecordIntentAsync(promoted);
            await ledger.RecordIntentAsync(otherProvisioner);
            await ledger.MarkCreatedAsync(promoted.LedgerRowId, "vm-1", ObservedAt);
        }

        await using var freshContext = fixture.CreateContext();
        var ledgerUnderTest = new EfProvisioningLedger(freshContext);

        var created = await ledgerUnderTest.ListCreatedAsync("hetzner");

        // An Intended row has no provider-assigned id by definition, so it must never appear in an
        // enumeration whose every element promises one. Listing it would be the exact confusion between
        // "may exist" and "does exist" the two states are there to keep apart.
        created.Should().ContainSingle();
        created[0].LedgerRowId.Should().Be(promoted.LedgerRowId);
        created.Should().NotContain(row => row.LedgerRowId == stillIntended.LedgerRowId);

        // …and the other enumeration is unchanged: it still returns exactly the unresolved row.
        var intended = await ledgerUnderTest.ListIntendedAsync("hetzner");
        intended.Should().ContainSingle();
        intended[0].LedgerRowId.Should().Be(stillIntended.LedgerRowId);

        // Another provisioner's rows belong to another sweep.
        (await ledgerUnderTest.ListCreatedAsync("digitalocean")).Should().BeEmpty();
    }

    [Fact]
    public async Task ListCreated_ReturnsTagsIntact_IncludingValuesContainingDelimiterCharacters()
    {
        using var fixture = new SqliteDatabaseFixture();

        // The same delimiter-collision set the intent path is guarded against, asserted on the handle a
        // caller would drive a drift check with: an orphan sweep that matches on shredded tags matches the
        // wrong resource, and this enumeration is what hands those tags to it.
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.note"] = "region:fsn1,role:primary",
            ["servyx.url:endpoint"] = "https://example.invalid:8443/path,with,commas",
            ["servyx.empty"] = string.Empty,
            ["servyx.managed"] = "true",
        };

        var intent = NewIntent() with { Tags = tags };

        await using (var writeContext = fixture.CreateContext())
        {
            var ledger = new EfProvisioningLedger(writeContext);
            await ledger.RecordIntentAsync(intent);
            await ledger.MarkCreatedAsync(intent.LedgerRowId, "vm-tagged", ObservedAt);
        }

        await using var freshContext = fixture.CreateContext();
        var created = await new EfProvisioningLedger(freshContext).ListCreatedAsync("hetzner");

        created.Should().ContainSingle();
        created[0].Handle.Tags.Should().BeEquivalentTo(tags);
        created[0].Handle.ProviderResourceId.Should().Be("vm-tagged");
    }

    [Fact]
    public async Task ListCreated_RejectsABlankProvisionerId_RatherThanSweepingEverything()
    {
        using var fixture = new SqliteDatabaseFixture();

        await using var context = fixture.CreateContext();
        var ledger = new EfProvisioningLedger(context);

        var act = async () => await ledger.ListCreatedAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Tags_RoundTripIntact_IncludingValuesContainingDelimiterCharacters()
    {
        using var fixture = new SqliteDatabaseFixture();

        // Tags are stored as JSON (see JsonCollectionConverters), which is the whole reason these survive.
        // A "key:value,key:value" encoding — the obvious cheap alternative — would shred every one of these:
        // the first pair hides a comma and a colon in its value, the second hides a colon in its key, and the
        // third is an empty value that a naive split would drop entirely.
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.note"] = "region:fsn1,role:primary",
            ["servyx.url:endpoint"] = "https://example.invalid:8443/path,with,commas",
            ["servyx.empty"] = string.Empty,
            ["servyx.managed"] = "true",
        };

        var intent = NewIntent() with { Tags = tags };

        await using (var writeContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(writeContext).RecordIntentAsync(intent);
        }

        await using var freshContext = fixture.CreateContext();

        (await freshContext.ProvisionedResources.SingleAsync()).Tags.Should().BeEquivalentTo(tags);

        var intended = await new EfProvisioningLedger(freshContext).ListIntendedAsync("hetzner");
        intended.Should().ContainSingle();
        intended[0].Tags.Should().BeEquivalentTo(tags);
    }

    [Fact]
    public async Task RecordedIntent_IgnoresLaterMutationOfTheCallersOwnTagDictionary()
    {
        using var fixture = new SqliteDatabaseFixture();

        var callerTags = new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" };
        var intent = NewIntent() with { Tags = callerTags };

        await using (var writeContext = fixture.CreateContext())
        {
            await new EfProvisioningLedger(writeContext).RecordIntentAsync(intent);
        }

        // The ledger records what was about to be applied. A caller reusing its dictionary afterwards must
        // not be able to rewrite history about what the provider was asked for.
        callerTags["servyx.managed"] = "false";

        await using var freshContext = fixture.CreateContext();
        (await freshContext.ProvisionedResources.SingleAsync()).Tags["servyx.managed"].Should().Be("true");
    }

    private static ProvisioningIntent NewIntent() => new(
        LedgerRowId: Guid.NewGuid(),
        ProvisionerId: "hetzner",
        Region: "fsn1",
        Tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" },
        JobId: "job-1",
        RecordedAt: RecordedAt);
}
