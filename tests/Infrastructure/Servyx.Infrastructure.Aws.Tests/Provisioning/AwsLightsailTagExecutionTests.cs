using System.Net;
using System.Text.Json.Nodes;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the Lightsail adapter: the one operation it will carry out, the
/// several it refuses, and — the assertions the rest of this file exists to protect — that a refusal issues no
/// mutating request, that a submitted retag is never mistaken for a finished one, and that the Servyx ownership
/// tags cannot be lost by the very operation that writes tags.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted AWS endpoint, so no network access, no AWS account and no IAM
/// credential beyond the fake key pair in the scenario is involved. The refusal tests assert on the request
/// list itself — for the guards that run before any HTTP, that it is <em>empty</em>; for the one guard that has
/// to read the instance first, that it contains reads and no <c>TagResource</c> — because the claim being made
/// is about Lightsail's state and not about this process's. Each of those tests also installs a responder that
/// throws on any request at all, so an assertion of emptiness cannot pass by accident.
/// </para>
/// <para>
/// <strong>Why "no mutating request" is asserted by action name here.</strong> The EC2 suite can lean on the
/// HTTP verb, because the Query API sends reads as GETs. Lightsail speaks AWS JSON 1.1, in which <em>every</em>
/// call is a <c>POST /</c> and the action is named by an <c>X-Amz-Target</c> header — so a verb check would
/// prove nothing. What is asserted is the action name, which is the only thing distinguishing
/// <c>GetInstance</c> from <c>TagResource</c> on this wire.
/// </para>
/// <para>
/// <strong>The finding this suite pins.</strong> A tag change is not merely the first in-place operation this
/// adapter implements — it is the only one there can be. AWS publishes no operation that changes an existing
/// Lightsail instance's bundle, region or zone, and the blueprint is fixed at create time. So the bundle and
/// blueprint refusals below are permanent facts about Lightsail, not a backlog.
/// </para>
/// </remarks>
public class AwsLightsailTagExecutionTests
{
    /// <summary>The extra tag these tests ask for. Not a canonical ownership key, deliberately.</summary>
    private const string ExtraTagKey = "servyx.env";

    /// <summary>The value the extra tag is written to.</summary>
    private const string ExtraTagValue = "prod";

    /// <summary>A bundle other than the one the scenario's instance runs, for the bundle-change refusal.</summary>
    private const string OtherBundle = "large_3_0";

    /// <summary>A blueprint other than the scenario's, for the blueprint-change refusal.</summary>
    private const string OtherBlueprint = "ubuntu_22_04";

    /// <summary>Every Lightsail action that changes something. A refusal may produce none of these.</summary>
    private static readonly string[] MutatingActionNames =
    [
        "CreateInstances",
        "DeleteInstance",
        "TagResource",
        "UntagResource",
        "CreateInstanceSnapshot",
        "CreateInstancesFromSnapshot",
        "PutInstancePublicPorts",
    ];

    // -------------------------------------------------------------------------------------------------
    // The adapter is an update applier at all, and says so honestly
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_an_update_applier_and_the_two_ids_agree()
    {
        var provisioner = new LightsailScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IUpdateApplier>();
        ((IUpdateApplier)provisioner).ProvisionerId.Should().Be(AwsLightsailProvisioner.Id);
    }

