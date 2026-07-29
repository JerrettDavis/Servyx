using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the VM adapter: the one operation it will carry out, the many it
/// refuses, and — the assertions the rest of this file exists to protect — that a refusal sends nothing and
/// that an accepted request is never mistaken for a finished one.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted ARM and token service, so no network access, no Azure
/// subscription and no service principal beyond the fake one in the scenario is involved. The refusal tests
/// assert <c>scenario.Api.Requests</c> is <em>empty</em> — a request count of zero, not merely "an error came
/// back" — because the claim being made is about Azure's state and not about this process's. Note what "zero"
/// covers here that it does not for DigitalOcean: the token exchange is a request too, so a refused plan is
/// proved not even to have bought a credential.
/// </para>
/// <para>
/// The suite-wide claim that no update body ever names an image is enforced in
/// <see cref="AzureArmApiDouble"/> itself, which every request in this assembly passes through, and is proved
/// to be a real check by <see cref="The_api_double_fails_an_update_whose_body_names_the_image"/> below.
/// </para>
/// </remarks>
public class AzureVirtualMachineResizeExecutionTests
{
    /// <summary>The size these tests ask the machine to be resized to. Distinct from <see cref="AzureScenario.VmSize"/>.</summary>
    private const string TargetSize = "Standard_D2s_v5";

    /// <summary>The id of the long-running operation the substituted ARM creates for a resize.</summary>
    private const string OperationId = "11111111-aaaa-bbbb-cccc-222222222222";

    /// <summary>The absolute URL the substituted ARM hands back in <c>Azure-AsyncOperation</c>.</summary>
    private const string OperationUri =
        "https://management.azure.com/subscriptions/" + AzureScenario.SubscriptionId
        + "/providers/Microsoft.Compute/locations/" + AzureScenario.Region
        + "/operations/" + OperationId + "?api-version=2024-07-01";

    // -------------------------------------------------------------------------------------------------
    // The adapter is an update applier at all, and says so honestly
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_an_update_applier_and_the_two_ids_agree()
    {
        var provisioner = new AzureScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IUpdateApplier>();
        ((IUpdateApplier)provisioner).ProvisionerId.Should().Be(AzureVirtualMachineProvisioner.Id);
    }

    // -------------------------------------------------------------------------------------------------
    // The one operation it performs, and the shape of the request that performs it
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resize_issues_one_patch_whose_body_sets_only_the_vm_size()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteResize(scenario, ["Succeeded"]);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        var submissions = scenario.Api.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        submissions.Should().ContainSingle("a resize is one write, submitted once");

        var submission = submissions[0];
        submission.Uri.AbsolutePath.Should().Be(AzureScenario.VmId);
        submission.Uri.Query.Should().Contain("api-version=");

        submission.Body.Should().NotBeNull();
        submission.Body.Should().Contain("\"hardwareProfile\"");
        submission.Body.Should().Contain("\"vmSize\":\"" + TargetSize + "\"");

        // The whole safety claim of this adapter, on the wire: a body that cannot describe a different machine.
        submission.Body.Should().NotContain("storageProfile");
        submission.Body.Should().NotContain("imageReference");
        submission.Body.Should().NotContain("osDisk");

        // And it really is the merge verb, not a whole-resource write that would have to carry the image back.
        scenario.Api.ArmRequests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task No_request_a_resize_issues_carries_an_image_reference_or_a_storage_profile()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteResize(scenario, ["InProgress", "Succeeded"]);

