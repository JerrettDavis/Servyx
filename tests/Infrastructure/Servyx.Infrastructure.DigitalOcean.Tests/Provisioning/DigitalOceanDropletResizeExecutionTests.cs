using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the droplet adapter: the one operation it will carry out, the
/// many it refuses, and — the assertions the rest of this file exists to protect — that a refusal sends
/// nothing and that an accepted request is never mistaken for a finished one.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted DigitalOcean API, so no network access, no account and no
/// token beyond the fake one in the scenario is involved. The refusal tests assert
/// <c>scenario.Api.Requests</c> is <em>empty</em> — a request count of zero, not merely "an error came
/// back" — because the claim being made is about DigitalOcean's state and not about this process's.
/// </para>
/// <para>
/// The suite-wide claim that no request body ever sets <c>disk: true</c> is enforced in
/// <see cref="DigitalOceanApiDouble"/> itself, which every request in this assembly passes through, and is
/// proved to be a real check by
/// <see cref="The_api_double_fails_any_request_whose_body_sets_disk_true"/> below.
/// </para>
/// </remarks>
public class DigitalOceanDropletResizeExecutionTests
{
    /// <summary>The size the live droplet in the scenario reports.</summary>
    private const string LiveSize = "s-2vcpu-4gb";

    /// <summary>The size these tests ask the droplet to be resized to.</summary>
    private const string TargetSize = "s-4vcpu-8gb";

    /// <summary>The action id the substituted API hands back from a resize submission.</summary>
    private const long ActionId = 36804636;

    // -------------------------------------------------------------------------------------------------
    // The adapter is an update applier at all, and says so honestly
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_an_update_applier_and_the_two_ids_agree()
    {
        var provisioner = new DigitalOceanScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IUpdateApplier>();
        ((IUpdateApplier)provisioner).ProvisionerId.Should().Be(DigitalOceanDropletProvisioner.Id);
    }

    // -------------------------------------------------------------------------------------------------
    // The one operation it performs, and the shape of the request that performs it
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resize_submits_exactly_one_action_request_whose_body_sets_disk_false()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        RouteResize(scenario, ["completed"]);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        var submissions = scenario.Api.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        submissions.Should().ContainSingle("a resize is one action, submitted once");

        var submission = submissions[0];
        submission.Uri.AbsolutePath.Should().Be(
            string.Create(CultureInfo.InvariantCulture, $"/v2/droplets/{DigitalOceanScenario.DropletId}/actions"));

        submission.Body.Should().NotBeNull();
        submission.Body.Should().Contain("\"type\":\"resize\"");
        submission.Body.Should().Contain("\"size\":\"" + TargetSize + "\"");

