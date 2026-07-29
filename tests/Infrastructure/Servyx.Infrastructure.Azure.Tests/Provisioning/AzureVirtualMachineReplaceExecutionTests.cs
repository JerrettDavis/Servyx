using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The <see cref="IDestructiveUpdateApplier"/> half of the VM adapter: the one operation in this assembly that
/// deletes a customer's data on purpose, the many shapes it refuses, and — the assertions the rest of this file
/// exists to protect — that every refusal sends <em>nothing</em>, that a submitted replace is never mistaken
/// for a finished one, and that a create which fails after the delete succeeded says so in those words.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted ARM and token service, so no network access, no Azure
/// subscription, no service principal beyond the fake one in the scenario, and no real virtual machine is ever
/// deleted. The refusal tests assert <c>scenario.Api.Requests</c> is <em>empty</em> — a genuine request count
/// of zero, not merely "an error came back" — because an error return with a delete already in flight is
/// exactly the failure these gates exist to rule out, and only the count can tell the two apart. They
/// additionally install <see cref="FailOnAnyRequest"/>, so a request would fail the test at the point it was
/// issued even if the count assertion were deleted. Note what "zero" covers here that it does not for
/// DigitalOcean: the token exchange is a request too, so a refused replace is proved not even to have bought a
/// credential.
/// </para>
/// <para>
/// The acknowledgement checked here is a <see cref="DataImpact"/> rather than Servyx.Application's
/// <c>DataImpactAcknowledgement</c> token, because that token lives in the Application layer and this
/// assembly's subject references only <c>Servyx.Domain</c>. The token half of the same gate — that only
/// <c>Destroyed()</c> produces the value these tests pass, and that no plan can derive it — is asserted in
/// <c>Servyx.Application.Tests</c>.
/// </para>
/// </remarks>
public class AzureVirtualMachineReplaceExecutionTests
{
    /// <summary>The image these tests ask the machine to be replaced onto. Not the image it is running.</summary>
    private const string TargetImage = "Debian:debian-12:12:latest";

    /// <summary>The size these tests hold constant, so a planned replacement carries exactly one change.</summary>
    private const string LiveSize = AzureScenario.VmSize;

    /// <summary>The absolute URL the substituted ARM hands back in <c>Azure-AsyncOperation</c> for the delete.</summary>
    private const string DeleteOperationUri =
        "https://management.azure.com/subscriptions/" + AzureScenario.SubscriptionId
        + "/providers/Microsoft.Compute/locations/" + AzureScenario.Region
        + "/operations/delete-11111111-aaaa?api-version=2024-07-01";

    /// <summary>The absolute URL the substituted ARM hands back in <c>Azure-AsyncOperation</c> for the create.</summary>
    private const string CreateOperationUri =
        "https://management.azure.com/subscriptions/" + AzureScenario.SubscriptionId
        + "/providers/Microsoft.Compute/locations/" + AzureScenario.Region
        + "/operations/create-22222222-bbbb?api-version=2024-07-01";

    // -------------------------------------------------------------------------------------------------
    // The adapter is a destructive update applier at all, and says so honestly
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_a_destructive_update_applier_and_the_two_ids_agree()
    {
        var provisioner = new AzureScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IDestructiveUpdateApplier>();
        ((IDestructiveUpdateApplier)provisioner).ProvisionerId.Should().Be(AzureVirtualMachineProvisioner.Id);
    }

    [Fact]
    public void The_two_execution_entry_points_are_separate_members_that_cannot_reach_each_other()
    {
        // The structural claim, stated as a type test: an update and a replacement are different interface
        // members with different signatures, and the only way to reach the destructive one is to call it and
        // hand it an acknowledgement. There is no argument to ApplyUpdateAsync that could arrive here.
        typeof(IUpdateApplier).GetMethod(nameof(IUpdateApplier.ApplyUpdateAsync))!
            .GetParameters().Should().NotContain(p => p.ParameterType == typeof(DataImpact?));

        typeof(IDestructiveUpdateApplier).GetMethod(nameof(IDestructiveUpdateApplier.ApplyDestructiveUpdateAsync))!
            .GetParameters().Should().Contain(p => p.ParameterType == typeof(DataImpact?));
    }