        await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests
            .Where(r => r.Body is not null)
            .Should().OnlyContain(r =>
                !r.Body!.Contains("imageReference", StringComparison.OrdinalIgnoreCase)
                && !r.Body!.Contains("storageProfile", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"properties\":{\"storageProfile\":{\"imageReference\":{\"publisher\":\"Debian\"}}}}", "storageProfile")]
    [InlineData("{\"properties\":{\"hardwareProfile\":{\"vmSize\":\"Standard_D2s_v5\"},\"imageReference\":{\"sku\":\"12\"}}}", "imageReference")]
    public async Task The_api_double_fails_an_update_whose_body_names_the_image(string body, string expected)
    {
        // The suite-wide guarantee is only worth anything if the check behind it is real, so this test sends
        // the forbidden body deliberately - the only place in the assembly that does - and asserts the double
        // refuses it. Nothing in the production adapter can build this body: the resize request type has no
        // member that could carry either name, so it is constructed here by hand.
        using var api = new AzureArmApiDouble();
        using var client = api.Client();

        var thrown = await Record.ExceptionAsync(() => client.PatchAsync(
            new Uri("https://management.azure.com" + AzureScenario.VmId + "?api-version=2024-07-01"),
            new StringContent(body)));

        thrown.Should().NotBeNull();

        // HttpClient may wrap a handler failure, so the whole chain is searched rather than only the top.
        var messages = new List<string>();
        for (var exception = thrown; exception is not null; exception = exception.InnerException)
        {
            messages.Add(exception.Message);
        }

        messages.Should().Contain(m => m.Contains(expected, StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals. Every one of these asserts a request count of zero.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            AzureScenario.RecordedHandle(),
            plan,
            approvedPlanHash: "0000000000000000000000000000000000000000000000000000000000000000");

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not the plan that was approved");

        scenario.Api.Requests.Should().BeEmpty("a refused plan sends nothing to Azure");
    }

    [Fact]
    public async Task A_stale_plan_hash_is_refused_even_when_nothing_has_ever_been_read()
    {
        // The strongest form of the same claim: this scenario has issued no request at all, ever - not even the
        // token exchange - so the empty request list cannot be an artefact of anything having been cleared.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AzureScenario.RecordedHandle(),
            HandBuiltPlan([SizeChange()], planHash: "abc123"),
            approvedPlanHash: "def456");

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty("a refusal does not even buy a credential");
    }

    [Fact]
    public async Task An_image_change_plan_is_refused_with_no_http_request()
    {
        // The real thing, planned by the real planner: ARM cannot reimage a machine in place, so an image
        // change is a delete-and-recreate that takes the managed OS disk with it. Replacement is not
        // implemented, and this is the assertion that keeps it that way.
        var (scenario, provisioner, plan) = await PlannedAsync(
            size: AzureScenario.VmSize,
            overrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "Debian:debian-12:12:latest",
            });

        plan.DataImpact.Should().Be(DataImpact.Destroyed);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("only an in-place resize");

