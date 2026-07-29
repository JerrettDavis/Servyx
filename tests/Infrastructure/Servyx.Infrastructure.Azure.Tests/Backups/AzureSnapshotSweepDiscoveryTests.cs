using System.Net;

using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.Azure.Backups;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.Azure.Tests.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Backups;

/// <summary>
/// The property an Azure snapshot has that an EBS snapshot and a DigitalOcean snapshot do not: it is an
/// ordinary ARM resource, so the orphan sweep the provisioner already performs finds it without knowing that
/// backups exist.
/// </summary>
/// <remarks>
/// <para>
/// This matters because the failure mode the backup provider is most likely to leave behind is a snapshot
/// Servyx wrote and then lost track of — a create that was interrupted between two of its per-disk writes, or
/// a set whose ownership could not be verified. On AWS such a snapshot is only findable by someone who knows to
/// go looking at snapshots specifically. Here it turns up in the same subscription-wide
/// <c>servyx.managed=true</c> listing as an orphaned public IP address.
/// </para>
/// <para>
/// Asserted from the provisioner rather than claimed in a comment, because the claim is about a code path in a
/// <em>different</em> file that this change did not touch — and a claim of that shape is exactly the kind that
/// quietly stops being true.
/// </para>
/// </remarks>
public sealed class AzureSnapshotSweepDiscoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, string> SnapshotTags() =>
        AzureSnapshotOwnership.BuildTags(
            AzureScenario.InstanceId,
            AzureScenario.ResourceGroup,
            AzureScenario.VmName,
            AzureScenario.JobId,
            AzureScenario.ConnectorId,
            AzureSnapshotOwnership.FormatSetName(AzureScenario.InstanceId, Now),
            AzureSnapshotScenario.OsDiskName);

    private static string SnapshotResourceRow()
    {
        var name = AzureSnapshotOwnership.FormatMemberName(
            AzureSnapshotOwnership.FormatSetName(AzureScenario.InstanceId, Now),
            0);

        return AzureScenario.ResourceSummaryJson(
            AzureScenario.ResourceGroupId + "/providers/Microsoft.Compute/snapshots/" + name,
            "Microsoft.Compute/snapshots",
            name,
            tags: SnapshotTags());
    }

    [Fact]
    public void A_servyx_snapshots_tags_satisfy_the_exact_predicate_the_sweep_filters_on()
    {
        var tags = SnapshotTags();

        ServyxAzureTags.IsManaged(tags).Should().BeTrue();
        ServyxAzureTags.ManagedFilter.Should().Be("tagName eq 'servyx.managed' and tagValue eq 'true'");
        tags[ServyxTagKeys.Managed].Should().Be(ServyxTagKeys.ManagedValue);
    }

    [Fact]
    public async Task An_orphaned_servyx_snapshot_is_returned_by_the_provisioners_existing_orphan_sweep()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(null, SnapshotResourceRow()));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        handles.Should().ContainSingle().Which.ProviderResourceId.Should().Contain(
            "/providers/Microsoft.Compute/snapshots/",
            "a snapshot Servyx wrote and lost track of is a billable ARM resource sitting in a resource group, and "
            + "the sweep that already finds an orphaned public IP finds it too — without knowing backups exist");
    }

    [Fact]
    public async Task An_orphaned_snapshot_is_swept_alongside_the_hosts_other_resources_not_instead_of_them()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(
                    null,
                    [.. AzureScenario.SweptHostResources(), SnapshotResourceRow()]));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        handles.Should().HaveCount(5);
        handles.Should().Contain(h => h.ProviderResourceId.Contains(
            "/providers/Microsoft.Compute/snapshots/",
            StringComparison.Ordinal));
        handles.Should().OnlyContain(h => h.Tags[ServyxTagKeys.InstanceId] == AzureScenario.InstanceId);
    }

    [Fact]
    public async Task A_snapshot_servyx_did_not_create_is_invisible_to_the_sweep_as_it_must_be()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(
                    null,
                    AzureScenario.ResourceSummaryJson(
                        AzureScenario.ResourceGroupId + "/providers/Microsoft.Compute/snapshots/hand-taken",
                        "Microsoft.Compute/snapshots",
                        "hand-taken",
                        tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "ops" })));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        handles.Should().BeEmpty(
            "a sweep's output is a delete list, and a snapshot Servyx did not create must never appear on one");
    }
}