        // The whole safety claim of this adapter, on the wire: the CPU-and-memory-only form.
        submission.Body.Should().Contain("\"disk\":false");
        submission.Body.Should().NotContain("\"disk\":true");
    }

    [Fact]
    public async Task No_request_body_issued_during_a_resize_sets_disk_true()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        RouteResize(scenario, ["in-progress", "completed"]);

        await provisioner.ApplyUpdateAsync(DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests
            .Where(r => r.Body is not null)
            .Should().OnlyContain(r => !r.Body!.Contains("\"disk\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_api_double_fails_any_request_whose_body_sets_disk_true()
    {
        // The suite-wide guarantee is only worth anything if the check behind it is real, so this test
        // sends the forbidden body deliberately - the only place in the assembly that does - and asserts the
        // double refuses it. Nothing in the production adapter can build this body: the resize request type's
        // disk member is a property with no setter, so it is constructed here by hand.
        using var api = new DigitalOceanApiDouble();
        using var client = api.Client();

        var thrown = await Record.ExceptionAsync(() => client.PostAsync(
            new Uri("https://api.digitalocean.com/v2/droplets/1/actions"),
            new StringContent("{\"type\":\"resize\",\"size\":\"s-4vcpu-8gb\",\"disk\":true}")));

        thrown.Should().NotBeNull();

        // HttpClient may wrap a handler failure, so the whole chain is searched rather than only the top.
        var messages = new List<string>();
        for (var exception = thrown; exception is not null; exception = exception.InnerException)
        {
            messages.Add(exception.Message);
        }

        messages.Should().Contain(m => m.Contains("\"disk\": true", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals. Every one of these asserts a request count of zero.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_issues_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            plan,
            approvedPlanHash: "0000000000000000000000000000000000000000000000000000000000000000");

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not the plan that was approved");

        scenario.Api.Requests.Should().BeEmpty("a refused plan sends nothing to DigitalOcean");
    }

    [Fact]
    public async Task A_stale_plan_hash_is_refused_even_when_nothing_has_ever_been_read()
    {
        // The strongest form of the same claim: this scenario has issued no request at all, ever, so the
        // empty request list cannot be an artefact of anything having been cleared.
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            HandBuiltPlan([new PlannedChange("size", LiveSize, TargetSize, RequiresRecreate: false)], planHash: "abc123"),
            approvedPlanHash: "def456");

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_with_no_http_request()
    {
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan(
            [new PlannedChange("size", LiveSize, TargetSize, RequiresRecreate: false)],
            provisionerId: "docker-container");

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("docker-container");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_that_is_not_a_droplet_id_is_refused_with_no_http_request()
    {
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([new PlannedChange("size", LiveSize, TargetSize, RequiresRecreate: false)]);
        var handle = new ResourceHandle(
            DigitalOceanDropletProvisioner.Id,
            "not-a-droplet-id",
            "nyc3",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await scenario.Provisioner().ApplyUpdateAsync(handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not a DigitalOcean droplet id");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rebuild_plan_is_refused_with_no_http_request()
    {
        // The real thing, planned by the real planner: an image change is a rebuild, which erases the boot
        // disk. Rebuild is not implemented, and this is the assertion that keeps it that way.
        var (scenario, provisioner, plan) = await PlannedAsync(size: LiveSize, image: "ubuntu-22-04-x64");

        plan.DataImpact.Should().Be(DataImpact.Destroyed);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("only an in-place resize");

        scenario.Api.Requests.Should().BeEmpty("a plan that would erase the disk is never sent");
    }

    [Fact]
    public async Task A_plan_that_does_not_preserve_data_is_refused_with_no_http_request()
    {
        // The data-impact guard on its own, isolated from the strategy guard that catches every rebuild the
        // real planner produces. Nothing that admits to destroying data reaches a provider call from here.
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan(
            [new PlannedChange("size", LiveSize, TargetSize, RequiresRecreate: false)],
            dataImpact: DataImpact.Destroyed);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("Destroyed");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_region_change_plan_is_refused_with_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(size: LiveSize, region: "sfo3");

        plan.Strategy.Should().Be(UpdateStrategy.Recreate);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_reports_no_change_is_refused_with_no_http_request()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(size: LiveSize);

        plan.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resize_bundled_with_another_change_is_refused_rather_than_partly_applied()
    {
        // Executing the half it understands and skipping the rest would report a half-applied update as an
        // applied one. The tag attach is not implemented, so the whole plan is declined.
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan(
        [
            new PlannedChange("size", LiveSize, TargetSize, RequiresRecreate: false),
            new PlannedChange("tag servyx.owner", null, "ops", RequiresRecreate: false),
        ]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("resize and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_size_change_naming_no_target_is_refused_with_no_http_request()
    {
        var scenario = new DigitalOceanScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([new PlannedChange("size", LiveSize, null, RequiresRecreate: false)]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Submission is not success: the three ends an action can reach, kept apart
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_still_in_progress_when_the_polls_are_spent_is_neither_success_nor_failure()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync(actionPollAttempts: 3);
        RouteResize(scenario, ["in-progress"]);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

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
        ActionReads(scenario).Should().Be(3);
    }

    [Fact]
    public async Task A_still_running_action_and_an_errored_action_are_different_types()
    {
        var (runningScenario, runningProvisioner, runningPlan) = await PlannedResizeAsync();
        RouteResize(runningScenario, ["in-progress"]);
        var running = await runningProvisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), runningPlan, runningPlan.PlanHash);

        var (erroredScenario, erroredProvisioner, erroredPlan) = await PlannedResizeAsync();
        RouteResize(erroredScenario, ["errored"]);
        var errored = await erroredProvisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), erroredPlan, erroredPlan.PlanHash);

        running.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        errored.Should().BeOfType<UpdateExecutionResult.Failed>();
        running.GetType().Should().NotBe(errored.GetType());
    }

    [Fact]
    public async Task An_errored_action_is_a_failure_carrying_the_providers_own_message()
    {
        const string ProviderMessage = "Droplet is currently locked by another action";

        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        RouteResize(scenario, ["in-progress", "errored"], actionMessage: ProviderMessage);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain(ProviderMessage);
    }

    [Fact]
    public async Task A_submission_the_provider_refuses_is_a_failure_carrying_its_message()
    {
        // The refusal DigitalOcean gives for a disk:false resize whose target needs a larger boot disk. That
        // refusal is the intended outcome, not a gap: the only way past it is the irreversible form.
        const string ProviderMessage = "the disk size of this droplet is too small for the requested size";

        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.UnprocessableEntity,
            "{\"id\":\"unprocessable_entity\",\"message\":\"" + ProviderMessage + "\"}");

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain(ProviderMessage);

        // One attempt, and no polling of an action that was never created.
        scenario.Api.Requests.Should().ContainSingle();
        ActionReads(scenario).Should().Be(0);
    }

    [Fact]
    public async Task A_completed_action_is_reported_only_after_the_poll_observes_it_completed()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync(actionPollAttempts: 5);
        RouteResize(scenario, ["in-progress", "in-progress", "completed"]);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;
        completed.Message.Should().Contain("completed after 3 check(s)");
        completed.Resource.Handle.ProviderResourceId.Should().Be(
            DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture));

        // Three reads: the success came from an observation, not from the submission.
        ActionReads(scenario).Should().Be(3);
    }

    [Fact]
    public async Task An_action_the_submission_already_calls_completed_is_still_not_trusted()
    {
        // DigitalOcean's POST response carries a status of its own. If that status were believed, this test
        // would report success; the poll is the only thing that decides, and here it never agrees.
        var (scenario, provisioner, plan) = await PlannedResizeAsync(actionPollAttempts: 2);
        RouteResize(scenario, ["in-progress"], submissionStatus: "completed");

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        ActionReads(scenario).Should().Be(2);
    }

    [Fact]
    public async Task A_completed_resize_hands_back_the_droplet_as_it_now_is()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        RouteResize(scenario, ["completed"], dropletSizeAfter: TargetSize);

        var result = await provisioner.ApplyUpdateAsync(
            DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;

        // Re-read after the action finished, so the resource describes the machine that exists now.
        scenario.Api.Requests
            .Count(r => r.Method == HttpMethod.Get
                && r.Uri.AbsolutePath.StartsWith("/v2/droplets/", StringComparison.Ordinal))
            .Should().Be(1);

        completed.Resource.ConnectorId.Should().Be(DigitalOceanScenario.ConnectorId);
        completed.Message.Should().Contain(TargetSize);
        completed.Message.Should().Contain("boot disk was not written to");
    }

    [Fact]
    public async Task Every_request_a_resize_makes_still_carries_a_freshly_resolved_bearer_token()
    {
        var (scenario, provisioner, plan) = await PlannedResizeAsync();
        scenario.Secrets.Resolved.Clear();
        RouteResize(scenario, ["completed"]);

        await provisioner.ApplyUpdateAsync(DigitalOceanScenario.RecordedHandle(), plan, plan.PlanHash);

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(
            r => r.Authorization == "Bearer " + DigitalOceanScenario.ApiToken);

        // One resolution per request, so nothing on this path caches the token.
        scenario.Secrets.Resolved.Should().HaveCount(scenario.Api.Requests.Count);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>Plans a real resize against the substituted API, then forgets the reads that planning made.</summary>
    private static Task<(DigitalOceanScenario Scenario, DigitalOceanDropletProvisioner Provisioner, UpdatePlan Plan)>
        PlannedResizeAsync(int actionPollAttempts = 3) =>
        PlannedAsync(size: TargetSize, actionPollAttempts: actionPollAttempts);

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
    /// A plan built by hand, for the shapes the real planner cannot currently produce — a plan belonging to
    /// another provisioner, a resize bundled with a tag attach, a size change naming no target.
    /// </summary>
    private static UpdatePlan HandBuiltPlan(
        IReadOnlyList<PlannedChange> changes,
        string planHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        string provisionerId = DigitalOceanDropletProvisioner.Id,
        UpdateStrategy strategy = UpdateStrategy.InPlace,
        DataImpact dataImpact = DataImpact.Preserved) =>
        new(
            planId: "test:update:1",
            planHash: planHash,
            provisionerId: provisionerId,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: [new ProvisioningStage("resize-droplet", provisionerId, "Resize the droplet.")],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>Makes any HTTP request at all fail the test where it happens.</summary>
    private static void FailOnAnyRequest(DigitalOceanScenario scenario) =>
        scenario.Api.Responder = request => throw new InvalidOperationException(
            $"A refused update issued a {request.Method} request to '{request.Uri}'. It must send nothing at all.");

    /// <summary>How many times the action endpoint was read.</summary>
    private static int ActionReads(DigitalOceanScenario scenario) =>
        scenario.Api.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/v2/actions/", StringComparison.Ordinal));

    /// <summary>
    /// Routes the resize exchange: one POST answering with an action, then action reads walking
    /// <paramref name="actionStatuses"/> (repeating the last one), then droplet reads.
    /// </summary>
    private static void RouteResize(
        DigitalOceanScenario scenario,
        IReadOnlyList<string> actionStatuses,
        string? actionMessage = null,
        string submissionStatus = "in-progress",
        string dropletSizeAfter = TargetSize)
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
                DigitalOceanScenario.DropletEnvelopeJson(sizeSlug: dropletSizeAfter));
        };
    }

    /// <summary>An <c>{ "action": ... }</c> envelope as DigitalOcean reports one.</summary>
    private static string ActionEnvelopeJson(long id, string status, string? message = null) =>
        "{\"action\":{\"id\":" + id.ToString(CultureInfo.InvariantCulture)
        + ",\"status\":\"" + status + "\""
        + ",\"type\":\"resize\""
        + ",\"resource_id\":" + DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture)
        + ",\"resource_type\":\"droplet\""
        + (message is null ? string.Empty : ",\"message\":\"" + message + "\"")
        + "}}";
}
