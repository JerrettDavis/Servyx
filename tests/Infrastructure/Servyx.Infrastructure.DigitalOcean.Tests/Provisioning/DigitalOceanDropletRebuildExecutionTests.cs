using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The <see cref="IDestructiveUpdateApplier"/> half of the droplet adapter: the one operation in this
/// codebase that deletes a customer's data on purpose, the many shapes it refuses, and — the assertions the
/// rest of this file exists to protect — that every refusal sends <em>nothing</em>, and that an accepted
/// rebuild is never mistaken for a finished one.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted DigitalOcean API, so no network access, no account, no token
/// beyond the fake one in the scenario, and no real droplet is ever reimaged. The refusal tests assert
/// <c>scenario.Api.Requests</c> is <em>empty</em> — a genuine request count of zero, not merely "an error
/// came back" — because an error return with a rebuild already in flight is exactly the failure these gates
/// exist to rule out, and only the count can tell the two apart. They additionally install
/// <see cref="FailOnAnyRequest"/>, so a request would fail the test at the point it was issued even if the
/// count assertion were deleted.
/// </para>
/// <para>
/// The acknowledgement checked here is a <see cref="DataImpact"/> rather than Servyx.Application's
/// <c>DataImpactAcknowledgement</c> token, because that token lives in the Application layer and this
/// assembly's subject references only <c>Servyx.Domain</c>. The token half of the same gate — that only
/// <c>Destroyed()</c> produces the value these tests pass, and that no plan can derive it — is asserted in
/// <c>Servyx.Application.Tests</c>.
/// </para>
/// </remarks>
public class DigitalOceanDropletRebuildExecutionTests
{
    /// <summary>The size the live droplet in the scenario reports. Held constant so only the image differs.</summary>
    private const string LiveSize = "s-2vcpu-4gb";

    /// <summary>The image these tests ask the droplet to be rebuilt from. Not the image it is running.</summary>
    private const string TargetImage = "ubuntu-22-04-x64";

    /// <summary>The action id the substituted API hands back from a rebuild submission.</summary>
    private const long ActionId = 41007733;

    // -------------------------------------------------------------------------------------------------
    // The adapter is a destructive update applier at all, and says so honestly
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_a_destructive_update_applier_and_the_two_ids_agree()
    {
        var provisioner = new DigitalOceanScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IDestructiveUpdateApplier>();
        ((IDestructiveUpdateApplier)provisioner).ProvisionerId.Should().Be(DigitalOceanDropletProvisioner.Id);
    }

