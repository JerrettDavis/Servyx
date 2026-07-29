using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// What AWS Cloud Map service discovery does and does not give a Fargate deployment, pinned end to end: the plan
/// that names it, the create that registers it, the cost line that includes it, the destroy that removes it, and
/// — the point of all of it — the control address that decides whether the resource can be operated at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The finding these tests exist to protect is uncomfortable and easy to lose.</strong> Cloud Map really
/// does give an ECS service a DNS name that survives every task replacement, which is what
/// <see cref="ControlChannelAddress.Durable"/> asks for. It does not give that name a routable address: AWS
/// registers the task's <em>private</em> IPv4 into the record, explicitly and even in a public namespace. So a
/// durable name and a usable one are different things here, and an adapter that conflated them would hand the
/// control channel an endpoint that is correctly configured and times out. Several assertions below exist purely
/// to make that conflation fail.
/// </para>
/// <para>
/// <strong>Nothing here opens a socket.</strong> Every AWS call goes through <see cref="AwsApiDouble"/>, and the
/// tests that claim no request was made prove it by asserting <see cref="AwsApiDouble.Requests"/> is empty.
/// </para>
/// </remarks>
public class AwsEcsFargateServiceDiscoveryTests
{
    private static string Stage(ProvisioningPlan plan, string stageId) =>
        plan.Stages.Single(s => string.Equals(s.StageId, stageId, StringComparison.Ordinal)).Description;

    private static bool HasStage(ProvisioningPlan plan, string stageId) =>
        plan.Stages.Any(s => string.Equals(s.StageId, stageId, StringComparison.Ordinal));

