using NSubstitute;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;

namespace Servyx.Application.Tests.Provisioning;

/// <summary>
/// The ledger's two enumerations as the dashboard projects them: unresolved write-ahead intents, and the
/// rows the provider confirmed — the latter carrying the provider-assigned id that makes a live resource
/// addressable at all.
/// </summary>
/// <remarks>
/// The ledger here is the real <see cref="InMemoryProvisioningLedger"/> driven through its own write path,
/// not a substitute returning canned lists. A test that stubbed the enumeration could not tell whether a
/// <c>Created</c> row genuinely stops being <c>Intended</c>, which is the transition every assertion below
/// depends on.
/// </remarks>
public class ProvisioningLedgerEnumerationTests
{
    private const string ProvisionerId = "docker-container";
    private const string OtherProvisionerId = "hetzner";

    private static readonly DateTimeOffset RecordedAt = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 5, 4, 3, 2, 43, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> Tags =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = "srv-001",
        };

    private static IProvisioner Provisioner(string provisionerId = ProvisionerId)
    {
        var provisioner = Substitute.For<IProvisioner>();
        provisioner.ProvisionerId.Returns(provisionerId);
        provisioner.Capabilities.Returns(ProvisioningCapabilities.Create);
        return provisioner;
    }

    private static ProvisioningIntent Intent(Guid rowId, string provisionerId = ProvisionerId) => new(
        LedgerRowId: rowId,
        ProvisionerId: provisionerId,
        Region: "fsn1",
        Tags: Tags,
        JobId: "job-1",
        RecordedAt: RecordedAt);

    [Fact]
    public async Task A_confirmed_row_is_listed_with_the_provider_assigned_id_it_was_stamped_with()
    {
        var ledger = new InMemoryProvisioningLedger();
        var rowId = Guid.NewGuid();

        await ledger.RecordIntentAsync(Intent(rowId));
        await ledger.MarkCreatedAsync(rowId, "c0ffee1234ab", ConfirmedAt);

        var created = await ledger.ListCreatedAsync(ProvisionerId);

        created.Should().ContainSingle();
        created[0].LedgerRowId.Should().Be(rowId);
        created[0].Handle.ProviderResourceId.Should().Be("c0ffee1234ab");
        created[0].Handle.ProvisionerId.Should().Be(ProvisionerId);
        created[0].Handle.Region.Should().Be("fsn1");
        created[0].Handle.Tags.Should().BeEquivalentTo(Tags);
        created[0].JobId.Should().Be("job-1");
        created[0].RecordedAt.Should().Be(RecordedAt);
        created[0].ConfirmedAt.Should().Be(ConfirmedAt);
    }

    [Fact]
    public async Task An_unresolved_intent_is_never_listed_as_a_confirmed_resource()
    {
        var ledger = new InMemoryProvisioningLedger();
        var unresolved = Guid.NewGuid();
        var confirmed = Guid.NewGuid();

        await ledger.RecordIntentAsync(Intent(unresolved));
        await ledger.RecordIntentAsync(Intent(confirmed));
        await ledger.MarkCreatedAsync(confirmed, "c0ffee1234ab", ConfirmedAt);

        // The two enumerations partition the rows; neither may claim the other's.
        (await ledger.ListCreatedAsync(ProvisionerId)).Should().ContainSingle()
            .Which.LedgerRowId.Should().Be(confirmed);
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().ContainSingle()
            .Which.LedgerRowId.Should().Be(unresolved);
    }

    [Fact]
    public async Task Another_provisioners_confirmed_rows_are_not_returned()
    {
        var ledger = new InMemoryProvisioningLedger();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await ledger.RecordIntentAsync(Intent(mine));
        await ledger.RecordIntentAsync(Intent(theirs, OtherProvisionerId));
        await ledger.MarkCreatedAsync(mine, "c0ffee1234ab", ConfirmedAt);
        await ledger.MarkCreatedAsync(theirs, "vm-991823", ConfirmedAt);

        var created = await ledger.ListCreatedAsync(ProvisionerId);

        created.Should().ContainSingle();
        created[0].Handle.ProviderResourceId.Should().Be("c0ffee1234ab");
    }

    [Fact]
    public async Task The_dashboard_surfaces_a_confirmed_row_with_its_handle_and_its_state()
    {
        var ledger = new InMemoryProvisioningLedger();
        var rowId = Guid.NewGuid();

        await ledger.RecordIntentAsync(Intent(rowId));
        await ledger.MarkCreatedAsync(rowId, "c0ffee1234ab", ConfirmedAt);

        var dashboard = new ProvisioningDashboardService([Provisioner()], ledger);

        var entries = await dashboard.ListLedgerEntriesAsync();

        entries.Should().ContainSingle();
        entries[0].State.Should().Be(ResourceLifecycleState.Created);

        // The correspondence the UI branches on: a Created row carries a handle, and it names the resource
        // by the id the provider assigned rather than by anything derivable from the row's tags.
        entries[0].Handle.Should().NotBeNull();
        entries[0].Handle!.ProviderResourceId.Should().Be("c0ffee1234ab");
        entries[0].Handle!.Region.Should().Be("fsn1");
        entries[0].Intent.LedgerRowId.Should().Be(rowId);
        entries[0].Intent.JobId.Should().Be("job-1");
        entries[0].Intent.RecordedAt.Should().Be(RecordedAt);
    }

    [Fact]
    public async Task The_dashboard_surfaces_an_unresolved_intent_with_no_handle_at_all()
    {
        var ledger = new InMemoryProvisioningLedger();
        var rowId = Guid.NewGuid();

        await ledger.RecordIntentAsync(Intent(rowId));

        var entries = await new ProvisioningDashboardService([Provisioner()], ledger).ListLedgerEntriesAsync();

        entries.Should().ContainSingle();
        entries[0].State.Should().Be(ResourceLifecycleState.Intended);

        // No handle is fabricated from the row's servyx.instance-id tag, even though it carries one: the
        // provider was never contacted for this row, so nothing it names is known to exist.
        entries[0].Handle.Should().BeNull();
        entries[0].Intent.Tags.Should().ContainKey("servyx.instance-id");
    }

    [Fact]
    public async Task Both_states_are_listed_together_so_neither_can_be_rendered_without_the_other()
    {
        var ledger = new InMemoryProvisioningLedger();
        var unresolved = Guid.NewGuid();
        var confirmed = Guid.NewGuid();

        await ledger.RecordIntentAsync(Intent(unresolved));
        await ledger.RecordIntentAsync(Intent(confirmed));
        await ledger.MarkCreatedAsync(confirmed, "c0ffee1234ab", ConfirmedAt);

        var entries = await new ProvisioningDashboardService([Provisioner()], ledger).ListLedgerEntriesAsync();

        entries.Should().HaveCount(2);
        entries.Should().ContainSingle(e => e.State == ResourceLifecycleState.Intended && e.Handle == null);
        entries.Should().ContainSingle(e => e.State == ResourceLifecycleState.Created && e.Handle != null);
    }

    [Fact]
    public async Task A_dashboard_with_no_ledger_reports_nothing_and_says_so_separately()
    {
        var dashboard = new ProvisioningDashboardService([Provisioner()]);

        dashboard.LedgerConfigured.Should().BeFalse(
            "'no rows' and 'nothing is recording rows' are different answers");
        (await dashboard.ListLedgerEntriesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Blank_provisioner_ids_are_rejected_by_both_enumerations()
    {
        var ledger = new InMemoryProvisioningLedger();

        var blankCreated = async () => await ledger.ListCreatedAsync("  ");
        var blankIntended = async () => await ledger.ListIntendedAsync("  ");

        await blankCreated.Should().ThrowAsync<ArgumentException>();
        await blankIntended.Should().ThrowAsync<ArgumentException>();
    }
}