    // -------------------------------------------------------------------------------------------------
    // The one operation it performs, and the shape of the request that performs it
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rebuild_submits_exactly_one_action_naming_the_droplet_and_the_image()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        RouteRebuild(scenario, ["completed"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        var submissions = scenario.Api.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        submissions.Should().ContainSingle("a rebuild is one action, submitted once");

        var submission = submissions[0];
        submission.Uri.AbsolutePath.Should().Be(
            string.Create(CultureInfo.InvariantCulture, $"/v2/droplets/{DigitalOceanScenario.DropletId}/actions"));

        submission.Body.Should().NotBeNull();
        submission.Body.Should().Contain("\"type\":\"rebuild\"");
        submission.Body.Should().Contain("\"image\":\"" + TargetImage + "\"");
    }

    [Fact]
    public async Task A_rebuild_request_never_carries_the_resize_type_and_never_names_a_size_or_a_disk()
    {
        // The action type is the only difference between the request that changes a droplet's CPU allocation
        // and the request that erases its boot disk. Both bodies fix their own type with a property that has
        // no setter, so neither can become the other; this is that claim asserted on the wire.
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        RouteRebuild(scenario, ["in-progress", "completed"]);

        await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var bodies = scenario.Api.Requests.Where(r => r.Body is not null).Select(r => r.Body!).ToList();
        bodies.Should().NotBeEmpty();
        bodies.Should().OnlyContain(b => !b.Contains("\"type\":\"resize\"", StringComparison.Ordinal));
        bodies.Should().OnlyContain(b => !b.Contains("\"size\"", StringComparison.Ordinal));
        bodies.Should().OnlyContain(b => !b.Contains("\"disk\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resize_request_never_carries_the_rebuild_type_and_never_names_an_image()
    {
        // The mirror of the test above, taken through the resize entry point. Neither request type can be
        // expressed as the other, so the two operations cannot be confused at the provider.
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();
        var provisioner = scenario.Provisioner();

        var plan = await provisioner.PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(size: "s-4vcpu-8gb"));

        plan.Should().NotBeNull();
        scenario.Api.Requests.Clear();
        RouteRebuild(scenario, ["completed"], dropletImageAfter: DigitalOceanScenario.ImageSlug);

        await provisioner.ApplyUpdateAsync(DigitalOceanScenario.RecordedHandle(), plan!, plan!.PlanHash);

        var bodies = scenario.Api.Requests.Where(r => r.Body is not null).Select(r => r.Body!).ToList();
        bodies.Should().NotBeEmpty();
        bodies.Should().OnlyContain(b => !b.Contains("\"type\":\"rebuild\"", StringComparison.Ordinal));
        bodies.Should().OnlyContain(b => !b.Contains("\"image\"", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------------
    // The acknowledgement gate. Every one of these asserts a request count of zero.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rebuild_with_no_acknowledgement_at_all_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, acknowledgedDataImpact: null);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the acknowledgement supplied was none");

        scenario.Api.Requests.Should().BeEmpty("a rebuild nobody acknowledged is never sent");
    }

    [Fact]
    public async Task A_rebuild_acknowledged_only_as_at_risk_is_refused_and_issues_no_http_request()
    {
        // Acknowledging that data might be separated from the workload is not acknowledging that it will be
        // deleted. The two are different approvals and the milder one authorises nothing here.
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.AtRisk);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the acknowledgement supplied was AtRisk");

        scenario.Api.Requests.Should().BeEmpty("an AtRisk approval never authorises a disk erasure");
    }

    [Fact]
    public async Task A_rebuild_acknowledged_as_preserved_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Preserved);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("the acknowledgement supplied was Preserved");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_acknowledgement_is_checked_before_anything_at_all_has_been_read()
    {
        // The strongest form of the same claim: this scenario has issued no request ever, so its empty
        // request list cannot be an artefact of anything having been cleared after the fact.
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            HandBuiltRebuildPlan(),
            approvedPlanHash: RebuildPlanHash,
            acknowledgedDataImpact: null);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Every other refusal. Also a request count of zero, every time.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_issues_no_http_request_even_with_a_matching_token()
    {
        // The acknowledgement is not a force flag: a correct token does not make a stale plan runnable.
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
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
        // A droplet cannot be moved. Its plan is Destroyed and Recreate, so it reaches this entry point -
        // and is refused here, because no rebuild would relocate the machine.
        var (scenario, provisioner, plan) = await PlannedAsync(size: LiveSize, region: "sfo3");

        plan.DataImpact.Should().Be(DataImpact.Destroyed);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("cannot be moved between regions");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rebuild_bundled_with_a_resize_is_refused_rather_than_partly_applied()
    {
        // Executing the half it understands would report a half-applied update as an applied one - and the
        // half it understands is the irreversible one.
        var (scenario, provisioner, plan) = await PlannedAsync(size: "s-4vcpu-8gb", image: TargetImage);

        plan.Changes.Should().HaveCount(2);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("rebuild and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_whose_impact_is_not_destroyed_is_refused_and_issues_no_http_request()
    {
        // A plan claiming something milder than what a rebuild actually does is a reason to stop.
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltRebuildPlan(dataImpact: DataImpact.Preserved);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("only a plan that states Destroyed");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_rebuild_entry_point_cannot_resize_and_issues_no_http_request()
    {
        // The mirror of the existing "a rebuild plan is refused by the resize path" assertion. A size change
        // handed to this member - even labelled Destroyed and fully acknowledged - is refused, so the two
        // entry points cannot stand in for one another.
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltRebuildPlan(
            changes: [new PlannedChange("size", LiveSize, "s-4vcpu-8gb", RequiresRecreate: false)]);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("rebuild and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_with_no_http_request()
    {
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltRebuildPlan(provisionerId: "docker-container");

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("docker-container");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_that_is_not_a_droplet_id_is_refused_with_no_http_request()
    {
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltRebuildPlan();
        var handle = new ResourceHandle(
            DigitalOceanDropletProvisioner.Id,
            "not-a-droplet-id",
            "nyc3",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            handle, plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not a DigitalOcean droplet id");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_change_naming_no_target_is_refused_with_no_http_request()
    {
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltRebuildPlan(
            changes: [new PlannedChange("image", DigitalOceanScenario.ImageSlug, null, RequiresRecreate: true)]);

        var result = await scenario.Provisioner().ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("names no target image");

        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Submission is not success: the three ends an action can reach, kept apart
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_still_in_progress_when_the_polls_are_spent_is_neither_success_nor_failure()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync(actionPollAttempts: 3);
        RouteRebuild(scenario, ["in-progress"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        // Not success - the rebuild was never observed finishing.
        result.Should().NotBeOfType<UpdateExecutionResult.Completed>();

        // And not a failure either. A rebuild takes minutes, so "still reimaging" is the likeliest reading of
        // this outcome, and it calls for the opposite response from "the reimage failed".
        result.Should().NotBeOfType<UpdateExecutionResult.Failed>();

        var timedOut = result.Should().BeOfType<UpdateExecutionResult.TimedOut>().Which;
        timedOut.Message.Should().Contain("NOT confirmed");
        timedOut.Message.Should().Contain("NOT reported as failed");
        timedOut.Message.Should().Contain("still running");
        timedOut.Message.Should().Contain("do NOT resubmit");

        // The polls were really made, and really stopped where they were told to.
        ActionReads(scenario).Should().Be(3);
    }

    [Fact]
    public async Task A_still_running_rebuild_and_an_errored_rebuild_are_different_types()
    {
        var (runningScenario, runningProvisioner, runningPlan) = await PlannedRebuildAsync();
        RouteRebuild(runningScenario, ["in-progress"]);
        var running = await runningProvisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), runningPlan, runningPlan.PlanHash, DataImpact.Destroyed);

        var (erroredScenario, erroredProvisioner, erroredPlan) = await PlannedRebuildAsync();
        RouteRebuild(erroredScenario, ["errored"]);
        var errored = await erroredProvisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), erroredPlan, erroredPlan.PlanHash, DataImpact.Destroyed);

        running.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        errored.Should().BeOfType<UpdateExecutionResult.Failed>();
        running.GetType().Should().NotBe(errored.GetType());

        // And the two messages tell an operator to do opposite things, which is the point of the distinction.
        running.Message.Should().Contain("do NOT resubmit");
        errored.Message.Should().Contain("did not complete");
    }

    [Fact]
    public async Task An_errored_action_is_a_failure_carrying_the_providers_own_message()
    {
        const string ProviderMessage = "Droplet is currently locked by another action";

        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        RouteRebuild(scenario, ["in-progress", "errored"], actionMessage: ProviderMessage);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain(ProviderMessage);
    }

    [Fact]
    public async Task A_submission_the_provider_refuses_is_a_failure_and_nothing_was_erased()
    {
        const string ProviderMessage = "image is not available in this region";

        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.UnprocessableEntity,
            "{\"id\":\"unprocessable_entity\",\"message\":\"" + ProviderMessage + "\"}");

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;
        failed.Message.Should().Contain(ProviderMessage);
        failed.Message.Should().Contain("its disk was not erased");

        // One attempt, and no polling of an action that was never created.
        scenario.Api.Requests.Should().ContainSingle();
        ActionReads(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_completed_rebuild_is_reported_only_after_the_poll_observes_it_completed()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync(actionPollAttempts: 5);
        RouteRebuild(scenario, ["in-progress", "in-progress", "completed"]);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;
        completed.Message.Should().Contain("completed after 3 check(s)");
        completed.Message.Should().Contain(TargetImage);
        completed.Message.Should().Contain("boot disk was erased");
        completed.Resource.Handle.ProviderResourceId.Should().Be(
            DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture));

        // Three reads: the success came from an observation, not from the submission.
        ActionReads(scenario).Should().Be(3);
    }

    [Fact]
    public async Task An_action_the_submission_already_calls_completed_is_still_not_trusted()
    {
        // DigitalOcean's POST response carries a status of its own. If that status were believed, this test
        // would report a finished rebuild; the poll is the only thing that decides, and here it never agrees.
        var (scenario, provisioner, plan) = await PlannedRebuildAsync(actionPollAttempts: 2);
        RouteRebuild(scenario, ["in-progress"], submissionStatus: "completed");

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        result.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        ActionReads(scenario).Should().Be(2);
    }

    [Fact]
    public async Task A_completed_rebuild_hands_back_the_droplet_as_it_now_is()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        RouteRebuild(scenario, ["completed"], dropletImageAfter: TargetImage);

        var result = await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;

        // Re-read after the action finished, so the resource describes the machine that exists now.
        scenario.Api.Requests
            .Count(r => r.Method == HttpMethod.Get
                && r.Uri.AbsolutePath.StartsWith("/v2/droplets/", StringComparison.Ordinal))
            .Should().Be(1);

        completed.Resource.ConnectorId.Should().Be(DigitalOceanScenario.ConnectorId);
    }

    [Fact]
    public async Task Every_request_a_rebuild_makes_still_carries_a_freshly_resolved_bearer_token()
    {
        var (scenario, provisioner, plan) = await PlannedRebuildAsync();
        scenario.Secrets.Resolved.Clear();
        RouteRebuild(scenario, ["completed"]);

        await provisioner.ApplyDestructiveUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash, DataImpact.Destroyed);

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(
            r => r.Authorization == "Bearer " + DigitalOceanScenario.ApiToken);

        // One resolution per request, so nothing on this path caches the token.
        scenario.Secrets.Resolved.Should().HaveCount(scenario.Api.Requests.Count);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>The hash carried by every hand-built plan below.</summary>
    private const string RebuildPlanHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Plans a real, lone rebuild against the substituted API — same size, different image.</summary>
    private static Task<(DigitalOceanScenario Scenario, DigitalOceanDropletProvisioner Provisioner, UpdatePlan Plan)>
        PlannedRebuildAsync(int actionPollAttempts = 3) =>
        PlannedAsync(size: LiveSize, image: TargetImage, actionPollAttempts: actionPollAttempts);

    /// <summary>
    /// Builds a genuine <see cref="UpdatePlan"/> with the real planner, then clears the recorded requests so
    /// the execution assertions that follow count only what execution itself issued.
    /// </summary>
    private static async Task<(DigitalOceanScenario Scenario, DigitalOceanDropletProvisioner Provisioner, UpdatePlan Plan)>
        PlannedAsync(
            string size,
            string? image = null,
            string? region = null,
            int actionPollAttempts = 3)
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var provisioner = scenario.Provisioner(actionPollAttempts: actionPollAttempts);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (image is not null)
        {
            overrides["image"] = image;
        }

        if (region is not null)
        {
            overrides["region"] = region;
        }

        var plan = await provisioner.PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(overrides, size));

        plan.Should().NotBeNull();
        scenario.Api.Requests.Clear();

        return (scenario, provisioner, plan!);
    }

    /// <summary>
    /// A plan built by hand, for the shapes the real planner cannot produce — a plan belonging to another
    /// provisioner, an image change naming no target, a Destroyed plan whose lone change is a resize.
    /// </summary>
    private static UpdatePlan HandBuiltRebuildPlan(
        IReadOnlyList<PlannedChange>? changes = null,
        string provisionerId = DigitalOceanDropletProvisioner.Id,
        DataImpact dataImpact = DataImpact.Destroyed) =>
        new(
            planId: "test:update:1",
            planHash: RebuildPlanHash,
            provisionerId: provisionerId,
            strategy: UpdateStrategy.Recreate,
            dataImpact: dataImpact,
            changes: changes
                ?? [new PlannedChange("image", DigitalOceanScenario.ImageSlug, TargetImage, RequiresRecreate: true)],
            stages: [new ProvisioningStage("rebuild-droplet", provisionerId, "Rebuild the droplet.")],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>Makes any HTTP request at all fail the test where it happens.</summary>
    private static void FailOnAnyRequest(DigitalOceanScenario scenario) =>
        scenario.Api.Responder = request => throw new InvalidOperationException(
            $"A refused rebuild issued a {request.Method} request to '{request.Uri}'. It must send nothing at all.");

    /// <summary>How many times the action endpoint was read.</summary>
    private static int ActionReads(DigitalOceanScenario scenario) =>
        scenario.Api.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/v2/actions/", StringComparison.Ordinal));

    /// <summary>
    /// Routes the rebuild exchange: one POST answering with an action, then action reads walking
    /// <paramref name="actionStatuses"/> (repeating the last one), then droplet reads.
    /// </summary>
    private static void RouteRebuild(
        DigitalOceanScenario scenario,
        IReadOnlyList<string> actionStatuses,
        string? actionMessage = null,
        string submissionStatus = "in-progress",
        string dropletImageAfter = TargetImage)
    {
        var reads = 0;

        scenario.Api.Responder = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return DigitalOceanApiDouble.Json(
                    HttpStatusCode.Created,
                    ActionEnvelopeJson(ActionId, submissionStatus));
            }

            if (request.Uri.AbsolutePath.StartsWith("/v2/actions/", StringComparison.Ordinal))
            {
                var status = actionStatuses[Math.Min(reads, actionStatuses.Count - 1)];
                reads++;
                return DigitalOceanApiDouble.Json(
                    HttpStatusCode.OK,
                    ActionEnvelopeJson(ActionId, status, actionMessage));
            }

            return DigitalOceanApiDouble.Json(
                HttpStatusCode.OK,
                DigitalOceanScenario.DropletEnvelopeJson(imageSlug: dropletImageAfter));
        };
    }

    /// <summary>An <c>{ "action": ... }</c> envelope as DigitalOcean reports one for a rebuild.</summary>
    private static string ActionEnvelopeJson(long id, string status, string? message = null) =>
        "{\"action\":{\"id\":" + id.ToString(CultureInfo.InvariantCulture)
        + ",\"status\":\"" + status + "\""
        + ",\"type\":\"rebuild\""
        + ",\"resource_id\":" + DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture)
        + ",\"resource_type\":\"droplet\""
        + (message is null ? string.Empty : ",\"message\":\"" + message + "\"")
        + "}}";
}
