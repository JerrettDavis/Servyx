using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// Tests for the AWS ECS/Fargate adapter — shape M, and the second adapter in this codebase whose output is a
/// resource no transport can reach.
/// </summary>
/// <remarks>
/// <para>
/// Five claims carry most of the weight here and are asserted rather than documented. First, that the adapter
/// never names a transport: not a real one, not a made-up one, not an empty one. Second, that planning is free —
/// no HTTP request, no signature, no secret resolution. Third, that a <c>200 OK</c> from ECS is never reported as
/// success at either end of a resource's life: a create is not done until a task reports <c>RUNNING</c>, and a
/// destroy is not done until the service reports <c>INACTIVE</c>. Fourth, that persistent storage is
/// unrepresentable-otherwise rather than merely validated. Fifth, that the adapter touches exactly one AWS
/// service and never reaches for <c>elasticfilesystem</c>, <c>ec2</c>, <c>iam</c> or <c>logs</c>.
/// </para>
/// <para>
/// No <c>Should().Match(x =&gt; x is …)</c> anywhere: that overload compiles to an expression tree, where a
/// pattern-matching operator is a compile error (CS8122). Shapes are asserted with <c>BeOfType</c>.
/// </para>
/// <para>
/// Every test runs against a substituted <see cref="HttpMessageHandler"/>, so no AWS account, IAM credential, or
/// outbound network access is required or attempted.
/// </para>
/// </remarks>
public class AwsEcsFargateProvisionerTests
{
    // -----------------------------------------------------------------------------------------------------
    // Identity and capabilities
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_id_is_stable()
    {
        new EcsScenario().Provisioner().ProvisionerId.Should().Be("aws-ecs-fargate");
    }

    [Fact]
    public void The_region_and_cluster_are_adapter_state_a_caller_can_read()
    {
        var provisioner = new EcsScenario().Provisioner();

        provisioner.Region.Should().Be(EcsScenario.Region);
        provisioner.Cluster.Should().Be(EcsScenario.Cluster);
    }

    [Fact]
    public void Capabilities_are_exactly_what_is_implemented()
    {
        new EcsScenario().Provisioner().Capabilities.Should().Be(
            ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost);
    }