        scenario.Api.Requests.Should().BeEmpty("a plan that would delete the OS disk is never sent");
    }

    [Fact]
    public async Task A_region_change_plan_is_refused_with_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(
            size: AzureScenario.VmSize,
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "westus2" });

        plan.Strategy.Should().Be(UpdateStrategy.Recreate);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resource_group_change_plan_is_refused_with_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(
            size: AzureScenario.VmSize,
            overrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceGroup"] = "rg-servyx-somewhere-else",
            });

        plan.Strategy.Should().Be(UpdateStrategy.Recreate);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([SizeChange()], provisionerId: "digitalocean-droplet");

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("digitalocean-droplet");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_that_is_not_a_virtual_machine_id_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([SizeChange()]);

        // A real ARM id, and a real Servyx-owned resource - just not a machine. Resizing "whatever answers to
        // that id" is exactly what this guard exists to prevent.
        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AzureScenario.RecordedHandle(resourceId: AzureScenario.NicId), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("is not the ARM id of a");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_reports_no_change_is_refused_with_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(size: AzureScenario.VmSize);

        plan.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resize_bundled_with_a_tag_write_is_refused_rather_than_partly_applied()
    {
        // Executing the half it understands and skipping the rest would report a half-applied update as an
        // applied one. The tag write is not implemented, so the whole plan is declined.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan(
        [
            SizeChange(),
            new PlannedChange("tag servyx.owner", null, "ops", RequiresRecreate: false),
        ]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("resize and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_does_not_preserve_data_is_refused_with_no_http_request()
    {
        // The data-impact guard on its own, isolated from the strategy guard that catches every replacement the
        // real planner produces. Nothing that admits to destroying data reaches an ARM call from here.
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([SizeChange()], dataImpact: DataImpact.Destroyed);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("Destroyed");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_size_change_naming_no_target_is_refused_with_no_http_request()
    {
        var scenario = new AzureScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([new PlannedChange("size", AzureScenario.VmSize, null, RequiresRecreate: false)]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Submission is not success: the three ends an operation can reach, kept apart
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_operation_still_running_when_the_polls_are_spent_is_neither_success_nor_failure()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 3);
        RouteResize(scenario, ["InProgress"]);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        // Not success - the resize was never observed finishing.
        result.Should().NotBeOfType<UpdateExecutionResult.Completed>();

        // And not a failure either, which is the distinction an operator has to be able to make: a failed
        // resize may be retried, a running one may not.
        result.Should().NotBeOfType<UpdateExecutionResult.Failed>();

        var timedOut = result.Should().BeOfType<UpdateExecutionResult.TimedOut>().Which;
        timedOut.Message.Should().Contain("NOT confirmed");
        timedOut.Message.Should().Contain("NOT reported as failed");
        timedOut.Message.Should().Contain("Do not resubmit");

        // The polls were really made, and really stopped where they were told to.
        OperationReads(scenario).Should().Be(3);

        // Nothing was read back, because nothing was confirmed to have changed.
        VirtualMachineReads(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_still_running_operation_and_a_failed_operation_are_different_types()
    {
        var (runningScenario, runningProvisioner, runningPlan) = await PlannedAsync();
        RouteResize(runningScenario, ["InProgress"]);
        var running = await runningProvisioner.ApplyUpdateAsync(
            AzureScenario.RecordedHandle(), runningPlan, runningPlan.PlanHash);

        var (failedScenario, failedProvisioner, failedPlan) = await PlannedAsync();
        RouteResize(failedScenario, ["Failed"]);
        var failed = await failedProvisioner.ApplyUpdateAsync(
            AzureScenario.RecordedHandle(), failedPlan, failedPlan.PlanHash);

        running.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        failed.Should().BeOfType<UpdateExecutionResult.Failed>();
        running.GetType().Should().NotBe(failed.GetType());
    }

    [Fact]
    public async Task A_failed_operation_is_a_failure_carrying_azures_own_message()
    {
        const string ArmMessage =
            "The requested size Standard_D2s_v5 is not available in the current cluster for the VM palworld-01";

        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteResize(scenario, ["InProgress", "Failed"], errorMessage: ArmMessage);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain(ArmMessage);
    }

    [Fact]
    public async Task A_submission_azure_refuses_is_a_failure_carrying_its_message()
    {
        const string ArmMessage = "Changing property 'hardwareProfile.vmSize' is not allowed while the VM is deallocating";

        var (scenario, provisioner, plan) = await PlannedAsync();
        scenario.Api.Responder = request =>
            AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.Conflict,
                "{\"error\":{\"code\":\"OperationNotAllowed\",\"message\":\"" + ArmMessage + "\"}}");

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain(ArmMessage);

        // One attempt, and no polling of an operation that was never created.
        scenario.Api.Requests.Should().ContainSingle();
        OperationReads(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_completed_resize_is_reported_only_after_a_terminal_success_is_observed()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 5);
        RouteResize(scenario, ["InProgress", "InProgress", "Succeeded"]);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;
        completed.Message.Should().Contain("succeeded after 3 check(s)");

        // Three reads: the success came from an observation, not from the submission.
        OperationReads(scenario).Should().Be(3);
    }

    [Fact]
    public async Task A_submission_that_reports_success_in_its_own_body_is_still_not_trusted()
    {
        // The PATCH response carries a provisioning state of its own. If that state were believed, this test
        // would report success; the long-running operation ARM named is the only thing that decides, and here
        // it never agrees.
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 2);
        RouteResize(scenario, ["InProgress"], submissionProvisioningState: "Succeeded");

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        OperationReads(scenario).Should().Be(2);
    }

    [Fact]
    public async Task A_resize_azure_names_no_operation_for_is_confirmed_from_the_resources_own_state()
    {
        // ARM's other answer: 200 with the resource and no tracking header. "Accepted" is still not "done" -
        // the provisioning state says Updating - so the machine is re-read until ARM reports it Succeeded.
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 4);
        RouteResizeWithoutTracker(scenario, ["Updating", "Succeeded"]);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>()
            .Which.Message.Should().Contain("succeeded after 2 check(s)");
    }

    [Fact]
    public async Task A_completed_resize_hands_back_the_machine_as_it_now_is()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteResize(scenario, ["Succeeded"], vmSizeAfter: TargetSize);

        var result = await provisioner.ApplyUpdateAsync(AzureScenario.RecordedHandle(), plan, plan.PlanHash);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;

        completed.Resource.Handle.ProviderResourceId.Should().Be(AzureScenario.VmId);
        completed.Resource.ConnectorId.Should().Be(AzureScenario.ConnectorId);

        // Re-read after the operation finished, so the resource describes the machine that exists now.
        VirtualMachineReads(scenario).Should().BeGreaterThan(0);

        completed.Message.Should().Contain(TargetSize);
        completed.Message.Should().Contain("properties.hardwareProfile.vmSize");
        completed.Message.Should().Contain("every file on the machine is where it was");

        // The interruption is stated rather than hidden behind "in place", and is named as what it is.
        completed.Message.Should().Contain("deallocated and restarted");
        completed.Message.Should().Contain("service interruption, not an impact on persistent data");
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a genuine <see cref="UpdatePlan"/> with the real planner, then clears the recorded requests so
    /// the execution assertions that follow count only what execution itself issued.
    /// </summary>
    private static async Task<(AzureScenario Scenario, AzureVirtualMachineProvisioner Provisioner, UpdatePlan Plan)>
        PlannedAsync(
            string size = TargetSize,
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

    /// <summary>The lone size change every hand-built plan in this file is built around.</summary>
    private static PlannedChange SizeChange() =>
        new("size", AzureScenario.VmSize, TargetSize, RequiresRecreate: false);

    /// <summary>
    /// A plan built by hand, for the shapes the real planner cannot currently produce — a plan belonging to
    /// another provisioner, a resize bundled with a tag write, a size change naming no target.
    /// </summary>
    private static UpdatePlan HandBuiltPlan(
        IReadOnlyList<PlannedChange> changes,
        string planHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        string provisionerId = AzureVirtualMachineProvisioner.Id,
        UpdateStrategy strategy = UpdateStrategy.InPlace,
        DataImpact dataImpact = DataImpact.Preserved) =>
        new(
            planId: "test:update:1",
            planHash: planHash,
            provisionerId: provisionerId,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: [new ProvisioningStage("resize-virtual-machine", provisionerId, "Resize the machine.")],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>Makes any HTTP request at all — to ARM or to the token service — fail the test where it happens.</summary>
    private static void FailOnAnyRequest(AzureScenario scenario) =>
        scenario.Api.Responder = request => throw new InvalidOperationException(
            $"A refused update issued a {request.Method} request to '{request.Uri}'. It must send nothing at all.");

    /// <summary>How many times the long-running operation endpoint was read.</summary>
    private static int OperationReads(AzureScenario scenario) =>
        scenario.Api.Requests.Count(r => r.Uri.AbsolutePath.Contains("/operations/", StringComparison.Ordinal));

    /// <summary>How many times the virtual machine itself was read.</summary>
    private static int VirtualMachineReads(AzureScenario scenario) =>
        scenario.Api.Requests.Count(r =>
            r.Method == HttpMethod.Get
            && r.Uri.AbsolutePath.EndsWith(AzureScenario.VmId, StringComparison.Ordinal));

    /// <summary>
    /// Routes the resize exchange as ARM's asynchronous form: one PATCH answered <c>202</c> with an
    /// <c>Azure-AsyncOperation</c> header, then operation reads walking <paramref name="operationStatuses"/>
    /// (repeating the last one), then ordinary resource reads for the read-back.
    /// </summary>
    private static void RouteResize(
        AzureScenario scenario,
        IReadOnlyList<string> operationStatuses,
        string? errorMessage = null,
        string? submissionProvisioningState = null,
        string vmSizeAfter = TargetSize)
    {
        var reads = 0;

        scenario.Api.Responder = request =>
        {
            var token = AzureScenario.RouteTokenExchange(request);
            if (token is not null)
            {
                return token;
            }

            if (request.Method == HttpMethod.Patch)
            {
                return Accepted(submissionProvisioningState);
            }

            if (request.Uri.AbsolutePath.Contains("/operations/", StringComparison.Ordinal))
            {
                var status = operationStatuses[Math.Min(reads, operationStatuses.Count - 1)];
                reads++;
                return AzureArmApiDouble.Json(HttpStatusCode.OK, OperationStatusJson(status, errorMessage));
            }

            return request.Method == HttpMethod.Get
                ? AzureArmApiDouble.Json(HttpStatusCode.OK, ReadPayload(request, vmSizeAfter))
                : throw new InvalidOperationException(
                    $"The resize path issued an unexpected {request.Method} request to '{request.Uri}'.");
        };
    }

    /// <summary>
    /// Routes the resize exchange as ARM's synchronous-looking form: a <c>200</c> carrying the resource and
    /// <em>no</em> tracking header, so the only evidence available is the machine's own provisioning state,
    /// walked through <paramref name="provisioningStates"/> (repeating the last one).
    /// </summary>
    private static void RouteResizeWithoutTracker(AzureScenario scenario, IReadOnlyList<string> provisioningStates)
    {
        var reads = 0;

        scenario.Api.Responder = request =>
        {
            var token = AzureScenario.RouteTokenExchange(request);
            if (token is not null)
            {
                return token;
            }

            if (request.Method == HttpMethod.Patch)
            {
                // Accepted, and explicitly not finished.
                return AzureArmApiDouble.Json(
                    HttpStatusCode.OK,
                    AzureScenario.VirtualMachineJson(provisioningState: "Updating", vmSize: TargetSize));
            }

            if (request.Method != HttpMethod.Get)
            {
                throw new InvalidOperationException(
                    $"The resize path issued an unexpected {request.Method} request to '{request.Uri}'.");
            }

            if (!request.Uri.AbsolutePath.EndsWith(AzureScenario.VmId, StringComparison.Ordinal))
            {
                return AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request));
            }

            var state = provisioningStates[Math.Min(reads, provisioningStates.Count - 1)];
            reads++;

            return AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.VirtualMachineJson(provisioningState: state, vmSize: TargetSize));
        };
    }

    /// <summary>The <c>202</c> ARM answers a resize with, naming the operation it created to track it.</summary>
    private static HttpResponseMessage Accepted(string? provisioningState)
    {
        var response = provisioningState is null
            ? AzureArmApiDouble.Empty(HttpStatusCode.Accepted)
            : AzureArmApiDouble.Json(
                HttpStatusCode.Accepted,
                "{\"properties\":{\"provisioningState\":\"" + provisioningState + "\"}}");

        response.Headers.Add("Azure-AsyncOperation", OperationUri);
        return response;
    }

    /// <summary>The status document an <c>Azure-AsyncOperation</c> URL answers with.</summary>
    private static string OperationStatusJson(string status, string? errorMessage = null) =>
        "{\"name\":\"" + OperationId + "\",\"status\":\"" + status + "\""
        + (errorMessage is null
            ? string.Empty
            : ",\"error\":{\"code\":\"OperationFailed\",\"message\":\"" + errorMessage + "\"}")
        + "}";

    /// <summary>The substituted ARM object for whichever resource a read names, with the machine at its new size.</summary>
    private static string ReadPayload(RecordedRequest request, string vmSize) =>
        request.Uri.AbsolutePath.Contains("/Microsoft.Compute/virtualMachines/", StringComparison.OrdinalIgnoreCase)
            ? AzureScenario.VirtualMachineJson(vmSize: vmSize)
            : AzureScenario.PayloadFor(request);
}