    // -------------------------------------------------------------------------------------------------
    // The acknowledgement gate. Every one of these asserts a request count of zero.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_replace_with_no_acknowledgement_at_all_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, acknowledgedDataImpact: null);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the acknowledgement supplied was none");

        scenario.Api.Requests.Should().BeEmpty("a replacement nobody acknowledged is never sent");
    }

    [Fact]
    public async Task A_replace_acknowledged_only_as_at_risk_is_refused_and_issues_no_http_request()
    {
        // Acknowledging that data might be separated from the workload is not acknowledging that it will be
        // deleted. The two are different approvals and the milder one authorises nothing here.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.AtRisk);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the acknowledgement supplied was AtRisk");

        scenario.Api.Requests.Should().BeEmpty("an AtRisk approval never authorises deleting a machine's disk");
    }

    [Fact]
    public async Task A_replace_acknowledged_as_preserved_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Preserved);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the acknowledgement supplied was Preserved");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_acknowledgement_is_checked_before_anything_at_all_has_been_read()
    {
        // The strongest form of the same claim: this scenario has issued no request ever, so its empty request
        // list cannot be an artefact of anything having been cleared after the fact - and it never even bought
        // a credential.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(),
            HandBuiltReplacePlan(),
            approvedPlanHash: ReplacePlanHash,
            acknowledgedDataImpact: null);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty("a refusal does not even buy a credential");
    }

    // -------------------------------------------------------------------------------------------------
    // Every other refusal. Also a request count of zero, every time.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_issues_no_http_request_even_with_a_matching_token()
    {
        // The acknowledgement is not a force flag: a correct token does not make a stale plan runnable.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(),
            plan,
            approvedPlanHash: "0000000000000000000000000000000000000000000000000000000000000000",
            DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not the plan that was approved");

        scenario.Api.Requests.Should().BeEmpty("a stale plan is refused, never executed");
    }

    [Fact]
    public async Task A_region_change_is_refused_and_issues_no_http_request()
    {
        // An ARM resource's location is immutable. Its plan is Destroyed and Recreate, so it reaches this
        // entry point - and is refused here, because replacing the machine at this id moves nothing.
        var (scenario, provisioner, plan) = await PlannedAsync(
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "westus2" });

        plan.DataImpact.Should().Be(DataImpact.Destroyed);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("location is immutable");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resource_group_change_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(
            overrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceGroup"] = "rg-servyx-somewhere-else",
            });

        plan.DataImpact.Should().Be(DataImpact.Destroyed);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("part of a resource's ARM id");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replace_bundled_with_a_resize_is_refused_rather_than_partly_applied()
    {
        // Executing the half it understands would report a half-applied update as an applied one - and the
        // half it understands is the irreversible one.
        var (scenario, provisioner, plan) = await PlannedAsync(
            size: "Standard_D2s_v5",
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = TargetImage });

        plan.Changes.Should().HaveCount(2);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("replacement and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_whose_impact_is_not_destroyed_is_refused_and_issues_no_http_request()
    {
        // A plan claiming something milder than what a replacement actually does is a reason to stop.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltReplacePlan(dataImpact: DataImpact.AtRisk);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("only a plan that states Destroyed");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_replace_entry_point_cannot_resize_and_issues_no_http_request()
    {
        // The mirror of the existing "an image change plan is refused by the resize path" assertion. A size
        // change handed to this member - even labelled Destroyed and fully acknowledged - is refused, so the
        // two entry points cannot stand in for one another.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltReplacePlan(
            changes: [new PlannedChange("size", LiveSize, "Standard_D2s_v5", RequiresRecreate: false)]);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("replacement and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_does_not_describe_itself_as_a_recreate_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        // An image change that claims not to need a recreate - the shape a plan would have if someone had
        // decided a reimage were an in-place edit. The domain will not let such a change coexist with
        // UpdateStrategy.InPlace unless RequiresRecreate is false, so it is spelled out here.
        var plan = HandBuiltReplacePlan(
            changes: [new PlannedChange("image", AzureScenario.ImageUrn, TargetImage, RequiresRecreate: false)],
            strategy: UpdateStrategy.InPlace);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the plan's strategy is InPlace");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltReplacePlan(provisionerId: "digitalocean-droplet");

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("digitalocean-droplet");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_that_is_not_a_virtual_machine_id_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        // A real ARM id, and a real Servyx-owned resource - just not a machine. Deleting "whatever answers to
        // that id" is exactly what this guard exists to prevent.
        var plan = HandBuiltReplacePlan();

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(resourceId: AzureScenario.NicId), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("is not the ARM id of a");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_change_naming_no_target_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltReplacePlan(
            changes: [new PlannedChange("image", AzureScenario.ImageUrn, null, RequiresRecreate: true)]);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("names no target image");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_change_naming_a_malformed_urn_is_refused_before_anything_is_deleted()
    {
        // Discovering a bad URN after the delete would mean discovering it with the machine already gone, so
        // it is parsed while the machine is still there - and here, before Azure has been spoken to at all.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltReplacePlan(
            changes: [new PlannedChange("image", AzureScenario.ImageUrn, "ubuntu-24-04-x64", RequiresRecreate: true)]);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("four-part Azure image URN");

        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals that need the live machine read first. Nothing is deleted by any of them.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_machine_azure_no_longer_has_is_refused_and_nothing_is_deleted_or_created()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        scenario.RouteMissingVirtualMachine();

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("no longer has a machine at");

        Deletes(scenario).Should().Be(0);
        Creates(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_machine_azure_reports_no_ssh_key_for_is_refused_before_anything_is_deleted()
    {
        // A replacement created without an authorised key would be a machine nobody can log in to, because
        // this adapter disables password authentication. Refused while the machine is still there.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(sshPublicKey: null));

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var refused = result.Should().BeOfType<UpdateExecutionResult.Refused>().Which;
        refused.Message.Should().Contain("authorised SSH public key");
        refused.Message.Should().Contain("Nothing was deleted and nothing was created");

        Deletes(scenario).Should().Be(0);
        Creates(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_machine_azure_reports_no_network_interface_for_is_refused_before_anything_is_deleted()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(nicId: null));

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("any network interface");

        Deletes(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_machine_azure_reports_no_disk_tier_for_is_refused_before_anything_is_deleted()
    {
        // Guessing the tier would silently change the machine's storage performance under an operator who
        // approved an image change and nothing else.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(osDiskStorageAccountType: null));

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("OS disk tier");

        Deletes(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_machine_that_does_not_carry_the_servyx_management_tag_is_refused()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();

        var strangersTags = new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "someone-else" };
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(tags: strangersTags));

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("does not carry the Servyx management tag");

        Deletes(scenario).Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------------
    // The operation itself: one delete, one create, in that order, at the same ARM id
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_replace_deletes_the_machine_and_creates_it_again_from_the_new_image()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteReplace(scenario, ["Succeeded"], ["Succeeded"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        var deletes = scenario.Api.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        deletes.Should().ContainSingle("a replacement deletes one machine, once");
        deletes[0].Uri.AbsolutePath.Should().Be(AzureScenario.VmId);

        var creates = scenario.Api.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        creates.Should().ContainSingle("a replacement creates one machine, once");
        creates[0].Uri.AbsolutePath.Should().Be(AzureScenario.VmId, "the replacement takes the same ARM id");

        // The delete really did come first: a create-then-delete would have destroyed the replacement.
        scenario.Api.Requests.IndexOf(deletes[0])
            .Should().BeLessThan(scenario.Api.Requests.IndexOf(creates[0]));

        var body = creates[0].Body;
        body.Should().NotBeNull();
        body.Should().Contain("\"publisher\":\"Debian\"");
        body.Should().Contain("\"offer\":\"debian-12\"");
        body.Should().Contain("\"sku\":\"12\"");
        body.Should().Contain("\"version\":\"latest\"");

        // Everything else is the machine that was there: same size, same NIC, same key, same disk tier, and
        // the cascade that keeps the disk sweepable is re-declared rather than inherited.
        body.Should().Contain("\"vmSize\":\"" + LiveSize + "\"");
        body.Should().Contain("\"id\":\"" + AzureScenario.NicId + "\"");
        body.Should().Contain("\"keyData\":\"" + AzureScenario.SshPublicKey + "\"");
        body.Should().Contain("\"storageAccountType\":\"" + AzureScenario.OsDiskStorageAccountType + "\"");
        body.Should().Contain("\"deleteOption\":\"Delete\"");
        body.Should().Contain("\"disablePasswordAuthentication\":true");
        body.Should().Contain("\"servyx.instance-id\":\"" + AzureScenario.InstanceId + "\"");

        // ARM never returns customData, so nothing was invented to put back.
        body.Should().NotContain("customData");
    }

    [Fact]
    public async Task A_replace_never_deletes_the_network_interface_the_public_address_or_the_network()
    {
        // The one thing that survives a replacement is the host's address, and it survives because neither
        // call names the resources that hold it.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteReplace(scenario, ["Succeeded"], ["Succeeded"]);

        await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var mutations = scenario.Api.Requests
            .Where(r => r.Method == HttpMethod.Delete || r.Method == HttpMethod.Put || r.Method == HttpMethod.Patch)
            .ToList();

        mutations.Should().NotBeEmpty();
        mutations.Should().OnlyContain(r => r.Uri.AbsolutePath == AzureScenario.VmId);

        scenario.Api.Requests.Should().NotContain(r =>
            r.Method == HttpMethod.Delete
            && (r.Uri.AbsolutePath == AzureScenario.NicId
                || r.Uri.AbsolutePath == AzureScenario.PublicIpId
                || r.Uri.AbsolutePath == AzureScenario.VirtualNetworkId));
    }

    [Fact]
    public async Task A_resize_is_still_structurally_unable_to_name_an_image()
    {
        // The mirror of the test above, taken through the other entry point. A resize is a PATCH whose body
        // type has no member that could carry an image, and the suite-wide check in AzureArmApiDouble would
        // fail this test at the request if it ever did.
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();
        var provisioner = scenario.Provisioner();

        var plan = await provisioner.PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(size: "Standard_D2s_v5"));

        plan.Should().NotBeNull();
        plan!.DataImpact.Should().Be(DataImpact.Preserved);
        scenario.Api.Requests.Clear();

        RouteResizeOnly(scenario);
        await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        var bodies = scenario.Api.Requests.Where(r => r.Body is not null).Select(r => r.Body!).ToList();
        bodies.Should().NotBeEmpty();
        bodies.Should().OnlyContain(b => !b.Contains("imageReference", StringComparison.OrdinalIgnoreCase));
        bodies.Should().OnlyContain(b => !b.Contains("storageProfile", StringComparison.OrdinalIgnoreCase));

        // And it never deleted anything: a resize mutates the machine that exists.
        scenario.Api.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
        scenario.Api.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    // -------------------------------------------------------------------------------------------------
    // Submission is not success: the ends each of the two operations can reach, kept apart
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_delete_still_running_when_the_polls_are_spent_is_a_timeout_and_creates_nothing()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync(pollAttempts: 3);
        RouteReplace(scenario, ["InProgress"], ["Succeeded"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().NotBeOfType<UpdateExecutionResult.Completed>();
        result.Should().NotBeOfType<UpdateExecutionResult.Failed>();

        var timedOut = result.Should().BeOfType<UpdateExecutionResult.TimedOut>().Which;
        timedOut.Message.Should().Contain("NOT confirmed");
        timedOut.Message.Should().Contain("NOT reported as failed");
        timedOut.Message.Should().Contain("do NOT resubmit");
        timedOut.Message.Should().Contain("No replacement has been created");

        // The polls were really made, and really stopped where they were told to.
        OperationReads(scenario, DeleteOperationUri).Should().Be(3);

        // And no machine was created against a delete that was never seen to finish.
        Creates(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_create_still_running_when_the_polls_are_spent_is_a_timeout_not_a_failure()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync(pollAttempts: 3);
        RouteReplace(scenario, ["Succeeded"], ["InProgress"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var timedOut = result.Should().BeOfType<UpdateExecutionResult.TimedOut>().Which;

        // The machine is gone and the message leads with that, not with the timeout.
        timedOut.Message.Should().Contain("HAS BEEN DELETED");
        timedOut.Message.Should().Contain("NOT confirmed");
        timedOut.Message.Should().Contain("NOT reported as failed");
        timedOut.Message.Should().Contain("do NOT resubmit");
        timedOut.Message.Should().Contain("address is unchanged");

        OperationReads(scenario, CreateOperationUri).Should().Be(3);
    }

    [Fact]
    public async Task A_still_running_replacement_and_a_failed_one_are_different_types_with_opposite_instructions()
    {
        var (runningScenario, runningProvisioner, runningPlan) = await PlannedReplaceAsync();
        RouteReplace(runningScenario, ["Succeeded"], ["InProgress"]);
        var running = await runningProvisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), runningPlan, runningPlan.PlanHash, DataImpact.Destroyed);

        var (failedScenario, failedProvisioner, failedPlan) = await PlannedReplaceAsync();
        RouteReplace(failedScenario, ["Succeeded"], ["Failed"]);
        var failed = await failedProvisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), failedPlan, failedPlan.PlanHash, DataImpact.Destroyed);

        running.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        failed.Should().BeOfType<UpdateExecutionResult.Failed>();
        running.GetType().Should().NotBe(failed.GetType());

        // The two messages tell an operator to do opposite things, which is the point of the distinction:
        // one says wait, the other says the create is over and a retry is the way forward.
        running.Message.Should().Contain("do NOT resubmit");
        running.Message.Should().Contain("may yet succeed");
        failed.Message.Should().Contain("was then NOT created");
        failed.Message.Should().Contain("a retry can create a machine at the same id");
        failed.Message.Should().NotContain("may yet succeed");
    }

    [Fact]
    public async Task A_delete_azure_reports_as_failed_leaves_the_machine_alone_and_creates_nothing()
    {
        const string ArmMessage = "Cannot delete the virtual machine because it has a lock";

        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteReplace(scenario, ["Failed"], ["Succeeded"], errorMessage: ArmMessage);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;
        failed.Message.Should().Contain(ArmMessage);
        failed.Message.Should().Contain("No replacement was created");

        Creates(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_delete_azure_refuses_outright_changes_nothing_at_all()
    {
        const string ArmMessage = "The resource is protected by a CanNotDelete lock";

        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteDeleteRefused(scenario, ArmMessage);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;
        failed.Message.Should().Contain(ArmMessage);
        failed.Message.Should().Contain("was NOT deleted");
        failed.Message.Should().Contain("Nothing about the host has changed");

        Creates(scenario).Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------------
    // The window: a create that fails after the delete succeeded
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_that_fails_after_a_successful_delete_says_the_machine_is_gone_and_unreplaced()
    {
        const string ArmMessage = "The requested VM size Standard_B2s is not available in zone 1";

        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteReplace(scenario, ["Succeeded"], ["Failed"], errorMessage: ArmMessage);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;

        // Azure's own words, and then the state of the world in plain terms.
        failed.Message.Should().Contain(ArmMessage);
        failed.Message.Should().Contain("HAS BEEN DELETED");
        failed.Message.Should().Contain("cannot be recovered");
        failed.Message.Should().Contain("there is no machine at that id now");
        failed.Message.Should().Contain("the delete happened and the create did not");

        // And the surviving resources are named, because they are what a retry is built on - and what is
        // still billing in the meantime.
        failed.Message.Should().Contain("network interface and the public IP address");
        failed.Message.Should().Contain("still billing");

        // It is not a timeout: nothing is still running.
        result.Should().NotBeOfType<UpdateExecutionResult.TimedOut>();
    }

    [Fact]
    public async Task After_a_create_fails_the_ledgers_handle_still_names_a_machine_that_is_honestly_reported_gone()
    {
        // The ledger truth claim, asserted through the read paths a sweep actually uses. The recorded handle
        // is unchanged by a replacement - the replacement would have taken the same ARM id - so what matters
        // is that Servyx does not claim a machine exists at that id when none does.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteReplace(scenario, ["Succeeded"], ["Failed"], errorMessage: "quota exceeded");

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        // A failure carries no resource, so nothing downstream is handed a machine that is not there.
        result.Should().BeOfType<UpdateExecutionResult.Failed>();
        result.Should().NotBeOfType<UpdateExecutionResult.Completed>();

        // And the world as Azure now describes it: the id 404s. Both read paths say so rather than matching.
        scenario.RouteMissingVirtualMachine();

        var refreshed = await provisioner.RefreshAsync(AzureScenario.RecordedHandle());
        refreshed.Should().BeNull("Servyx must not describe a machine that no longer exists");

        var drift = await provisioner.DetectDriftAsync(AzureScenario.RecordedHandle());
        drift.Divergences.Should().Contain(d => d.Aspect == "existence" && d.Found == null);
    }

    // -------------------------------------------------------------------------------------------------
    // The completed replacement
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_replacement_is_reported_only_after_both_operations_are_observed_terminal()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync(pollAttempts: 5);
        RouteReplace(scenario, ["InProgress", "Succeeded"], ["InProgress", "InProgress", "Succeeded"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;
        completed.Message.Should().Contain("delete as succeeded after 2 check(s)");
        completed.Message.Should().Contain("create as succeeded after 3 check(s)");

        // The successes came from observations, not from either submission.
        OperationReads(scenario, DeleteOperationUri).Should().Be(2);
        OperationReads(scenario, CreateOperationUri).Should().Be(3);
    }

    [Fact]
    public async Task A_completed_replacement_hands_back_the_machine_as_it_now_is_and_states_what_was_lost()
    {
        var (scenario, provisioner, plan) = await PlannedReplaceAsync();
        RouteReplace(scenario, ["Succeeded"], ["Succeeded"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;

        completed.Resource.Handle.ProviderResourceId.Should().Be(AzureScenario.VmId);
        completed.Resource.ConnectorId.Should().Be(AzureScenario.ConnectorId);
        completed.Resource.Facts.PublicAddress.Should().Be(AzureScenario.PublicIp, "the address survives a replace");

        completed.Message.Should().Contain(TargetImage);
        completed.Message.Should().Contain("every save file - is gone and cannot be recovered");
        completed.Message.Should().Contain("No snapshot was taken");
        completed.Message.Should().Contain("keeps the address it had");
        completed.Message.Should().Contain("cloud-init");
    }

    [Fact]
    public async Task A_replacement_azure_names_no_operation_for_is_confirmed_from_observed_state()
    {
        // ARM's other answers: a delete that finishes synchronously, and a create whose 201 carries a
        // provisioning state and no tracking header. "Accepted" is still not "done" in either case.
        var (scenario, provisioner, plan) = await PlannedReplaceAsync(pollAttempts: 4);
        RouteReplaceWithoutTrackers(scenario, ["Creating", "Succeeded"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Completed>()
            .Which.Message.Should().Contain("create as succeeded after 2 check(s)");
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>The hash carried by every hand-built plan below.</summary>
    private const string ReplacePlanHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Plans a real, lone replacement with the real planner — same size, different image.</summary>
    private static Task<(AzureScenario Scenario, AzureVirtualMachineProvisioner Provisioner, UpdatePlan Plan)>
        PlannedReplaceAsync(int pollAttempts = 3) =>
        PlannedAsync(
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = TargetImage },
            pollAttempts: pollAttempts);

    /// <summary>
    /// Builds a genuine <see cref="UpdatePlan"/> with the real planner, then clears the recorded requests so
    /// the execution assertions that follow count only what execution itself issued.
    /// </summary>
    private static async Task<(AzureScenario Scenario, AzureVirtualMachineProvisioner Provisioner, UpdatePlan Plan)>
        PlannedAsync(
            string size = LiveSize,
            IReadOnlyDictionary<string, string>? overrides = null,
            int pollAttempts = 3)
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var provisioner = scenario.Provisioner(pollAttempts: pollAttempts);

        var plan = await provisioner.PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(overrides, size));

        plan.Should().NotBeNull();
        scenario.Api.Requests.Clear();

        return (scenario, provisioner, plan!);
    }

    /// <summary>
    /// A plan built by hand, for the shapes the real planner cannot produce — a plan belonging to another
    /// provisioner, an image change naming no target, a Destroyed plan whose lone change is a resize.
    /// </summary>
    private static UpdatePlan HandBuiltReplacePlan(
        IReadOnlyList<PlannedChange>? changes = null,
        string provisionerId = AzureVirtualMachineProvisioner.Id,
        UpdateStrategy strategy = UpdateStrategy.Recreate,
        DataImpact dataImpact = DataImpact.Destroyed) =>
        new(
            planId: "test:update:1",
            planHash: ReplacePlanHash,
            provisionerId: provisionerId,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes
                ?? [new PlannedChange("image", AzureScenario.ImageUrn, TargetImage, RequiresRecreate: true)],
            stages: [new ProvisioningStage("delete-virtual-machine", provisionerId, "Delete the machine.")],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>Makes any HTTP request at all — to ARM or to the token service — fail the test where it happens.</summary>
    private static void FailOnAnyRequest(AzureScenario scenario) =>
        scenario.Api.Responder = request => throw new InvalidOperationException(
            $"A refused replacement issued a {request.Method} request to '{request.Uri}'. It must send nothing at all.");

    /// <summary>How many virtual-machine deletions were submitted.</summary>
    private static int Deletes(AzureScenario scenario) =>
        scenario.Api.Requests.Count(r => r.Method == HttpMethod.Delete);

    /// <summary>How many virtual-machine creations were submitted.</summary>
    private static int Creates(AzureScenario scenario) =>
        scenario.Api.Requests.Count(r => r.Method == HttpMethod.Put);

    /// <summary>How many times one of the two long-running operations was read.</summary>
    private static int OperationReads(AzureScenario scenario, string operationUri) =>
        scenario.Api.Requests.Count(r =>
            r.Uri.AbsolutePath == new Uri(operationUri, UriKind.Absolute).AbsolutePath);

    /// <summary>
    /// Routes the replacement exchange as ARM's asynchronous form: a GET of the machine, a DELETE answered
    /// <c>202</c> with an <c>Azure-AsyncOperation</c> header, operation reads walking
    /// <paramref name="deleteStatuses"/> (repeating the last one), a PUT answered <c>201</c> with a second
    /// operation, operation reads walking <paramref name="createStatuses"/>, and ordinary resource reads for
    /// the read-back.
    /// </summary>
    private static void RouteReplace(
        AzureScenario scenario,
        IReadOnlyList<string> deleteStatuses,
        IReadOnlyList<string> createStatuses,
        string? errorMessage = null,
        string imageAfter = TargetImage)
    {
        var deleteReads = 0;
        var createReads = 0;

        // The machine reports the image it is actually running: the old one until the replacement has been
        // written, the new one afterwards. A double that answered the new image from the start would be
        // describing a machine that needs no replacing, and the adapter would correctly refuse to replace it.
        var created = false;

        scenario.Api.Responder = request =>
        {
            var token = AzureScenario.RouteTokenExchange(request);
            if (token is not null)
            {
                return token;
            }

            if (request.Method == HttpMethod.Delete)
            {
                return Accepted(DeleteOperationUri);
            }

            if (request.Method == HttpMethod.Put)
            {
                created = true;
                var response = AzureArmApiDouble.Json(
                    HttpStatusCode.Created,
                    "{\"properties\":{\"provisioningState\":\"Creating\"}}");
                response.Headers.Add("Azure-AsyncOperation", CreateOperationUri);
                return response;
            }

            if (request.Uri.AbsolutePath.Contains("/operations/delete-", StringComparison.Ordinal))
            {
                var status = deleteStatuses[Math.Min(deleteReads, deleteStatuses.Count - 1)];
                deleteReads++;
                return AzureArmApiDouble.Json(HttpStatusCode.OK, OperationStatusJson(status, errorMessage));
            }

            if (request.Uri.AbsolutePath.Contains("/operations/create-", StringComparison.Ordinal))
            {
                var status = createStatuses[Math.Min(createReads, createStatuses.Count - 1)];
                createReads++;
                return AzureArmApiDouble.Json(HttpStatusCode.OK, OperationStatusJson(status, errorMessage));
            }

            return request.Method == HttpMethod.Get
                ? AzureArmApiDouble.Json(
                    HttpStatusCode.OK,
                    ReadPayload(request, created ? imageAfter : AzureScenario.ImageUrn))
                : throw new InvalidOperationException(
                    $"The replace path issued an unexpected {request.Method} request to '{request.Uri}'.");
        };
    }

    /// <summary>
    /// Routes the replacement exchange as ARM's synchronous-looking form: a <c>200</c> delete that is already
    /// over, and a create whose <c>201</c> carries a provisioning state and <em>no</em> tracking header, so the
    /// only evidence available is the machine's own state, walked through <paramref name="provisioningStates"/>.
    /// </summary>
    private static void RouteReplaceWithoutTrackers(AzureScenario scenario, IReadOnlyList<string> provisioningStates)
    {
        var reads = 0;
        var created = false;

        scenario.Api.Responder = request =>
        {
            var token = AzureScenario.RouteTokenExchange(request);
            if (token is not null)
            {
                return token;
            }

            if (request.Method == HttpMethod.Delete)
            {
                // ARM's other delete answer: over by the time it replies.
                return AzureArmApiDouble.Empty(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Put)
            {
                created = true;
                return AzureArmApiDouble.Json(
                    HttpStatusCode.Created,
                    AzureScenario.VirtualMachineJson(provisioningState: "Creating", imageUrn: TargetImage));
            }

            if (request.Method != HttpMethod.Get)
            {
                throw new InvalidOperationException(
                    $"The replace path issued an unexpected {request.Method} request to '{request.Uri}'.");
            }

            if (!created || !request.Uri.AbsolutePath.EndsWith(AzureScenario.VmId, StringComparison.Ordinal))
            {
                return AzureArmApiDouble.Json(
                    HttpStatusCode.OK,
                    ReadPayload(request, created ? TargetImage : AzureScenario.ImageUrn));
            }

            var state = provisioningStates[Math.Min(reads, provisioningStates.Count - 1)];
            reads++;

            return AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.VirtualMachineJson(provisioningState: state, imageUrn: TargetImage));
        };
    }

    /// <summary>Routes a delete ARM refuses outright, so nothing is ever deleted or created.</summary>
    private static void RouteDeleteRefused(AzureScenario scenario, string armMessage) =>
        scenario.Api.Responder = request =>
            AzureScenario.RouteTokenExchange(request)
            ?? (request.Method == HttpMethod.Delete
                ? AzureArmApiDouble.Json(
                    HttpStatusCode.Conflict,
                    "{\"error\":{\"code\":\"ScopeLocked\",\"message\":\"" + armMessage + "\"}}")
                : request.Method == HttpMethod.Get
                    ? AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request))
                    : throw new InvalidOperationException(
                        $"A refused delete was followed by a {request.Method} request to '{request.Uri}'."));

    /// <summary>Routes an ordinary resize, so the resize path can be exercised without the replace path.</summary>
    private static void RouteResizeOnly(AzureScenario scenario) =>
        scenario.Api.Responder = request =>
            AzureScenario.RouteTokenExchange(request)
            ?? (request.Method == HttpMethod.Patch
                ? AzureArmApiDouble.Json(
                    HttpStatusCode.OK,
                    "{\"properties\":{\"provisioningState\":\"Succeeded\"}}")
                : request.Method == HttpMethod.Get
                    ? AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request))
                    : throw new InvalidOperationException(
                        $"The resize path issued an unexpected {request.Method} request to '{request.Uri}'."));

    /// <summary>The <c>202</c> ARM answers a delete with, naming the operation it created to track it.</summary>
    private static HttpResponseMessage Accepted(string operationUri)
    {
        var response = AzureArmApiDouble.Empty(HttpStatusCode.Accepted);
        response.Headers.Add("Azure-AsyncOperation", operationUri);
        return response;
    }

    /// <summary>The status document an <c>Azure-AsyncOperation</c> URL answers with.</summary>
    private static string OperationStatusJson(string status, string? errorMessage = null) =>
        "{\"status\":\"" + status + "\""
        + (errorMessage is null
            ? string.Empty
            : ",\"error\":{\"code\":\"OperationFailed\",\"message\":\"" + errorMessage + "\"}")
        + "}";

    /// <summary>The substituted ARM object for whichever resource a read names, with the machine reimaged.</summary>
    private static string ReadPayload(RecordedRequest request, string imageUrn) =>
        request.Uri.AbsolutePath.Contains("/Microsoft.Compute/virtualMachines/", StringComparison.OrdinalIgnoreCase)
            ? AzureScenario.VirtualMachineJson(imageUrn: imageUrn)
            : AzureScenario.PayloadFor(request);
}