    [Theory]
    [InlineData(ProvisioningCapabilities.Resize)]
    [InlineData(ProvisioningCapabilities.Snapshot)]
    [InlineData(ProvisioningCapabilities.StaticAddress)]
    [InlineData(ProvisioningCapabilities.FirewallRules)]
    [InlineData(ProvisioningCapabilities.UpdateInPlace)]
    [InlineData(ProvisioningCapabilities.RecreateToUpdate)]
    [InlineData(ProvisioningCapabilities.DetectDrift)]
    public void Capabilities_ECS_offers_but_this_adapter_does_not_implement_are_absent(ProvisioningCapabilities absent)
    {
        // StaticAddress in particular: a Fargate task's address belongs to its ENI and the service replaces the
        // task as ordinary operation, so the address moves by design rather than by exception. FirewallRules
        // likewise: a security group with real source rules exists here, and this adapter does not write it.
        new EcsScenario().Provisioner().Capabilities.HasFlag(absent).Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------------------
    // Planning is free
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Planning_issues_no_http_request_at_all()
    {
        var scenario = new EcsScenario();

        await scenario.Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Planning_resolves_no_secret_at_all()
    {
        var scenario = new EcsScenario();

        await scenario.Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        // Not merely "no mount credential" - this shape has none. The AWS key pair is resolved only by the
        // signer, per request, and a plan sends no request.
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateOperation_makes_no_call_and_resolves_nothing_until_it_is_driven()
    {
        var scenario = new EcsScenario();

        scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest());

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();

        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------------------------------------
    // What the plan says out loud
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_states_that_the_cluster_is_required_and_is_never_created()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "require-ecs-cluster");
        stage.Description.Should().Contain("REQUIRES (does not create)");
        stage.Description.Should().Contain(EcsScenario.Cluster);
        plan.Stages.Select(s => s.StageId).Should().NotContain("create-ecs-cluster");
    }

    [Fact]
    public async Task A_plan_states_that_the_efs_file_system_is_required_billed_separately_and_never_destroyed()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "require-efs-file-system");
        stage.Description.Should().Contain(EcsScenario.FileSystemId);
        stage.Description.Should().Contain(EcsScenario.AccessPointId);
        stage.Description.Should().Contain(EcsScenario.MountPath);
        stage.Description.Should().Contain("BILLED SEPARATELY");
        stage.Description.Should().Contain("NEVER created, modified or destroyed by Servyx");
    }

    [Fact]
    public async Task A_plan_names_the_mount_target_and_nfs_preconditions_Servyx_cannot_check()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "require-efs-file-system");

        // The hazard that replaces ACI's storage-account key: no credential, but two network preconditions that
        // let every API call succeed and then kill the task.
        stage.Description.Should().Contain("NOT CHECKED BY SERVYX");
        stage.Description.Should().Contain("mount target");
        stage.Description.Should().Contain("2049");
        stage.Description.Should().Contain("No credential is involved");
    }

    [Fact]
    public async Task A_plan_says_the_task_definition_revision_is_free_and_will_never_be_deleted()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "register-task-definition");
        stage.Description.Should().Contain("FREE");
        stage.Description.Should().Contain("never deleted");
        stage.Description.Should().Contain("Servyx does not sweep them");
    }

    [Fact]
    public async Task A_plan_marks_the_service_create_as_billable_and_says_the_meter_runs_indefinitely()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "create-service");
        stage.Description.Should().Contain("BILLABLE per second");
        stage.Description.Should().Contain("indefinitely by design");
        stage.Description.Should().Contain(EcsScenario.SubnetId);
        stage.Description.Should().Contain(EcsScenario.SecurityGroupId);
    }

    [Fact]
    public async Task A_plan_names_the_confirmation_step_and_why_it_exists()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "await-running-task");
        stage.Description.Should().Contain("CreateService answers 200 OK");
        stage.Description.Should().Contain("running count at zero");
        stage.Description.Should().Contain("stoppedReason");
    }

    [Fact]
    public async Task A_plan_says_no_stable_address_is_provided_and_names_what_obtaining_one_would_take()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "no-stable-address");
        stage.Description.Should().Contain("NOT PROVIDED");
        stage.Description.Should().Contain("ec2:DescribeNetworkInterfaces");
        stage.Description.Should().Contain("load balancer or Cloud Map");
    }

    [Fact]
    public async Task A_plan_says_the_result_will_be_unreachable_and_why()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "handoff-unreachable");
        stage.Description.Should().Contain("NO TRANSPORT TARGET");
        stage.Description.Should().Contain("RCON");
        stage.Description.Should().Contain("Provision tier");
        stage.Description.Should().Contain("ECS Exec");
    }

    [Fact]
    public async Task A_source_cidr_is_reported_as_not_applied_and_says_whose_security_group_it_is()
    {
        var scenario = new EcsScenario();
        var request = EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ingress:25575/tcp"] = "198.51.100.0/24" });

        var plan = await scenario.Provisioner().PlanAsync(request);

        var stage = plan.Stages.Single(s => s.StageId == "ingress-source-not-applied");
        stage.Description.Should().Contain("198.51.100.0/24");

        // The distinction from ACI, which the plan must not blur: there, no filter exists; here, one exists and
        // Servyx does not write it. Those lead an operator to different next steps.
        stage.Description.Should().Contain("DOES sit behind a real");
        stage.Description.Should().Contain("makes no ec2 call");
    }

    [Fact]
    public async Task Ports_without_a_source_cidr_produce_no_not_applied_stage()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        plan.Stages.Select(s => s.StageId).Should().NotContain("ingress-source-not-applied");
    }

    [Fact]
    public async Task A_missing_log_group_produces_a_stage_saying_container_output_is_discarded()
    {
        var scenario = new EcsScenario();
        var request = EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["logGroup"] = string.Empty });

        var plan = await scenario.Provisioner().PlanAsync(request);

        var stage = plan.Stages.Single(s => s.StageId == "no-log-configuration");
        stage.Description.Should().Contain("DISCARDED");
        stage.Description.Should().Contain("no 'docker logs' to run");
    }

    [Fact]
    public async Task A_named_log_group_produces_no_such_stage()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        plan.Stages.Select(s => s.StageId).Should().NotContain("no-log-configuration");
    }

    [Fact]
    public async Task The_plan_hash_is_stable_for_the_same_request_and_moves_when_the_file_system_moves()
    {
        var scenario = new EcsScenario();
        var provisioner = scenario.Provisioner();

        var first = await provisioner.PlanAsync(EcsScenario.PalworldRequest());
        var second = await provisioner.PlanAsync(EcsScenario.PalworldRequest());
        var moved = await provisioner.PlanAsync(EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["fileSystemId"] = "fs-99999999999999999" }));

        first.PlanHash.Should().Be(second.PlanHash);
        moved.PlanHash.Should().NotBe(first.PlanHash);
    }

    [Fact]
    public async Task The_plan_hash_moves_when_the_subnet_list_changes()
    {
        var scenario = new EcsScenario();
        var provisioner = scenario.Provisioner();

        var one = await provisioner.PlanAsync(EcsScenario.PalworldRequest());
        var two = await provisioner.PlanAsync(EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["subnetId:1"] = EcsScenario.SecondSubnetId }));

        two.PlanHash.Should().NotBe(one.PlanHash);
    }

    [Fact]
    public void Indexed_subnet_parameters_are_ordered_numerically_and_not_by_string()
    {
        var scenario = new EcsScenario();

        // '10' sorts before '9' as a string; the order reaches the request body and the plan hash, so it must be
        // the caller's order rather than the lexical one.
        var spec = scenario.Spec(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subnetId:0"] = "subnet-a",
            ["subnetId:9"] = "subnet-b",
            ["subnetId:10"] = "subnet-c",
        });

        spec.SubnetIds.Should().Equal("subnet-a", "subnet-b", "subnet-c");
    }

    // -----------------------------------------------------------------------------------------------------
    // Cost is compute-only, and says so
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cost_estimate_is_a_list_price_computed_from_the_two_per_second_meters()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        // 1 vCPU * $0.04048/hr + 2 GB * $0.004445/hr = $0.04937/hr.
        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.ListPrice);
        plan.EstimatedCost.Hourly.Should().Be(0.0494m);
        plan.EstimatedCost.Monthly.Should().Be(36.04m);
        plan.EstimatedCost.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task The_cost_estimate_says_out_loud_that_it_is_not_all_in()
    {
        var plan = await new EcsScenario().Provisioner().PlanAsync(EcsScenario.PalworldRequest());

        plan.EstimatedCost.Source.Should().Contain("COMPUTE ONLY - NOT ALL-IN");
        plan.EstimatedCost.Source.Should().Contain("EFS file system");
        plan.EstimatedCost.Source.Should().Contain("CloudWatch Logs");
        plan.EstimatedCost.Source.Should().Contain("not directly comparable");
    }

    // -----------------------------------------------------------------------------------------------------
    // Fargate's size matrix is enforced at plan time
    // -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(256, 512)]
    [InlineData(256, 2048)]
    [InlineData(512, 1024)]
    [InlineData(1024, 2048)]
    [InlineData(2048, 16384)]
    [InlineData(4096, 30720)]
    [InlineData(8192, 61440)]
    [InlineData(16384, 122880)]
    public void Sizings_Fargate_will_run_are_accepted(int cpu, int memory)
    {
        AwsFargateSizing.IsValid(cpu, memory).Should().BeTrue();
    }

    [Theory]
    [InlineData(256, 4096)]
    [InlineData(1024, 1024)]
    [InlineData(1024, 2500)]
    [InlineData(768, 2048)]
    [InlineData(16384, 131072)]
    public void Sizings_Fargate_would_refuse_are_rejected(int cpu, int memory)
    {
        AwsFargateSizing.IsValid(cpu, memory).Should().BeFalse();
    }

    [Fact]
    public void An_impossible_sizing_is_refused_at_plan_time_naming_the_legal_range()
    {
        var scenario = new EcsScenario();
        var request = EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["memory"] = "1024" });

        var act = () => scenario.Provisioner().BuildSpec(request);

        // RegisterTaskDefinition would refuse this pair; refusing it here means the plan a caller approves is
        // one that can actually be applied.
        act.Should().Throw<ArgumentException>().WithMessage("*between 2048 and 8192 MiB*");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_cpu_value_that_is_not_a_Fargate_size_at_all_names_the_seven_that_are()
    {
        AwsFargateSizing.DescribeAllowed(768).Should().Contain("1024 units = 1 vCPU");
    }

    // -----------------------------------------------------------------------------------------------------
    // Persistent storage and networking are mandatory in the type, not in a validation rule
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_spec_cannot_be_built_without_a_persistent_volume()
    {
        var act = () => new AwsFargateServiceSpec(
            "svc",
            "cluster",
            "image",
            null!,
            [EcsScenario.SubnetId],
            ServyxEcsTags.For("i", "j", "c"));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_spec_cannot_be_built_without_a_subnet()
    {
        var act = () => new AwsFargateServiceSpec(
            "svc",
            "cluster",
            "image",
            new EfsVolumeMount(EcsScenario.FileSystemId, "/data"),
            [],
            ServyxEcsTags.For("i", "j", "c"));

        act.Should().Throw<ArgumentException>().WithMessage("*awsvpc*");
    }

    [Theory]
    [InlineData("fileSystemId")]
    [InlineData("mountPath")]
    [InlineData("name")]
    [InlineData("image")]
    [InlineData("instanceId")]
    public void A_request_missing_a_required_parameter_is_refused(string missing)
    {
        var scenario = new EcsScenario();
        var request = EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { [missing] = string.Empty });

        var act = () => scenario.Provisioner().BuildSpec(request);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_request_with_no_subnet_is_refused_naming_awsvpc()
    {
        var scenario = new EcsScenario();
        var request = EcsScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["subnetId:0"] = string.Empty });

        var act = () => scenario.Provisioner().BuildSpec(request);

        act.Should().Throw<ArgumentException>().WithMessage("*awsvpc*");
    }

    [Fact]
    public void A_file_system_id_that_is_not_one_is_refused()
    {
        var act = () => new EfsVolumeMount("my-file-system", "/data");

        act.Should().Throw<ArgumentException>().WithMessage("*not an EFS file system id*");
    }

    [Fact]
    public void An_access_point_id_that_is_not_one_is_refused()
    {
        var act = () => new EfsVolumeMount(EcsScenario.FileSystemId, "/data", accessPointId: "ap-123");

        act.Should().Throw<ArgumentException>().WithMessage("*not an EFS access point id*");
    }

    [Fact]
    public void A_relative_mount_path_is_refused()
    {
        var act = () => new EfsVolumeMount(EcsScenario.FileSystemId, "data");

        act.Should().Throw<ArgumentException>().WithMessage("*absolute path*");
    }

    [Fact]
    public void An_access_point_combined_with_a_non_root_directory_is_refused()
    {
        // AWS refuses the combination outright; catching it here means the plan a caller approves is one that
        // can actually be applied.
        var act = () => new EfsVolumeMount(
            EcsScenario.FileSystemId,
            "/data",
            rootDirectory: "/saves",
            accessPointId: EcsScenario.AccessPointId);

        act.Should().Throw<ArgumentException>().WithMessage("*access point already imposes*");
    }

    [Fact]
    public void The_efs_mount_carries_no_credential_of_any_kind()
    {
        // The one genuine improvement over the ACI mount, asserted rather than described: EFS is authorised by
        // network reachability and IAM, so unlike AzureFileShareMount there is no SecretUrn on this type and
        // nothing for a create path to resolve.
        typeof(EfsVolumeMount).GetProperties()
            .Should().NotContain(p => p.PropertyType == typeof(SecretUrn));
    }

    [Fact]
    public void Transit_encryption_is_always_enabled_and_has_no_knob()
    {
        EfsVolumeMount.TransitEncryption.Should().Be("ENABLED");

        typeof(EfsVolumeMount).GetProperties()
            .Should().NotContain(p => p.Name == "TransitEncryption");
    }

    // -----------------------------------------------------------------------------------------------------
    // Create: two writes, then a confirmation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_registers_the_task_definition_before_it_creates_the_service()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var actions = scenario.Api.Requests.Select(r => r.EcsAction).ToList();

        // The free call validates most of the deployment; issuing it first means an invalid CPU/memory pair or a
        // malformed volume is refused before anything billable exists.
        actions[0].Should().Be("RegisterTaskDefinition");
        actions[1].Should().Be("CreateService");
    }

    [Fact]
    public async Task A_create_makes_exactly_two_writes_and_the_rest_are_reads()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        scenario.Api.Requests
            .Count(r => r.EcsAction is "RegisterTaskDefinition" or "CreateService")
            .Should().Be(2);
    }

    [Fact]
    public async Task Every_request_a_create_makes_goes_to_ECS_and_to_nothing_else()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        // No elasticfilesystem call, no ec2 call, no iam call, no logs call. This adapter creates nothing it
        // cannot destroy, and the corollary is that it talks to exactly one service.
        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().AllSatisfy(r => r.IsEcs.Should().BeTrue());
        scenario.Api.Requests.Should().AllSatisfy(r =>
            r.Target.Should().StartWith("AmazonEC2ContainerServiceV20141113."));
    }

    [Fact]
    public async Task The_service_is_created_against_the_exact_revision_arn_and_not_the_family()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body!;

        // A bare family name resolves to whatever is latest when ECS reads it, which would let the service
        // launch a revision the approved plan never described.
        body.Should().Contain("\"taskDefinition\":\"" + EcsScenario.TaskDefinitionArn + "\"");
    }

    [Fact]
    public async Task The_task_definition_body_mounts_the_efs_volume_with_transit_encryption()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "RegisterTaskDefinition").Body!;

        body.Should().Contain("\"efsVolumeConfiguration\"");
        body.Should().Contain("\"fileSystemId\":\"" + EcsScenario.FileSystemId + "\"");
        body.Should().Contain("\"transitEncryption\":\"ENABLED\"");
        body.Should().Contain("\"accessPointId\":\"" + EcsScenario.AccessPointId + "\"");
        body.Should().Contain("\"containerPath\":\"" + EcsScenario.MountPath + "\"");
        body.Should().Contain("\"sourceVolume\":\"servyx-data\"");
    }

    [Fact]
    public async Task The_task_definition_body_declares_the_ports_the_image_and_the_fargate_requirements()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "RegisterTaskDefinition").Body!;

        body.Should().Contain("\"networkMode\":\"awsvpc\"");
        body.Should().Contain("\"requiresCompatibilities\":[\"FARGATE\"]");
        body.Should().Contain("\"image\":\"" + EcsScenario.Image + "\"");
        body.Should().Contain("\"containerPort\":8211");
        body.Should().Contain("\"protocol\":\"udp\"");
        body.Should().Contain("\"containerPort\":25575");
        // ECS types cpu and memory as strings, not numbers.
        body.Should().Contain("\"cpu\":\"1024\"");
        body.Should().Contain("\"memory\":\"2048\"");
    }

    [Fact]
    public async Task The_task_definition_body_carries_a_log_configuration_when_a_group_is_named()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "RegisterTaskDefinition").Body!;

        body.Should().Contain("\"logDriver\":\"awslogs\"");
        body.Should().Contain("\"awslogs-group\":\"" + EcsScenario.LogGroup + "\"");
        body.Should().Contain("\"awslogs-region\":\"" + EcsScenario.Region + "\"");
    }

    [Fact]
    public async Task The_task_definition_body_carries_no_log_configuration_when_no_group_is_named()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner()
            .CreateOperation(EcsScenario.PalworldRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["logGroup"] = string.Empty }))
            .CreateAsync();

        scenario.Api.Requests.Single(r => r.EcsAction == "RegisterTaskDefinition").Body!
            .Should().NotContain("logConfiguration");
    }

    [Fact]
    public async Task The_service_body_names_one_task_the_awsvpc_configuration_and_the_pre_existing_security_group()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body!;

        body.Should().Contain("\"desiredCount\":1");
        body.Should().Contain("\"launchType\":\"FARGATE\"");
        body.Should().Contain("\"awsvpcConfiguration\"");
        body.Should().Contain("\"subnets\":[\"" + EcsScenario.SubnetId + "\"]");
        body.Should().Contain("\"securityGroups\":[\"" + EcsScenario.SecurityGroupId + "\"]");
        body.Should().Contain("\"assignPublicIp\":\"ENABLED\"");
    }

    [Fact]
    public async Task The_service_body_leaves_ECS_Exec_off()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        // Turning it on would still not produce an IExecutionTarget, so enabling it would imply a capability
        // that does not follow from it.
        scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body!
            .Should().Contain("\"enableExecuteCommand\":false");
    }

    [Fact]
    public async Task Both_taggable_objects_are_tagged_in_the_call_that_creates_them_and_their_roles_differ()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        var definition = scenario.Api.Requests.Single(r => r.EcsAction == "RegisterTaskDefinition").Body!;
        var service = scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body!;

        definition.Should().Contain("\"key\":\"servyx.managed\",\"value\":\"true\"");
        service.Should().Contain("\"key\":\"servyx.managed\",\"value\":\"true\"");

        definition.Should().Contain("\"key\":\"servyx.role\",\"value\":\"ecs-task-definition\"");
        service.Should().Contain("\"key\":\"servyx.role\",\"value\":\"ecs-service\"");
    }

    [Fact]
    public async Task The_service_propagates_its_tags_to_every_task_it_launches()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CreateAsync();

        scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body!
            .Should().Contain("\"propagateTags\":\"SERVICE\"");
    }

    [Fact]
    public async Task The_write_ahead_tags_are_the_tags_that_reach_the_provider()
    {
        var scenario = new EcsScenario();
        scenario.RouteSuccessfulCreate();

        var operation = scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest());
        var recordedBeforeCreate = operation.Tags;

        await operation.CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.EcsAction == "CreateService").Body!;
        foreach (var tag in recordedBeforeCreate)
        {
            body.Should().Contain("\"key\":\"" + tag.Key + "\",\"value\":\"" + tag.Value + "\"");
        }

        operation.Region.Should().Be(EcsScenario.Region);
        operation.ProvisionerId.Should().Be(AwsEcsFargateProvisioner.Id);
    }

    [Fact]
    public async Task The_write_ahead_tags_record_the_task_definition_family_because_the_revision_does_not_exist_yet()
    {
        var scenario = new EcsScenario();

        var operation = scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest());

        operation.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.aws-ecs-task-definition-family", EcsScenario.ServiceName));

        // The revision ARN cannot be here: it does not exist until RegisterTaskDefinition has already run, and
        // the ledger commits these values before the create.
        operation.Tags.Values.Should().NotContain(EcsScenario.TaskDefinitionArn);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_created_service_is_handed_back_as_unreachable_with_a_reason()
    {
        var scenario = new EcsScenario();

        var resource = await scenario.CreateAsync();

        resource.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
        resource.TargetOrNull().Should().BeNull();

        var reason = ((ResourceReachability.NoTransport)resource.Reachability).Reason;
        reason.Should().Be(AwsEcsFargateProvisioner.UnreachableReason);
        reason.Should().Contain("runs no sshd");
        reason.Should().Contain("Systems Manager");
        reason.Should().Contain("RCON");
    }

    [Fact]
    public async Task No_transport_id_is_fabricated_anywhere_in_the_returned_resource()
    {
        var scenario = new EcsScenario();

        var resource = await scenario.CreateAsync();

        // The failure mode this whole shape exists to prevent: a made-up transport id does not fail here, it
        // fails later and elsewhere as "no transport for id", after a billable resource exists.
        var act = () => resource.RequireTarget();

        act.Should().Throw<InvalidOperationException>().WithMessage("*not reachable by any transport*");
    }

    [Fact]
    public async Task The_created_handle_names_the_service_arn_and_carries_the_role_and_the_four_pointers()
    {
        var scenario = new EcsScenario();

        var resource = await scenario.CreateAsync();

        resource.Handle.ProvisionerId.Should().Be(AwsEcsFargateProvisioner.Id);
        resource.Handle.ProviderResourceId.Should().Be(EcsScenario.ServiceArn);
        resource.Handle.Region.Should().Be(EcsScenario.Region);
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>("servyx.role", "ecs-service"));
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.aws-ecs-cluster", EcsScenario.Cluster));
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.aws-ecs-task-definition-family", EcsScenario.ServiceName));
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.aws-efs-file-system", EcsScenario.FileSystemId));
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.aws-efs-access-point", EcsScenario.AccessPointId));
        resource.ConnectorId.Should().Be(EcsScenario.ConnectorId);
    }

    [Fact]
    public async Task A_create_reports_the_current_tasks_private_address_and_never_a_public_one()
    {
        var scenario = new EcsScenario();

        var resource = await scenario.CreateAsync();

        resource.Facts.PrivateAddress.Should().Be(EcsScenario.PrivateIp);

        // DescribeTasks reports no public address; obtaining one means an ec2 call this adapter does not make,
        // and it would be just as ephemeral. Guessing would be exactly the fabrication this shape avoids.
        resource.Facts.PublicAddress.Should().BeNull();
    }

    [Fact]
    public async Task The_network_interface_id_is_read_but_never_turned_into_an_address()
    {
        var scenario = new EcsScenario();

        var resource = await scenario.CreateAsync();

        scenario.Api.Requests.Should().AllSatisfy(r => r.IsEcs.Should().BeTrue());
        resource.Facts.PublicAddress.Should().BeNull();
        resource.Facts.PrivateAddress.Should().NotBe(EcsScenario.NetworkInterfaceId);
    }

    [Fact]
    public async Task A_task_that_is_still_pending_is_polled_until_it_runs()
    {
        var scenario = new EcsScenario();
        var describes = 0;

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "RegisterTaskDefinition" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.TaskDefinitionEnvelopeJson()),
            "CreateService" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.ServiceEnvelopeJson()),
            "ListTasks" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.ListTasksJson()),
            "DescribeTasks" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeTasksJson(EcsScenario.TaskJson(lastStatus: ++describes > 1 ? "RUNNING" : "PENDING"))),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        var resource = await scenario.Provisioner(pollAttempts: 5)
            .CreateOperation(EcsScenario.PalworldRequest())
            .CreateAsync();

        resource.Facts.PrivateAddress.Should().Be(EcsScenario.PrivateIp);
    }

    [Fact]
    public async Task A_service_whose_task_never_runs_is_surfaced_as_a_failure_so_it_can_be_compensated()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "RegisterTaskDefinition" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.TaskDefinitionEnvelopeJson()),
            "CreateService" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.ServiceEnvelopeJson(EcsScenario.ServiceJson(runningCount: 0))),
            "ListTasks" => AwsApiDouble.Json(HttpStatusCode.OK, """{ "taskArns": [] }"""),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        var operation = scenario.Provisioner(pollAttempts: 2).CreateOperation(EcsScenario.PalworldRequest());

        var act = async () => await operation.CreateAsync();

        // ECS said 200 OK and reported the service ACTIVE. That is a submission, not a running workload.
        (await act.Should().ThrowAsync<AwsApiException>()).WithMessage("*no task reached RUNNING*");
    }

    [Fact]
    public async Task A_failure_to_start_carries_ECSs_own_stopped_reason()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request =>
        {
            if (request.EcsAction == "RegisterTaskDefinition")
            {
                return AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.TaskDefinitionEnvelopeJson());
            }

            if (request.EcsAction == "CreateService")
            {
                return AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.ServiceEnvelopeJson());
            }

            if (request.EcsAction == "ListTasks")
            {
                // A task whose desired status is RUNNING does not exist; the failed one is listed as STOPPED.
                return AwsApiDouble.Json(
                    HttpStatusCode.OK,
                    request.Body!.Contains("\"desiredStatus\":\"STOPPED\"", StringComparison.Ordinal)
                        ? EcsScenario.ListTasksJson()
                        : """{ "taskArns": [] }""");
            }

            if (request.EcsAction == "DescribeTasks")
            {
                return AwsApiDouble.Json(
                    HttpStatusCode.OK,
                    EcsScenario.DescribeTasksJson(EcsScenario.TaskJson(
                        lastStatus: "STOPPED",
                        desiredStatus: "STOPPED",
                        privateIp: null,
                        stoppedReason: "ResourceInitializationError: failed to invoke EFS utils commands")));
            }

            throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'.");
        };

        var operation = scenario.Provisioner(pollAttempts: 2).CreateOperation(EcsScenario.PalworldRequest());

        var act = async () => await operation.CreateAsync();

        // This is where a missing EFS mount target actually shows up. An exception that omitted it would send an
        // operator to the console to find out what a call Servyx already made had been told.
        (await act.Should().ThrowAsync<AwsApiException>()).WithMessage("*failed to invoke EFS utils commands*");
    }

    [Fact]
    public async Task Every_request_is_signed_and_the_key_pair_never_travels()
    {
        var scenario = new EcsScenario();

        await scenario.CreateAsync();

        foreach (var request in scenario.Api.Requests)
        {
            request.Authorization.Should().StartWith("AWS4-HMAC-SHA256");
            request.Signature.Should().NotBeNullOrWhiteSpace();
            request.AmzDate.Should().NotBeNullOrWhiteSpace();
        }

        // SigV4 transmits a hex HMAC, never the credential. The whole exchange must not contain either half of
        // the key pair anywhere.
        var everything = string.Join(
            "\n",
            scenario.Api.Requests.Select(r => (r.Body ?? string.Empty) + "\n" + (r.Authorization ?? string.Empty)));

        everything.Should().NotContain(EcsScenario.SecretAccessKey);
    }

    [Fact]
    public async Task The_credential_scope_names_the_ecs_service_and_the_configured_region()
    {
        var scenario = new EcsScenario();

        await scenario.CreateAsync();

        scenario.Api.Requests[0].Credential.Should().Contain("/" + EcsScenario.Region + "/ecs/aws4_request");
    }

    // -----------------------------------------------------------------------------------------------------
    // Compensation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compensation_deletes_the_service_it_created()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "DescribeServices" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.DescribeServicesJson()),
            "DeleteService" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.ServiceEnvelopeJson()),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CompensateAsync();

        scenario.Api.Requests.Should().Contain(r => r.EcsAction == "DeleteService");
    }

    [Fact]
    public async Task Compensation_leaves_a_name_collision_with_someone_elses_service_alone()
    {
        var scenario = new EcsScenario();
        var foreignTags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = "someone-elses-instance",
            ["servyx.job-id"] = "j",
            ["servyx.connector-id"] = "c",
        };

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "DescribeServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson(tags: foreignTags))),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CompensateAsync();

        scenario.Api.Requests.Should().NotContain(r => r.EcsAction == "DeleteService");
    }

    [Fact]
    public async Task Compensation_of_a_service_that_does_not_exist_deletes_nothing()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "DescribeServices" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.MissingServiceJson()),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CompensateAsync();

        scenario.Api.Requests.Should().NotContain(r => r.EcsAction == "DeleteService");
    }

    [Fact]
    public async Task Compensation_never_touches_the_efs_file_system_or_the_task_definition()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "DescribeServices" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.DescribeServicesJson()),
            "DeleteService" => AwsApiDouble.Json(HttpStatusCode.OK, EcsScenario.ServiceEnvelopeJson()),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        await scenario.Provisioner().CreateOperation(EcsScenario.PalworldRequest()).CompensateAsync();

        scenario.Api.Requests.Should().NotContain(r => r.EcsAction == "DeregisterTaskDefinition");
        scenario.Api.Requests.Should().AllSatisfy(r => r.IsEcs.Should().BeTrue());
    }

    // -----------------------------------------------------------------------------------------------------
    // Refresh
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_reports_the_service_as_unreachable_too()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly();

        var refreshed = await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle());

        refreshed.Should().NotBeNull();
        refreshed!.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
        refreshed.ConnectorId.Should().Be(EcsScenario.ConnectorId);
    }

    [Fact]
    public async Task A_refresh_reports_the_current_tasks_private_address_and_no_public_one()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly();

        var refreshed = await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle());

        refreshed!.Facts.PrivateAddress.Should().Be(EcsScenario.PrivateIp);
        refreshed.Facts.PublicAddress.Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_of_a_handle_that_is_not_a_service_arn_answers_null_without_calling_aws()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly();

        var refreshed = await scenario.Provisioner().RefreshAsync(
            new ResourceHandle(
                AwsEcsFargateProvisioner.Id,
                "i-0123456789abcdef0",
                EcsScenario.Region,
                EcsScenario.CanonicalTags));

        refreshed.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_of_a_service_in_another_cluster_answers_null_without_calling_aws()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly();

        var refreshed = await scenario.Provisioner().RefreshAsync(
            EcsScenario.RecordedHandle(EcsScenario.ForeignClusterServiceArn));

        // The cluster is in the ARN, so this is decidable without asking AWS - and a provisioner only ever
        // writes to the one cluster it was configured with.
        refreshed.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_of_a_service_ECS_reports_as_missing_answers_null()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly(EcsScenario.MissingServiceJson());

        // ECS answers 200 OK and moves the ARN into a failures array; an adapter reading only the services array
        // would see an empty list and have no way to tell "gone" from "asked about nothing".
        (await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle())).Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_of_an_inactive_service_answers_null_because_that_is_a_deleted_one()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly(EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson(status: "INACTIVE")));

        (await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle())).Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_of_a_service_that_is_not_servyx_managed_answers_null()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly(EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson(
            tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "someone-else" })));

        (await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle())).Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_of_a_service_with_no_running_task_does_not_throw()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly(listTasksJson: """{ "taskArns": [] }""");

        // The service exists and ECS is trying to start a replacement. That is a fact to report, not a missing
        // resource - the same divergence from the VM adapters the ACI adapter makes.
        var refreshed = await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle());

        refreshed.Should().NotBeNull();
        refreshed!.Facts.PrivateAddress.Should().BeNull();
        refreshed.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
    }

    [Fact]
    public async Task A_refresh_prices_the_service_from_the_revision_ECS_reports()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly(taskDefinitionJson: EcsScenario.TaskDefinitionJson(cpu: "2048", memory: "4096"));

        var refreshed = await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle());

        // 2 vCPU * $0.04048 + 4 GB * $0.004445 = $0.09874/hr.
        refreshed!.Facts.Cost.Hourly.Should().Be(0.0987m);
        refreshed.Facts.Cost.Source.Should().Contain("COMPUTE ONLY - NOT ALL-IN");
    }

    [Fact]
    public async Task A_refresh_reads_the_task_definition_separately_because_a_service_does_not_carry_its_reservation()
    {
        var scenario = new EcsScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().RefreshAsync(EcsScenario.RecordedHandle());

        // Four reads where the ACI adapter needs one: the shape of the provider, not inefficiency.
        scenario.Api.Requests.Select(r => r.EcsAction).Should().Equal(
            "DescribeServices", "ListTasks", "DescribeTasks", "DescribeTaskDefinition");
    }

    // -----------------------------------------------------------------------------------------------------
    // Reconcile
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sweep_finds_managed_fargate_services()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep();

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be(EcsScenario.ServiceArn);
        handles[0].ProvisionerId.Should().Be(AwsEcsFargateProvisioner.Id);
        handles[0].Region.Should().Be(EcsScenario.Region);
    }

    [Fact]
    public async Task A_sweep_asks_for_tags_explicitly_because_an_untagged_read_would_look_unmanaged()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep();

        await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        // Without include:["TAGS"] ECS returns an empty tags array rather than omitting the member, so every
        // Servyx service would read as someone else's - a silent failure that means "do not sweep".
        scenario.Api.Requests.Single(r => r.EcsAction == "DescribeServices").Body!
            .Should().Contain("\"include\":[\"TAGS\"]");
    }

    [Fact]
    public async Task A_sweep_ignores_a_service_that_is_not_servyx_managed()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep(describeServicesJson: EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson(
            tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "someone-else" })));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        // A sweep's output is a delete list, and acting on a false positive destroys someone else's workload.
        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_ignores_an_inactive_service_because_it_no_longer_exists()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep(describeServicesJson: EcsScenario.DescribeServicesJson(
            EcsScenario.ServiceJson(status: "INACTIVE")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_includes_a_draining_service_because_it_still_exists()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep(describeServicesJson: EcsScenario.DescribeServicesJson(
            EcsScenario.ServiceJson(status: "DRAINING")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        handles.Should().ContainSingle();
    }

    [Fact]
    public async Task A_sweep_follows_pagination_rather_than_stopping_at_the_first_page()
    {
        var scenario = new EcsScenario();
        var page = 0;

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "ListServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                ++page == 1
                    ? EcsScenario.ListServicesJson("more", EcsScenario.ServiceArn)
                    : EcsScenario.ListServicesJson(null, EcsScenario.ServiceArn + "-two")),
            "DescribeServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeServicesJson(
                    EcsScenario.ServiceJson(),
                    EcsScenario.ServiceJson(arn: EcsScenario.ServiceArn + "-two", serviceName: "second"))),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        // Stopping at page one would report "no orphans beyond page one" as "no orphans".
        page.Should().Be(2);
        handles.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_sweep_lists_services_and_nothing_else()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep();

        await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        // No ListTaskDefinitions: a revision is free and undeletable, so putting one on a delete list would be
        // handing back a handle nothing can act on.
        scenario.Api.Requests.Select(r => r.EcsAction)
            .Should().OnlyContain(a => a == "ListServices" || a == "DescribeServices");
    }

    [Fact]
    public async Task A_sweep_for_another_provisioner_reports_nothing_and_calls_nothing()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep();

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("aws-ec2"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_for_another_region_reports_nothing_and_calls_nothing()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep();

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id, "eu-west-1"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_search_space_shape_this_adapter_does_not_serve_is_declined_without_widening_it()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep();

        var handles = await scenario.Provisioner().ReconcileAsync(
            new OrphanScope.MarkerDirectory(AwsEcsFargateProvisioner.Id, "/var/lib/servyx"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_of_an_empty_cluster_describes_nothing()
    {
        var scenario = new EcsScenario();
        scenario.RouteSweep(listServicesJson: """{ "serviceArns": [] }""");

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEcsFargateProvisioner.Id));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().NotContain(r => r.EcsAction == "DescribeServices");
    }

    // -----------------------------------------------------------------------------------------------------
    // Destroy — confirmed, never merely submitted
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_destroy_deletes_the_service_and_confirms_it_reached_inactive()
    {
        var scenario = new EcsScenario();
        scenario.RouteDestroy();

        var destroyed = await scenario.Provisioner().DestroyAsync(EcsScenario.RecordedHandle());

        destroyed.Should().BeTrue();
        scenario.Api.Requests.Should().ContainSingle(r => r.EcsAction == "DeleteService");
        scenario.Api.Requests.Should().Contain(r => r.EcsAction == "DescribeServices");
    }

    [Fact]
    public async Task A_destroy_passes_force_because_a_service_at_desired_count_one_cannot_be_deleted_otherwise()
    {
        var scenario = new EcsScenario();
        scenario.RouteDestroy();

        await scenario.Provisioner().DestroyAsync(EcsScenario.RecordedHandle());

        // Not a bypass of a safety check: what the flag authorises is stopping this service's own tasks, which
        // is precisely what destroying it means.
        scenario.Api.Requests.Single(r => r.EcsAction == "DeleteService").Body!
            .Should().Contain("\"force\":true");
    }

    [Fact]
    public async Task A_destroy_that_is_only_submitted_is_not_reported_as_success()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "DeleteService" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.ServiceEnvelopeJson(EcsScenario.ServiceJson(status: "DRAINING"))),
            "DescribeServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                EcsScenario.DescribeServicesJson(EcsScenario.ServiceJson(status: "DRAINING"))),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        var act = async () => await scenario.Provisioner(pollAttempts: 2).DestroyAsync(EcsScenario.RecordedHandle());

        // DeleteService answers 200 OK with the service DRAINING and its task still running. Returning true on
        // that response would report a submission as a completion.
        (await act.Should().ThrowAsync<AwsApiException>()).WithMessage("*did not report the service as INACTIVE*");
    }

    [Fact]
    public async Task A_destroy_of_a_service_ECS_never_knew_answers_false()
    {
        var scenario = new EcsScenario();

        scenario.Api.Responder = request => request.EcsAction switch
        {
            "DeleteService" => AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                EcsScenario.ErrorJson(EcsScenario.ServiceNotFoundErrorType, "Service not found.")),
            _ => throw new InvalidOperationException($"Unexpected ECS action '{request.EcsAction}'."),
        };

        var destroyed = await scenario.Provisioner().DestroyAsync(EcsScenario.RecordedHandle());

        destroyed.Should().BeFalse();
    }

    [Fact]
    public async Task A_destroy_of_a_handle_this_adapter_could_not_have_created_deletes_nothing()
    {
        var scenario = new EcsScenario();
        scenario.RouteDestroy();

        var destroyed = await scenario.Provisioner().DestroyAsync(
            new ResourceHandle(
                AwsEcsFargateProvisioner.Id,
                "i-0123456789abcdef0",
                EcsScenario.Region,
                EcsScenario.CanonicalTags));

        destroyed.Should().BeFalse();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_destroy_of_a_service_in_another_cluster_deletes_nothing()
    {
        var scenario = new EcsScenario();
        scenario.RouteDestroy();

        var destroyed = await scenario.Provisioner()
            .DestroyAsync(EcsScenario.RecordedHandle(EcsScenario.ForeignClusterServiceArn));

        destroyed.Should().BeFalse();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_destroy_never_deletes_the_efs_file_system_or_deregisters_the_task_definition()
    {
        var scenario = new EcsScenario();
        scenario.RouteDestroy();

        await scenario.Provisioner().DestroyAsync(EcsScenario.RecordedHandle());

        // The file system holds the save data and Servyx did not create it. The task definition is free, and
        // deregistering is not deleting.
        scenario.Api.Requests.Should().AllSatisfy(r => r.IsEcs.Should().BeTrue());
        scenario.Api.Requests.Should().NotContain(r => r.EcsAction == "DeregisterTaskDefinition");
        scenario.Api.Requests.Should().NotContain(r => r.EcsAction == "DeleteTaskDefinitions");
    }

    // -----------------------------------------------------------------------------------------------------
    // The reachable adapters are unchanged by this one existing
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_lightsail_adapter_still_hands_back_a_reachable_ssh_target()
    {
        // The positive counterpart of the invariant ResourceReachability replaced: shape I still terminates in
        // something a transport can address, and adding a second unreachable adapter did not change that.
        var resource = await new LightsailScenario().CreateAsync();

        resource.Reachability.Should().BeOfType<ResourceReachability.ViaTransport>();
        resource.RequireTarget().TransportId.Should().Be("ssh");
        resource.TargetOrNull().Should().NotBeNull();
    }
}