    [Fact]
    public void Executing_a_tag_change_does_not_make_the_adapter_claim_the_resize_capability()
    {
        // The capability set is unchanged by this file: a retag is UpdateInPlace, which was already claimed and
        // is now actually backed. It is not a resize, and Lightsail has no resize for an instance to implement.
        var capabilities = new LightsailScenario().Provisioner().Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.UpdateInPlace);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.Resize);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.Snapshot);
    }

    // -------------------------------------------------------------------------------------------------
    // The one operation it performs: one call, then the effect is read back
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_change_is_applied_and_reports_completed()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();
        MutatingActions(scenario).Should().Equal("TagResource");
    }

    [Fact]
    public async Task The_tag_request_carries_the_requested_tag_and_names_the_instance()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario);

        await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var tag = scenario.Api.Requests.Single(r => r.LightsailAction == "TagResource");
        var body = JsonNode.Parse(tag.Body!)!.AsObject();

        body["resourceName"]!.GetValue<string>().Should().Be(LightsailScenario.InstanceName);
        TagsOf(tag).Should().Contain(new KeyValuePair<string, string>(ExtraTagKey, ExtraTagValue));
    }

    [Fact]
    public async Task The_tag_request_carries_every_canonical_ownership_tag_at_its_live_value()
    {
        // The structural guarantee on the wire. TagResource adds or overwrites the keys named in the request, so
        // naming all four canonical keys at the values the live instance already has makes the write idempotent
        // in the ownership marks - after it, the instance provably still carries them.
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario);

        await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var written = TagsOf(scenario.Api.Requests.Single(r => r.LightsailAction == "TagResource"));

        foreach (var canonical in LightsailScenario.CanonicalTags)
        {
            written.Should().Contain(canonical);
        }
    }

    [Fact]
    public async Task The_canonical_ownership_tags_are_present_on_the_instance_after_the_change()
    {
        // The observation, not the promise: the tags are read off the instance the substituted API reports
        // *after* the retag, which is the same thing the adapter itself reads before it will say Completed.
        var (scenario, provisioner, plan) = await PlannedAsync();
        var live = RouteTagChange(scenario);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        foreach (var canonical in LightsailScenario.CanonicalTags)
        {
            live.Should().Contain(canonical);
        }

        live.Should().Contain(new KeyValuePair<string, string>(ExtraTagKey, ExtraTagValue));
    }

    [Fact]
    public async Task The_completed_message_names_the_ownership_tags_it_read_back_and_says_the_machine_was_untouched()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var message = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which.Message;

        foreach (var key in ServyxTagKeys.Canonical)
        {
            message.Should().Contain(key);
        }

        message.Should().Contain("observed");
        message.Should().Contain("not stopped, restarted or otherwise touched");
    }

    [Fact]
    public async Task Nothing_on_the_whole_path_deletes_creates_or_untags_anything()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario);

        await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        scenario.Api.Requests.Should().NotBeEmpty();
        MutatingActions(scenario).Should().NotContain("DeleteInstance");
        MutatingActions(scenario).Should().NotContain("CreateInstances");
        MutatingActions(scenario).Should().NotContain("UntagResource");
    }

    [Fact]
    public async Task The_retag_is_confirmed_by_reading_the_instance_back_not_by_trusting_the_accepted_response()
    {
        // TagResource answers 200 with a non-terminal 'Started' operation. If the adapter treated that as
        // success there would be no GetInstance after the write at all; there are, and the first of them is
        // where the tags first appear.
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario, visibleAfterPolls: 2);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        var actions = scenario.Api.Requests.Select(r => r.LightsailAction).ToList();
        actions.IndexOf("TagResource").Should().BeGreaterThan(-1);
        actions.Skip(actions.IndexOf("TagResource") + 1).Should().OnlyContain(a => a == "GetInstance");
        actions.Count(a => a == "GetInstance").Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task A_retag_never_observed_on_the_instance_is_a_timeout_and_not_a_success()
    {
        // Submission is not success. The write was accepted and its effect was never seen, which is neither a
        // failure nor a completion - retrying a failure is right, retrying something still in flight is not.
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario, visibleAfterPolls: 99);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.TimedOut>()
            .Which.Message.Should().Contain("never observed taking effect");
    }

    [Fact]
    public async Task An_operation_lightsail_reports_as_Failed_is_a_failure_even_though_it_arrived_with_a_200()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(
            scenario,
            tagResponse: LightsailScenario.TagResourceJson(
                status: "Failed",
                isTerminal: true,
                errorCode: "InvalidTag",
                errorDetails: "The tag value is not valid."));

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var message = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which.Message;
        message.Should().Contain("InvalidTag");
        message.Should().Contain("did not arrive as an error status");
    }

    [Fact]
    public async Task A_provider_refusal_of_the_write_is_a_failure_that_says_the_machine_is_untouched()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTagChange(scenario, tagStatus: HttpStatusCode.BadRequest);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain("it is still running and nothing was interrupted");
    }

    // -------------------------------------------------------------------------------------------------
    // The ownership tags cannot be dropped or overwritten - a check, and two structural properties
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_that_would_overwrite_a_canonical_ownership_tag_is_refused_with_no_request_at_all()
    {
        // A genuinely planner-produced canonical change: the live instance carries a different job id, so
        // BuildUpdatePlan emits 'tag servyx.job-id' as an ordinary in-place, data-preserving tag change. It is
        // still refused, because rewriting an ownership mark is how an instance stops being findable by the
        // orphan sweep while it keeps billing.
        var drifted = new Dictionary<string, string>(LightsailScenario.CanonicalTags, StringComparer.Ordinal)
        {
            [ServyxTagKeys.JobId] = "job-OLD",
        };

        var (scenario, provisioner, plan) = await PlannedAsync(liveTags: drifted);

        plan.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Changes.Should().Contain(c => c.Aspect == $"tag {ServyxTagKeys.JobId}");

        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("Servyx ownership tag");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ServyxTagKeys.Managed)]
    [InlineData(ServyxTagKeys.InstanceId)]
    [InlineData(ServyxTagKeys.JobId)]
    [InlineData(ServyxTagKeys.ConnectorId)]
    public async Task A_hand_built_plan_dropping_any_ownership_tag_is_refused_with_no_request_at_all(string key)
    {
        // Desired null is a removal, which would be UntagResource. Both the canonical check and the
        // removal check refuse it, and neither lets a request out.
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt([new PlannedChange($"tag {key}", "true", null, RequiresRecreate: false)]);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hand_built_plan_dropping_an_ordinary_tag_is_refused_because_untagging_is_not_implemented()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt([new PlannedChange($"tag {ExtraTagKey}", ExtraTagValue, null, RequiresRecreate: false)]);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("UntagResource");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void The_tag_set_the_executor_builds_cannot_have_a_canonical_key_overwritten_by_anything()
    {
        // The structural backstop, asserted at the exact call the executor makes: the identity is read off the
        // live instance and the requested tags are passed as extras, and ServyxTagKeys.Build writes the
        // canonical keys LAST. No hostile input can win that ordering, so even with the check above removed the
        // request could not carry a rewritten ownership mark.
        var identity = ServyxLightsailTags.FromTags(LightsailScenario.CanonicalTags);
        identity.Should().NotBeNull();

        var hostile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "false",
            [ServyxTagKeys.InstanceId] = "srv-9999",
            [ServyxTagKeys.JobId] = "job-9999",
            [ServyxTagKeys.ConnectorId] = "conn-9999",
            [ExtraTagKey] = ExtraTagValue,
        };

        var built = identity!.ToTags(hostile);

        foreach (var canonical in LightsailScenario.CanonicalTags)
        {
            built.Should().Contain(canonical);
        }

        built.Should().Contain(new KeyValuePair<string, string>(ExtraTagKey, ExtraTagValue));
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals decided before any HTTP. Every one of these asserts a request count of zero.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_issues_no_request_at_all()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            plan,
            approvedPlanHash: "0000000000000000000000000000000000000000000000000000000000000000");

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not the plan that was approved");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bundle_change_is_refused_and_issues_no_request_at_all()
    {
        // Lightsail publishes no operation that changes an existing instance's bundle at all, so this refusal
        // is permanent rather than pending an implementation.
        var (scenario, provisioner, plan) = await PlannedAsync(size: OtherBundle);

        plan.Changes.Should().Contain(c => c.Aspect == "size");
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bundle_change_that_misdeclares_itself_as_in_place_and_preserving_is_still_refused()
    {
        // The plan's own strategy and impact are not the last line of defence: the aspect check is, and it does
        // not care what the plan calls itself.
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt([new PlannedChange("size", LightsailScenario.BundleId, OtherBundle, RequiresRecreate: false)]);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("no operation that changes an existing Lightsail instance's bundle");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blueprint_change_is_refused_and_issues_no_request_at_all()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherBlueprint });

        plan.Changes.Should().Contain(c => c.Aspect == "image");
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blueprint_change_that_misdeclares_itself_as_in_place_and_preserving_is_still_refused()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt(
            [new PlannedChange("image", LightsailScenario.BlueprintId, OtherBlueprint, RequiresRecreate: false)]);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not tag changes");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tag_change_bundled_with_a_blueprint_change_is_refused_rather_than_half_applied()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt(
            [
                new PlannedChange($"tag {ExtraTagKey}", null, ExtraTagValue, RequiresRecreate: false),
                new PlannedChange("image", LightsailScenario.BlueprintId, OtherBlueprint, RequiresRecreate: false),
            ]);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("half-applied");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DataImpact.Destroyed)]
    [InlineData(DataImpact.AtRisk)]
    public async Task A_non_preserved_data_impact_is_refused_and_issues_no_request_at_all(DataImpact impact)
    {
        // Redundant with the aspect check for every plan this adapter produces, and kept anyway: the impact is
        // one of the two properties the person approving the plan actually read.
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt(
            [new PlannedChange($"tag {ExtraTagKey}", null, ExtraTagValue, RequiresRecreate: false)],
            impact: impact);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("impact on persistent data");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recreate_strategy_is_refused_and_issues_no_request_at_all()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt(
            [new PlannedChange($"tag {ExtraTagKey}", null, ExtraTagValue, RequiresRecreate: true)],
            strategy: UpdateStrategy.Recreate,
            impact: DataImpact.Destroyed);

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("executes only an in-place tag change");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_and_issues_no_request_at_all()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt(
            [new PlannedChange($"tag {ExtraTagKey}", null, ExtraTagValue, RequiresRecreate: false)],
            provisionerId: "aws-ec2");

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("aws-ec2");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_belonging_to_another_provisioner_is_refused_and_issues_no_request_at_all()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt([new PlannedChange($"tag {ExtraTagKey}", null, ExtraTagValue, RequiresRecreate: false)]);

        var result = await provisioner.ApplyUpdateAsync(
            LightsailScenario.MaintenanceHandle(provisionerId: "aws-ec2"),
            plan,
            plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_carrying_no_instance_name_is_refused_and_issues_no_request_at_all()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();
        FailOnAnyRequest(scenario);

        var plan = HandBuilt([new PlannedChange($"tag {ExtraTagKey}", null, ExtraTagValue, RequiresRecreate: false)]);

        var result = await provisioner.ApplyUpdateAsync(
            LightsailScenario.MaintenanceHandle(providerResourceId: "   "),
            plan,
            plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("no Lightsail instance name");
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // The one guard that needs the network - it reads, and then still writes nothing
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_instance_lightsail_no_longer_knows_is_refused_and_nothing_is_written()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        scenario.RouteMissingInstance();

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("no longer has an instance named");

        scenario.Api.Requests.Should().OnlyContain(r => r.LightsailAction == "GetInstance");
        MutatingActions(scenario).Should().BeEmpty();
    }

    [Fact]
    public async Task An_instance_missing_its_ownership_tags_is_refused_and_nothing_is_written()
    {
        // There is no live identity to preserve, so the canonical keys would have to come from the plan - which
        // is exactly how an instance gets attributed to the wrong server, or to none at all.
        var (scenario, provisioner, plan) = await PlannedAsync();

        scenario.RouteReadOnly(LightsailScenario.InstanceJson(
            tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "someone-else" }));

        var result = await provisioner.ApplyUpdateAsync(LightsailScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("complete set of Servyx ownership tags");

        scenario.Api.Requests.Should().OnlyContain(r => r.LightsailAction == "GetInstance");
        MutatingActions(scenario).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>The mutating Lightsail actions a scenario recorded, in order.</summary>
    private static List<string> MutatingActions(LightsailScenario scenario) =>
        scenario.Api.Requests
            .Select(r => r.LightsailAction)
            .Where(a => a is not null && MutatingActionNames.Contains(a, StringComparer.Ordinal))
            .Select(a => a!)
            .ToList();

    /// <summary>Makes any request at all fail the test, so an assertion of "no requests" cannot pass by luck.</summary>
    private static void FailOnAnyRequest(LightsailScenario scenario) =>
        scenario.Api.Responder = request => throw new InvalidOperationException(
            $"A refusal path issued a Lightsail request: '{request.LightsailAction}'.");

    /// <summary>The tags a recorded <c>TagResource</c> request carries, decoded from its JSON body.</summary>
    private static IReadOnlyDictionary<string, string> TagsOf(RecordedRequest request)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in JsonNode.Parse(request.Body!)!["tags"]!.AsArray())
        {
            tags[node!["key"]!.GetValue<string>()] = node["value"]!.GetValue<string>();
        }

        return tags;
    }

    /// <summary>
    /// Plans a real update against the substituted endpoint and hands back the scenario, the provisioner and
    /// the plan — with the request log cleared, so a later assertion of "no request" means the apply made none.
    /// </summary>
    private static async Task<(LightsailScenario Scenario, AwsLightsailProvisioner Provisioner, UpdatePlan Plan)> PlannedAsync(
        IReadOnlyDictionary<string, string>? overrides = null,
        string? size = LightsailScenario.BundleId,
        IReadOnlyDictionary<string, string>? liveTags = null)
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly(LightsailScenario.InstanceJson(tags: liveTags));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"tag:{ExtraTagKey}"] = ExtraTagValue,
        };

        foreach (var pair in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            parameters[pair.Key] = pair.Value;
        }

        var provisioner = scenario.Provisioner();

        var plan = await provisioner.PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(parameters, size));

        plan.Should().NotBeNull();
        scenario.Api.Requests.Clear();

        return (scenario, provisioner, plan!);
    }

    /// <summary>
    /// Routes the retag exchange: reads answer with the instance's current tags, the one <c>TagResource</c>
    /// stashes what it was sent, and the tags become visible on the instance after
    /// <paramref name="visibleAfterPolls"/> further reads.
    /// </summary>
    /// <returns>
    /// The live tag dictionary the substituted Lightsail holds, so a test can assert on the state the provider
    /// is left in rather than only on what was sent.
    /// </returns>
    private static IReadOnlyDictionary<string, string> RouteTagChange(
        LightsailScenario scenario,
        int visibleAfterPolls = 1,
        string? tagResponse = null,
        HttpStatusCode tagStatus = HttpStatusCode.OK,
        IReadOnlyDictionary<string, string>? liveTags = null)
    {
        var current = new Dictionary<string, string>(
            liveTags ?? LightsailScenario.CanonicalTags,
            StringComparer.Ordinal);

        Dictionary<string, string>? pending = null;
        var pollsSinceTag = 0;

        scenario.Api.Responder = request =>
        {
            switch (request.LightsailAction)
            {
                case "GetInstance":
                    if (pending is not null && ++pollsSinceTag >= visibleAfterPolls)
                    {
                        foreach (var pair in pending)
                        {
                            current[pair.Key] = pair.Value;
                        }

                        pending = null;
                    }

                    return AwsApiDouble.Json(
                        HttpStatusCode.OK,
                        LightsailScenario.GetInstanceJson(LightsailScenario.InstanceJson(
                            tags: new Dictionary<string, string>(current, StringComparer.Ordinal))));

                case "TagResource":
                    if (tagStatus != HttpStatusCode.OK)
                    {
                        return AwsApiDouble.Json(
                            tagStatus,
                            LightsailScenario.ErrorJson("InvalidInputException", "The tag set was rejected."));
                    }

                    pending = new Dictionary<string, string>(TagsOf(request), StringComparer.Ordinal);
                    pollsSinceTag = 0;
                    return AwsApiDouble.Json(HttpStatusCode.OK, tagResponse ?? LightsailScenario.TagResourceJson());

                default:
                    throw new InvalidOperationException(
                        $"Unexpected Lightsail action '{request.LightsailAction}' during a retag.");
            }
        };

        return current;
    }

    /// <summary>
    /// A plan built by hand rather than by the planner, so a test can present a combination the planner would
    /// never produce — the point being that this file's guards do not depend on the planner's honesty.
    /// </summary>
    private static UpdatePlan HandBuilt(
        IReadOnlyList<PlannedChange> changes,
        UpdateStrategy strategy = UpdateStrategy.InPlace,
        DataImpact impact = DataImpact.Preserved,
        string provisionerId = AwsLightsailProvisioner.Id) =>
        new(
            planId: "hand-built",
            planHash: "1111111111111111111111111111111111111111111111111111111111111111",
            provisionerId: provisionerId,
            strategy: strategy,
            dataImpact: impact,
            changes: changes,
            stages: [],
            expiresAt: DateTimeOffset.UnixEpoch.AddYears(100));
}