    private static ProvisioningPlan Plan(EcsScenario scenario, AwsFargateServiceDiscovery? discovery)
    {
        var provisioner = scenario.Provisioner(serviceDiscovery: discovery);
        return provisioner.BuildPlan(provisioner.BuildSpec(EcsScenario.PalworldRequest()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The configuration value object.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_namespace_id_that_is_not_a_namespace_id_is_refused_before_any_plan_exists()
    {
        // Checked at construction rather than at CreateService, for the same reason EfsVolumeMount checks its
        // file system id: by the time AWS refuses it, somebody has already approved a plan.
        var act = () => new AwsFargateServiceDiscovery("servyx.local");

        act.Should().Throw<ArgumentException>().WithMessage("*not an AWS Cloud Map namespace id*");
    }

    [Fact]
    public void A_namespace_arn_is_accepted_because_a_shared_namespace_is_named_by_one()
    {
        var discovery = new AwsFargateServiceDiscovery(
            "arn:aws:servicediscovery:us-east-1:111122223333:namespace/ns-0123456789abcdef");

        discovery.NamespaceId.Should().StartWith("arn:");
    }

    [Fact]
    public void A_blank_reachability_attestation_is_refused_because_null_is_how_you_say_no()
    {
        // The difference matters: null means "no route has been claimed" and produces a refusal with a reason,
        // while an empty string would put a blank justification on a Durable address somebody is about to open a
        // control channel on.
        var act = () => new AwsFargateServiceDiscovery(EcsScenario.NamespaceId, "   ");

        act.Should().Throw<ArgumentException>().WithMessage("*must say something*");
    }

    [Fact]
    public void No_attestation_is_the_default_and_means_the_control_plane_cannot_reach_the_vpc()
    {
        var discovery = new AwsFargateServiceDiscovery(EcsScenario.NamespaceId);

        discovery.ControlPlaneVpcAccess.Should().BeNull();
        discovery.ControlPlaneCanReachVpc.Should().BeFalse();
    }

    [Fact]
    public void A_provisioner_says_whether_it_can_ever_produce_an_operable_resource()
    {
        var scenario = new EcsScenario();

        scenario.Provisioner().ServiceDiscovery.Should().BeNull();
        scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery()).ServiceDiscovery.Should().NotBeNull();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Planning.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Planning_a_registered_deployment_still_issues_no_request_and_resolves_no_secret()
    {
        // The rule does not bend for a second AWS service. A plan is pure computation over the request, cost
        // figure included, which is why AwsCloudMapPricing is a static snapshot exactly as AwsFargatePricing is.
        var scenario = new EcsScenario();

        Plan(scenario, EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess));

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_issues_no_request_either()
    {
        var scenario = new EcsScenario();
        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());

        await provisioner.PlanAsync(EcsScenario.PalworldRequest());

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public void The_namespace_is_named_as_required_and_the_refusal_to_create_one_is_argued()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var stage = Stage(plan, "require-cloud-map-namespace");

        stage.Should().Contain("REQUIRES (does not create)");
        stage.Should().Contain(EcsScenario.NamespaceId);
        stage.Should().Contain("Route 53 private hosted zone");
        stage.Should().Contain("bills every month", "an orphan that costs nothing is a different argument");
        stage.Should().Contain("will not manufacture a second");
    }

    [Fact]
    public void The_cloud_map_service_is_named_as_servyx_s_own_to_create_and_destroy()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var stage = Stage(plan, "create-cloud-map-service");

        stage.Should().Contain("CREATED AND DESTROYED BY SERVYX");
        stage.Should().Contain(EcsScenario.ServiceName);
        stage.Should().Contain(EcsScenario.NamespaceId);
        stage.Should().Contain("HealthCheckCustomConfig");
        stage.Should().Contain("never exists untagged");
    }

    [Fact]
    public void The_plan_says_registration_is_ecs_s_work_and_therefore_has_no_gap()
    {
        // The single best property of this mechanism: because serviceRegistries travels in the call that creates
        // the ECS service, there is no moment at which a running task exists and is not registered.
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var stage = Stage(plan, "register-task-in-service-discovery");

        stage.Should().Contain("NO SEPARATE CALL IS MADE");
        stage.Should().Contain("serviceRegistries");
        stage.Should().Contain("no window in which a running task exists and is not registered");
        stage.Should().Contain("no RegisterInstance");
    }

    [Fact]
    public void The_plan_says_out_loud_that_the_durable_name_resolves_to_a_private_address()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess));

        var stage = Stage(plan, "discovery-name-resolves-privately");

        stage.Should().Contain("THE NAME IS DURABLE AND THE ADDRESS BEHIND IT IS PRIVATE");
        stage.Should().Contain("PRIVATE IPv4");
        stage.Should().Contain("even when the namespace is a public one");
        stage.Should().Contain("assignPublicIp changes nothing");
    }

    [Fact]
    public void Without_an_attestation_the_plan_says_plainly_that_nothing_will_be_operable()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var stage = Stage(plan, "control-channel-address");

        stage.Should().Contain("NO CONTROL CHANNEL WILL BE OPENED");
        stage.Should().Contain("even though a durable name will exist");
        stage.Should().Contain("controlPlaneVpcAccess");
    }

    [Fact]
    public void With_an_attestation_the_plan_promises_a_channel_and_attributes_the_claim()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess));

        var stage = Stage(plan, "control-channel-address");

        stage.Should().Contain("CONTROL CHANNEL WILL BE AVAILABLE");
        stage.Should().Contain(EcsScenario.ControlPlaneVpcAccess, "the operator's own words are the evidence");
        stage.Should().Contain("Servyx cannot verify that");
        stage.Should().Contain("Operate tier and no higher");
    }

    [Fact]
    public void The_plan_says_how_the_registration_is_cleaned_up_and_what_is_left_alone()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var stage = Stage(plan, "destroy-deletes-cloud-map-service");

        stage.Should().Contain("does NOT delete the namespace");
        stage.Should().Contain("confirmed they are Servyx's");
        stage.Should().Contain("FAILS LOUDLY");
    }

    [Fact]
    public void Discovery_replaces_the_no_stable_address_stage_rather_than_sitting_beside_it()
    {
        // Leaving both in would tell an operator two contradictory things on one screen.
        var scenario = new EcsScenario();

        var without = Plan(scenario, discovery: null);
        var with = Plan(scenario, EcsScenario.Discovery());

        HasStage(without, "no-stable-address").Should().BeTrue();
        HasStage(without, "create-cloud-map-service").Should().BeFalse();

        HasStage(with, "no-stable-address").Should().BeFalse();
        HasStage(with, "create-cloud-map-service").Should().BeTrue();
    }

    [Fact]
    public void The_registration_is_planned_before_the_billable_ecs_service()
    {
        // A plan is read as a sequence. Showing the Cloud Map create after the ECS create would tell a reader
        // the wrong thing about what a partial failure leaves behind.
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var ids = plan.Stages.Select(s => s.StageId).ToList();

        ids.IndexOf("require-cloud-map-namespace").Should().BeLessThan(ids.IndexOf("register-task-definition"));
        ids.IndexOf("create-cloud-map-service").Should().BeLessThan(ids.IndexOf("create-service"));
        ids.IndexOf("register-task-in-service-discovery").Should().BeLessThan(ids.IndexOf("await-running-task"));
    }

    [Fact]
    public void The_handoff_is_still_a_resource_no_transport_can_reach()
    {
        // A durable control address does not make the resource reachable and must never be read as doing so.
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess));

        Stage(plan, "handoff-unreachable").Should().Contain("WITH NO TRANSPORT TARGET");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Cost.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cloud_map_registration_is_folded_into_the_estimate_rather_than_footnoted()
    {
        var scenario = new EcsScenario();

        var without = Plan(scenario, discovery: null).EstimatedCost;
        var with = Plan(scenario, EcsScenario.Discovery()).EstimatedCost;

        with.Monthly.Should().Be(without.Monthly + AwsCloudMapPricing.PerRegisteredResourcePerMonth);
        with.Confidence.Should().Be(CostConfidence.ListPrice);
        with.Currency.Should().Be(without.Currency);
    }

    [Fact]
    public void The_folded_estimate_still_says_what_it_does_not_include()
    {
        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        var source = plan.EstimatedCost.Source;

        source.Should().Contain("COMPUTE ONLY", "the Fargate caveats survive the fold");
        source.Should().Contain("0.10 USD per registered resource per month");
        source.Should().Contain("Route 53 hosted zone");
        source.Should().Contain("DiscoverInstances API charge does not apply");
    }

    [Fact]
    public void An_unknown_compute_figure_is_not_turned_into_a_known_partial_total()
    {
        // Adding a known number to an unknown one produces something that looks like a total and is not.
        var unknown = CostEstimate.Unknown("no reservation was reported.");

        var folded = AwsCloudMapPricing.Fold(unknown);

        folded.Monthly.Should().BeNull();
        folded.Hourly.Should().BeNull();
        folded.Confidence.Should().Be(CostConfidence.Unknown);
        folded.Source.Should().Contain("a partial total would read as a complete one");
    }

    [Fact]
    public void The_plan_hash_covers_the_registration()
    {
        var scenario = new EcsScenario();

        var without = Plan(scenario, discovery: null).PlanHash;
        var with = Plan(scenario, EcsScenario.Discovery()).PlanHash;
        var otherNamespace = Plan(scenario, EcsScenario.Discovery(namespaceId: "ns-ffffffffffffffff")).PlanHash;

        with.Should().NotBe(without);
        otherNamespace.Should().NotBe(with);
    }

    [Fact]
    public void The_reachability_attestation_is_deliberately_not_part_of_the_plan_hash()
    {
        // It changes what Servyx will offer a control channel, not what gets created at AWS. A plan invalidated
        // because somebody wrote a better sentence about their VPC peering would be a plan invalidated by prose.
        var scenario = new EcsScenario();

        var silent = Plan(scenario, EcsScenario.Discovery()).PlanHash;
        var attested = Plan(scenario, EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess)).PlanHash;

        attested.Should().Be(silent);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Creating.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cloud_map_service_is_created_after_the_free_write_and_before_the_billable_one()
    {
        // A Cloud Map service with no instances registered costs nothing, so a create that fails at the next
        // step leaves a free object rather than a running task. And the ECS service cannot name a registry that
        // does not exist yet, so the order is forced as well as preferable.
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        await provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest())).CreateAsync();

        var order = scenario.Api.Requests
            .Select(r => (r.IsServiceDiscovery ? "cloudmap:" : "ecs:") + r.EcsAction)
            .ToList();

        order.Should().StartWith(
            ["ecs:RegisterTaskDefinition", "cloudmap:CreateService", "ecs:CreateService"]);
    }

    [Fact]
    public async Task The_cloud_map_create_carries_every_servyx_tag_in_the_same_call()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        await provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest())).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.IsServiceDiscovery).Body;

        body.Should().NotBeNull();
        // Cloud Map capitalises its tag members, which is exactly the near-miss that would leave a resource
        // reading as untagged and therefore as somebody else's.
        body.Should().Contain("\"Key\":\"servyx.managed\"");
        body.Should().Contain("\"Value\":\"true\"");
        body.Should().Contain("\"Key\":\"servyx.instance-id\"");
        body.Should().Contain(EcsScenario.InstanceId);
        body.Should().Contain("\"Value\":\"cloud-map-service\"", "the role distinguishes it from the ECS service");
    }

    [Fact]
    public async Task The_cloud_map_create_asks_for_an_A_record_and_an_ecs_managed_health_check()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        await provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest())).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.IsServiceDiscovery).Body;

        body.Should().Contain("\"Type\":\"A\"");
        body.Should().Contain("\"RoutingPolicy\":\"MULTIVALUE\"");
        body.Should().Contain("\"HealthCheckCustomConfig\"");
        body.Should().NotContain("\"HealthCheckConfig\"", "a Route 53 health check bills and cannot reach a private address");
        body.Should().Contain("\"NamespaceId\":\"" + EcsScenario.NamespaceId + "\"");
    }

    [Fact]
    public async Task The_cloud_map_create_is_idempotent_across_a_retry()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        var spec = provisioner.BuildSpec(EcsScenario.PalworldRequest());

        await provisioner.CreateOperation(spec).CreateAsync();
        await provisioner.CreateOperation(spec).CreateAsync();

        var bodies = scenario.Api.Requests.Where(r => r.IsServiceDiscovery).Select(r => r.Body).ToList();

        bodies.Should().HaveCount(2);
        bodies[0].Should().Be(bodies[1], "a deterministic CreatorRequestId is what makes a retry a retry");
        bodies[0].Should().Contain("\"CreatorRequestId\":\"servyx-");
    }

    [Fact]
    public async Task The_ecs_service_names_the_registry_it_was_handed()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        await provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest())).CreateAsync();

        var body = scenario.Api.Requests
            .Last(r => !r.IsServiceDiscovery && r.EcsAction == "CreateService")
            .Body;

        body.Should().Contain("\"serviceRegistries\"");
        body.Should().Contain(EcsScenario.CloudMapServiceArn);
        // No port, containerName or containerPort: ECS needs those for SRV records, and Servyx registers an A.
        body.Should().NotContain("\"containerPort\":25575");
    }

    [Fact]
    public async Task Nothing_registers_an_instance_and_nothing_creates_a_namespace()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        await provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest())).CreateAsync();

        var actions = scenario.Api.Requests.Where(r => r.IsServiceDiscovery).Select(r => r.CloudMapAction).ToList();

        actions.Should().ContainSingle().Which.Should().Be("CreateService");
    }

    [Fact]
    public async Task A_provisioner_without_discovery_makes_no_servicediscovery_request_at_all()
    {
        var scenario = new EcsScenario();

        await scenario.CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().AllSatisfy(r => r.IsServiceDiscovery.Should().BeFalse());
    }

    [Fact]
    public async Task A_provisioner_without_discovery_writes_the_same_ecs_body_as_before()
    {
        var scenario = new EcsScenario();

        await scenario.CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body;

        body.Should().NotContain("serviceRegistries", "an unregistered service's body must be untouched");
        body.Should().NotContain("cloud-map");
    }

    [Fact]
    public async Task The_service_records_pointers_to_the_namespace_and_the_registration()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        var operation = provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest()));

        // Read before the create runs, because that is when the executor commits them to the write-ahead ledger.
        operation.Tags.Should().ContainKey("servyx.aws-cloud-map-namespace")
            .WhoseValue.Should().Be(EcsScenario.NamespaceId);
        operation.Tags.Should().ContainKey("servyx.aws-cloud-map-service")
            .WhoseValue.Should().Be(EcsScenario.ServiceName);

        await operation.CreateAsync();
    }

    [Fact]
    public void An_unregistered_service_carries_no_discovery_pointer_tags()
    {
        var scenario = new EcsScenario();
        var provisioner = scenario.Provisioner();

        var operation = provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest()));

        operation.Tags.Should().NotContainKey("servyx.aws-cloud-map-namespace");
        operation.Tags.Should().NotContainKey("servyx.aws-cloud-map-service");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Compensating.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compensation_deletes_the_registration_this_operation_created()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        var operation = provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest()));
        await operation.CreateAsync();

        scenario.Api.Requests.Clear();
        scenario.Api.Responder = request => request.IsServiceDiscovery
            ? AwsApiDouble.Json(HttpStatusCode.OK, "{}")
            : request.EcsAction switch
            {
                "DescribeServices" => AwsApiDouble.Json(
                    HttpStatusCode.OK,
                    EcsScenario.DescribeServicesJson(
                        EcsScenario.ServiceJson(tags: EcsScenario.DiscoveryTags))),
                _ => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.ServiceEnvelopeJson()),
            };

        await operation.CompensateAsync();

        scenario.Api.Requests
            .Where(r => r.IsServiceDiscovery)
            .Select(r => r.CloudMapAction)
            .Should()
            .ContainSingle().Which.Should().Be("DeleteService");
    }

    [Fact]
    public async Task Compensation_never_created_a_registration_so_it_never_deletes_one()
    {
        var scenario = new EcsScenario();
        scenario.Api.Responder = request => request.IsServiceDiscovery
            ? throw new InvalidOperationException("Nothing was created at Cloud Map, so nothing may be deleted there.")
            : AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.MissingServiceJson(EcsScenario.ServiceName));

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        var operation = provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest()));

        await operation.CompensateAsync();

        scenario.Api.Requests.Should().AllSatisfy(r => r.IsServiceDiscovery.Should().BeFalse());
    }

    [Fact]
    public async Task Compensation_survives_cloud_map_refusing_to_release_a_still_registered_service()
    {
        // Expected, not exceptional: compensation deliberately does not wait for the ECS service to drain, so
        // the task is usually still registered. Tearing the instance out by hand would deregister a live task.
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery());
        var operation = provisioner.CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest()));
        await operation.CreateAsync();

        scenario.Api.Responder = request => request.IsServiceDiscovery
            ? AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                EcsScenario.CloudMapErrorJson(
                    EcsScenario.ResourceInUseErrorType,
                    "The service contains registered instances."))
            : AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson(tags: EcsScenario.DiscoveryTags)));

        var act = () => operation.CompensateAsync();

        await act.Should().NotThrowAsync("a cleanup's failure must not replace the create failure it is handling");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Destroying.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Destroying_deletes_the_registration_after_the_ecs_service_has_settled()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryDestroy();

        var destroyed = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .DestroyAsync(EcsScenario.RecordedHandle());

        destroyed.Should().BeTrue();

        var order = scenario.Api.Requests
            .Select(r => (r.IsServiceDiscovery ? "cloudmap:" : "ecs:") + r.EcsAction)
            .ToList();

        // The registry ARN is read from the live service before the delete; the Cloud Map delete happens only
        // after ECS has reported INACTIVE, by which point ECS has deregistered the task.
        order.Should().StartWith(["ecs:DescribeServices", "ecs:DeleteService"]);
        order.Should().EndWith(["cloudmap:ListTagsForResource", "cloudmap:DeleteService"]);
    }

    [Fact]
    public async Task Destroying_never_deletes_the_namespace_and_never_deregisters_by_hand()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryDestroy();

        await scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .DestroyAsync(EcsScenario.RecordedHandle());

        var actions = scenario.Api.Requests.Where(r => r.IsServiceDiscovery).Select(r => r.CloudMapAction).ToList();

        actions.Should().NotContain("DeleteNamespace");
        actions.Should().NotContain("DeregisterInstance");
    }

    [Fact]
    public async Task A_registration_that_is_not_servyx_s_is_left_exactly_where_it_is()
    {
        // The ARN came from a Servyx-tagged ECS service, which is evidence and not proof: an operator may have
        // pointed it at a Cloud Map service they made and share.
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryDestroy(cloudMapTagsJson: """{ "Tags": [{"Key":"owner","Value":"platform-team"}] }""");

        var destroyed = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .DestroyAsync(EcsScenario.RecordedHandle());

        destroyed.Should().BeTrue("the ECS service - the billing resource - really was destroyed");
        scenario.Api.Requests
            .Where(r => r.IsServiceDiscovery)
            .Select(r => r.CloudMapAction)
            .Should()
            .NotContain("DeleteService");
    }

    [Fact]
    public async Task A_registration_cloud_map_will_not_release_fails_loudly_rather_than_quietly()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryDestroy(
            cloudMapDelete: () => AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                EcsScenario.CloudMapErrorJson(
                    EcsScenario.ResourceInUseErrorType,
                    "The service contains registered instances.")));

        var act = () => scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .DestroyAsync(EcsScenario.RecordedHandle());

        var thrown = await act.Should().ThrowAsync<AwsApiException>();

        // The message has to say both halves: the expensive thing is gone, and something Servyx made is not.
        thrown.Which.Message.Should().Contain("was destroyed");
        thrown.Which.Message.Should().Contain("no Fargate task is billing");
        thrown.Which.Message.Should().Contain("NOT reachable by this adapter's reconcile");
        thrown.Which.Message.Should().Contain("servicediscovery:DeleteService");
    }

    [Fact]
    public async Task A_service_that_was_never_registered_is_destroyed_without_a_cloud_map_call()
    {
        var scenario = new EcsScenario();
        var describes = 0;

        scenario.Api.Responder = request => request.IsServiceDiscovery
            ? throw new InvalidOperationException("There is no registration, so Cloud Map must not be called.")
            : request.EcsAction switch
            {
                "DescribeServices" => AwsApiDouble.Json(
                    HttpStatusCode.OK,
                    EcsScenario.DescribeServicesJson(
                        EcsScenario.ServiceJson(status: ++describes == 1 ? "ACTIVE" : "INACTIVE", runningCount: 0))),
                "DeleteService" => AwsApiDouble.Json(
                    HttpStatusCode.OK,
                    EcsScenario.ServiceEnvelopeJson(EcsScenario.ServiceJson(status: "DRAINING"))),
                _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
            };

        var destroyed = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .DestroyAsync(EcsScenario.RecordedHandle());

        destroyed.Should().BeTrue();
        scenario.Api.Requests.Should().AllSatisfy(r => r.IsServiceDiscovery.Should().BeFalse());
    }

    [Fact]
    public async Task A_handle_from_another_cluster_destroys_nothing_and_calls_nothing()
    {
        var scenario = new EcsScenario();

        var destroyed = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .DestroyAsync(EcsScenario.RecordedHandle(providerResourceId: EcsScenario.ForeignClusterServiceArn));

        destroyed.Should().BeFalse();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Resolving a control address — the point of all of the above.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_registered_service_resolves_to_a_durable_name_when_a_route_has_been_attested()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve();

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().BeOfType<ControlChannelAddress.Durable>()
            .Which.Host.Should().Be(EcsScenario.DiscoveryHost);
        address.OpenableHostOrNull().Should().Be(EcsScenario.DiscoveryHost);
    }

    [Fact]
    public async Task The_durability_justification_says_why_the_name_survives_and_whose_claim_the_rest_is()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve();

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        var justification = address.Should().BeOfType<ControlChannelAddress.Durable>().Which.Justification;

        justification.Should().Contain("belongs to the ECS service, not to any task");
        justification.Should().Contain("deregisters it when the task stops");
        justification.Should().Contain("REACHABILITY IS THE OPERATOR'S CLAIM AND NOT SERVYX'S");
        justification.Should().Contain(EcsScenario.ControlPlaneVpcAccess);
        justification.Should().Contain("private IPv4");
    }

    [Fact]
    public async Task The_name_is_read_back_from_aws_and_never_composed_from_configuration()
    {
        // The whole reason two extra reads are made. If the registration was renamed, or lives in a namespace
        // whose name is nothing like the id Servyx was configured with, the address must follow AWS.
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve(
            cloudMapServiceJson: EcsScenario.CloudMapServiceJson(name: "renamed-by-hand"),
            namespaceJson: EcsScenario.CloudMapNamespaceJson(name: "prod.internal"));

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.OpenableHostOrNull().Should().Be("renamed-by-hand.prod.internal");
    }

    [Fact]
    public async Task Without_an_attestation_the_durable_name_is_reported_and_still_refused()
    {
        // The most important refusal in the adapter. The name is real and durable; handing it to a control
        // channel that is not in the VPC produces something correctly configured that times out.
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve();

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        var reason = address.Should().BeOfType<ControlChannelAddress.NoAddress>().Which.Reason;

        address.OpenableHostOrNull().Should().BeNull();
        reason.Should().Contain(EcsScenario.DiscoveryHost, "a diagnostic must be able to show how close this is");
        reason.Should().Contain("a durable service-discovery name DOES exist");
        reason.Should().Contain("PRIVATE IPv4");
        reason.Should().Contain("controlPlaneVpcAccess");
    }

    [Fact]
    public async Task It_is_still_never_reported_as_merely_ephemeral()
    {
        // Reporting a durable name as ephemeral would be the same error as reporting an unreachable one as
        // durable, pointing the other way.
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve();

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().NotBeOfType<ControlChannelAddress.Ephemeral>();
    }

    [Fact]
    public async Task An_http_namespace_publishes_no_dns_record_and_is_therefore_not_an_address()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve(namespaceJson: EcsScenario.CloudMapNamespaceJson(type: "HTTP"));

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        var reason = address.Should().BeOfType<ControlChannelAddress.NoAddress>().Which.Reason;

        reason.Should().Be(AwsEcsFargateProvisioner.HttpNamespaceReason);
        reason.Should().Contain("DiscoverInstances");
    }

    [Fact]
    public async Task A_public_namespace_is_treated_exactly_as_a_private_one_because_the_address_is_private_either_way()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve(namespaceJson: EcsScenario.CloudMapNamespaceJson(type: "DNS_PUBLIC"));

        var refused = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        refused.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Contain("even when the namespace is a public one");
    }

    [Fact]
    public async Task A_service_created_before_discovery_was_configured_has_no_durable_name()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve(
            describeServicesJson: EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson()));

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        var reason = address.Should().BeOfType<ControlChannelAddress.NoAddress>().Which.Reason;

        reason.Should().Be(AwsEcsFargateProvisioner.NotRegisteredReason);
        reason.Should().Contain("cannot be added afterwards", "this adapter implements no IMaintainer");
    }

    [Fact]
    public async Task A_service_that_is_gone_or_is_not_servyx_s_has_no_control_address()
    {
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve(describeServicesJson: EcsScenario.MissingServiceJson());

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Be(AwsEcsFargateProvisioner.GoneOrUnmanagedReason);
    }

    [Fact]
    public async Task A_registration_ecs_points_at_and_cloud_map_no_longer_has_yields_no_guess()
    {
        var scenario = new EcsScenario();
        scenario.Api.Responder = request => request.IsServiceDiscovery
            ? AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                EcsScenario.CloudMapErrorJson(EcsScenario.CloudMapServiceNotFoundErrorType, "No such service."))
            : AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeServicesJson(
                    EcsScenario.ServiceJson(
                        tags: EcsScenario.DiscoveryTags,
                        registryArn: EcsScenario.CloudMapServiceArn)));

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Be(AwsEcsFargateProvisioner.RegistrationGoneReason);
    }

    [Fact]
    public async Task A_namespace_cloud_map_no_longer_has_yields_no_guess_either()
    {
        var scenario = new EcsScenario();
        scenario.Api.Responder = request => request.IsServiceDiscovery
            ? request.CloudMapAction switch
            {
                "GetService" => AwsApiDouble.Json(
                    HttpStatusCode.OK,
                    EcsScenario.CloudMapServiceEnvelopeJson()),
                _ => AwsApiDouble.Json(
                    HttpStatusCode.BadRequest,
                    EcsScenario.CloudMapErrorJson(
                        EcsScenario.NamespaceNotFoundErrorType,
                        "No such namespace.")),
            }
            : AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeServicesJson(
                    EcsScenario.ServiceJson(
                        tags: EcsScenario.DiscoveryTags,
                        registryArn: EcsScenario.CloudMapServiceArn)));

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Be(AwsEcsFargateProvisioner.NamespaceGoneReason);
    }

    [Fact]
    public async Task A_handle_from_another_cluster_is_refused_before_any_request()
    {
        var scenario = new EcsScenario();

        var address = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(
                EcsScenario.RecordedHandle(providerResourceId: EcsScenario.ForeignClusterServiceArn));

        address.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Be(AwsEcsFargateProvisioner.ForeignHandleReason);
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolving_reads_the_service_and_the_registry_and_never_the_task()
    {
        // A task's address is precisely what service discovery exists to stop anyone pinning to, so the resolve
        // path has no reason to look at one - and the routing function fails the test if it does.
        var scenario = new EcsScenario();
        scenario.RouteDiscoveryResolve();

        await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        var actions = scenario.Api.Requests
            .Select(r => (r.IsServiceDiscovery ? "cloudmap:" : "ecs:") + r.EcsAction)
            .ToList();

        actions.Should().Equal("ecs:DescribeServices", "cloudmap:GetService", "cloudmap:GetNamespace");
    }

    [Fact]
    public async Task A_cancelled_resolve_is_still_honoured_before_anything_is_read()
    {
        var scenario = new EcsScenario();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .ResolveControlAddressAsync(EcsScenario.RecordedHandle(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_durable_control_address_does_not_make_the_resource_reachable()
    {
        // ControlChannelAddress and ResourceReachability answer different questions, and acquiring an address
        // must not move the second one. The Provision tier needs a compose file; a Fargate task has none.
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulDiscoveryCreate();

        var provisioner = scenario.Provisioner(
            serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess));

        var resource = await provisioner
            .CreateOperation(provisioner.BuildSpec(EcsScenario.PalworldRequest()))
            .CreateAsync();

        resource.Reachability.Should().BeOfType<ResourceReachability.NoTransport>()
            .Which.Reason.Should().Be(AwsEcsFargateProvisioner.UnreachableReason);
        resource.Facts.PublicAddress.Should().BeNull("no public address is ever guessed, registered or not");
    }

    [Fact]
    public void Registering_a_service_claims_no_new_provisioning_capability()
    {
        // StaticAddress in particular is not claimed. The name is stable and resolves to a private address, so
        // an operator reading that bit would build a connection string on something that does not route.
        var scenario = new EcsScenario();

        var expected = ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost;

        scenario.Provisioner(serviceDiscovery: EcsScenario.Discovery(EcsScenario.ControlPlaneVpcAccess))
            .Capabilities.Should().Be(expected);
    }

    [Fact]
    public async Task A_sweep_still_returns_ecs_services_and_never_a_cloud_map_service()
    {
        // Stated as a test because it is the honest limit: an orphaned Cloud Map service is not findable by this
        // reconcile, and putting one on a delete list DestroyAsync cannot act on would be worse than saying so.
        var scenario = new EcsScenario();
        scenario.RouteSweep(
            describeServicesJson: EcsScenario.DescribeServicesJson(
                EcsScenario.ServiceJson(
                    tags: EcsScenario.DiscoveryTags,
                    registryArn: EcsScenario.CloudMapServiceArn)));

        var handles = await scenario
            .Provisioner(serviceDiscovery: EcsScenario.Discovery())
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id, EcsScenario.Region));

        handles.Should().ContainSingle()
            .Which.ProviderResourceId.Should().Be(EcsScenario.ServiceArn);
        scenario.Api.Requests.Should().AllSatisfy(r => r.IsServiceDiscovery.Should().BeFalse());
    }

    [Fact]
    public void A_swept_service_can_at_least_name_the_registration_it_depends_on()
    {
        // The pointer tags are worth exactly this much and no more: while the ECS service exists, an operator
        // who finds it can find the Cloud Map service and namespace behind it. Once it is destroyed, that
        // pointer is destroyed with it.
        EcsScenario.DiscoveryTags.Should().ContainKey("servyx.aws-cloud-map-namespace");
        EcsScenario.DiscoveryTags.Should().ContainKey("servyx.aws-cloud-map-service");
    }

    [Fact]
    public void The_unregistered_refusal_now_points_at_the_configuration_that_changes_it()
    {
        var reason = AwsEcsFargateProvisioner.NoControlAddressReason;

        reason.Should().Contain("AwsFargateServiceDiscovery");
        reason.Should().Contain("PRIVATE address into that record even in a public namespace");
    }

    [Fact]
    public void The_ttl_is_short_because_the_address_behind_the_durable_name_moves()
    {
        AwsFargateServiceDiscovery.DefaultRecordTtlSeconds.Should().BeLessThanOrEqualTo(30);

        var plan = Plan(new EcsScenario(), EcsScenario.Discovery());

        Stage(plan, "create-cloud-map-service").Should().Contain(
            "TTL " + AwsFargateServiceDiscovery.DefaultRecordTtlSeconds.ToString(CultureInfo.InvariantCulture) + "s");
    }
}
