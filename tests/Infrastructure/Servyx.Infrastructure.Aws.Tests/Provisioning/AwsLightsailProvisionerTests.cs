using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The Lightsail adapter's behaviour: planning, creating, refreshing, sweeping, destroying, and the handling of
/// the AWS key pair and the JSON protocol.
/// </summary>
/// <remarks>
/// Every test here runs against a substituted <see cref="HttpMessageHandler"/>, so no AWS account, IAM
/// credential, or outbound network access is required or attempted. The direct counterpart of
/// <c>AwsEc2ProvisionerTests</c>.
/// </remarks>
public class AwsLightsailProvisionerTests
{
    // ---------------------------------------------------------------------------------------------------
    // Planning changes nothing
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanAsync_issues_no_http_request_at_all()
    {
        var scenario = new LightsailScenario();

        await scenario.Provisioner().PlanAsync(LightsailScenario.PalworldInstanceRequest());

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_does_not_even_resolve_the_key_pair()
    {
        var scenario = new LightsailScenario();

        await scenario.Provisioner().PlanAsync(LightsailScenario.PalworldInstanceRequest());

        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateOperation_makes_no_call_and_resolves_nothing_until_it_is_driven()
    {
        var scenario = new LightsailScenario();
        var provisioner = scenario.Provisioner();

        provisioner.CreateOperation(LightsailScenario.PalworldInstanceRequest());

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_plan_names_the_create_the_wait_and_the_handoff_and_no_billable_address_stage()
    {
        var plan = await new LightsailScenario().Provisioner().PlanAsync(LightsailScenario.PalworldInstanceRequest());

        plan.Stages.Select(s => s.StageId).Should().Equal("create-instance", "await-instance-ready", "handoff-ssh-target");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == AwsLightsailProvisioner.Id);

        // Unlike EC2, Lightsail's public IPv4 address is part of the flat bundle price - there is no separately
        // billed "assign-public-ipv4" stage at all here, not merely one with a smaller number in it.
        plan.Stages.Should().NotContain(s => s.StageId.Contains("public", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_create_stage_names_the_bundle_the_zone_the_blueprint_and_the_tag_count()
    {
        var plan = await new LightsailScenario().Provisioner().PlanAsync(LightsailScenario.PalworldInstanceRequest());

        var stage = plan.Stages.Single(s => s.StageId == "create-instance");
        stage.Description.Should().Contain(LightsailScenario.BundleId);
        stage.Description.Should().Contain(LightsailScenario.InstanceName);
        stage.Description.Should().Contain(LightsailScenario.AvailabilityZone);
        stage.Description.Should().Contain(LightsailScenario.BlueprintId);

        // Four canonical tags and nothing else - Lightsail needs no synthetic 'Name' tag (its instance name IS
        // the display name) and no 'servyx.role' tag (there is only one taggable object per launch).
        stage.Description.Should().Contain("4 Servyx tag");
    }

    [Fact]
    public async Task A_requested_ingress_rule_is_described_as_NOT_APPLIED_with_lightsails_own_default_caveat()
    {
        var request = LightsailScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ingress:8211/udp"] = "0.0.0.0/0",
        });

        var plan = await new LightsailScenario().Provisioner().PlanAsync(request);

        var stage = plan.Stages.Single(s => s.StageId == "ingress-not-applied");
        stage.Description.Should().StartWith("NOT APPLIED:");
        stage.Description.Should().Contain("udp/8211");

        // The sharper contrast with EC2's "actively closed" caveat: Lightsail's blueprint default is not
        // deny-all, so the honest caveat here is different in substance, not just in wording.
        stage.Description.Should().Contain("SSH");
        stage.Description.Should().NotContain("actively closed");
    }

    [Fact]
    public async Task A_plan_hash_is_stable_for_one_request_and_changes_when_the_request_does()
    {
        var provisioner = new LightsailScenario().Provisioner();

        var first = await provisioner.PlanAsync(LightsailScenario.PalworldInstanceRequest());
        var same = await provisioner.PlanAsync(LightsailScenario.PalworldInstanceRequest());
        var resized = await provisioner.PlanAsync(LightsailScenario.PalworldInstanceRequest(size: "large_3_0"));

        first.PlanHash.Should().Be(same.PlanHash);
        first.PlanHash.Should().NotBe(resized.PlanHash);
        first.PlanId.Should().StartWith("aws-lightsail:palworld-01:");
    }

    [Fact]
    public void BuildSpec_refuses_a_request_missing_a_required_parameter()
    {
        var provisioner = new LightsailScenario().Provisioner();
        var request = new ProvisioningRequest("palworld", "aws-lightsail", null, new Dictionary<string, string>(StringComparer.Ordinal));

        var error = Assert.Throws<ArgumentException>(() => provisioner.BuildSpec(request));

        error.Message.Should().Contain("instanceId");
    }

    [Fact]
    public void BuildSpec_refuses_a_name_lightsail_would_refuse_before_any_http_call_is_made()
    {
        var provisioner = new LightsailScenario().Provisioner();
        var request = LightsailScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "-leading-hyphen-not-allowed",
        });

        var error = Assert.Throws<ArgumentException>(() => provisioner.BuildSpec(request));

        error.Message.Should().Contain("legal Lightsail instance name");
    }

    [Fact]
    public void BuildSpec_takes_the_region_from_the_provisioner_because_lightsail_cannot_take_it_from_the_request()
    {
        var scenario = new LightsailScenario();

        var request = LightsailScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["region"] = "eu-west-1",
        });

        scenario.Provisioner(region: "us-east-1").BuildSpec(request).Machine.Region.Should().Be("us-east-1");
    }

    [Fact]
    public void BuildSpec_defaults_the_availability_zone_to_the_regions_zone_a_when_none_is_named()
    {
        var scenario = new LightsailScenario();
        var request = LightsailScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["availabilityZone"] = string.Empty,
        });

        scenario.Provisioner(region: "eu-west-1").BuildSpec(request).AvailabilityZone.Should().Be("eu-west-1a");
    }

    [Fact]
    public void BuildSpec_has_no_subnet_or_security_group_parameters_at_all_because_lightsail_has_no_vpc_concept()
    {
        // The concrete shape of "cheaper than EC2": these keys are simply not recognised, not recognised-and-ignored.
        var scenario = new LightsailScenario();
        var request = LightsailScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subnetId"] = "subnet-0123456789abcdef0",
            ["securityGroupId:0"] = "sg-0123456789abcdef0",
        });

        // These parameters are silently unrecognised (not thrown on) - the same permissiveness BuildSpec
        // already has for any key it does not special-case - but they influence nothing about the spec.
        var spec = scenario.Provisioner().BuildSpec(request);

        spec.InstanceName.Should().Be(LightsailScenario.InstanceName);
    }

    // ---------------------------------------------------------------------------------------------------
    // The JSON protocol itself: X-Amz-Target routing, content type, and the plain-text user-data claim
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_request_is_a_post_carrying_an_X_Amz_Target_header_naming_the_action()
    {
        var scenario = new LightsailScenario();
        await scenario.CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Post);
        scenario.Api.Requests.Should().OnlyContain(r => r.Target != null && r.Target.StartsWith("Lightsail_20161128.", StringComparison.Ordinal));
        scenario.Api.Requests.Select(r => r.LightsailAction).Should().Contain(["CreateInstances", "GetInstance"]);
    }

    [Fact]
    public async Task Requests_go_to_the_regional_lightsail_endpoint_not_the_ec2_one()
    {
        var scenario = new LightsailScenario();
        await scenario.CreateAsync();

        scenario.Api.Requests.Should().OnlyContain(r => r.IsLightsail);
        scenario.Api.Requests.Should().NotContain(r => r.IsEc2);
        scenario.Api.Requests.Should().OnlyContain(r => r.Uri.Host == "lightsail.us-east-1.amazonaws.com");
    }

    [Fact]
    public async Task User_data_is_sent_as_plain_text_never_base64_unlike_ec2s_wire_format()
    {
        var scenario = new LightsailScenario();
        var request = LightsailScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cloudInit"] = "echo hello\necho world",
        });

        scenario.RouteSuccessfulCreate();
        await scenario.Provisioner().CreateOperation(request).CreateAsync();

        var create = scenario.Api.Requests.Single(r => r.LightsailAction == "CreateInstances");

        // JSON escapes the embedded newline as the two characters '\' and 'n', not as a base64 blob.
        create.Body.Should().Contain("echo hello\\necho world");
        create.Body.Should().NotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("echo hello\necho world")));
    }

    // ---------------------------------------------------------------------------------------------------
    // Tags applied inline at create - never a follow-up TagResource call
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tags_are_carried_inline_in_the_create_instances_body()
    {
        var scenario = new LightsailScenario();
        await scenario.CreateAsync();

        var create = scenario.Api.Requests.Single(r => r.LightsailAction == "CreateInstances");

        foreach (var tag in LightsailScenario.CanonicalTags)
        {
            create.Body.Should().Contain($"\"key\":\"{tag.Key}\"");
            create.Body.Should().Contain($"\"value\":\"{tag.Value}\"");
        }
    }

    [Fact]
    public async Task No_request_ever_names_the_TagResource_action()
    {
        var scenario = new LightsailScenario();
        await scenario.CreateAsync();

        // The whole point: if tagging ever needed a follow-up call, there would be a window in which a billing
        // instance exists untagged. There is no code path in this assembly that can produce one.
        scenario.Api.Requests.Should().NotContain(r => r.LightsailAction == "TagResource");
    }

    [Fact]
    public void The_production_client_has_no_method_that_could_call_TagResource()
    {
        // The structural half of the same claim: it is not merely untested, it is unreachable.
        var methods = typeof(AwsLightsailProvisioner).Assembly
            .GetType("Servyx.Infrastructure.Aws.LightsailJsonApiClient", throwOnError: true)!
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Select(m => m.Name);

        methods.Should().NotContain(n => n.Contains("TagResource", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Credentials never leak
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_secret_access_key_never_appears_on_the_wire_in_any_request()
    {
        var scenario = new LightsailScenario();
        var resource = await scenario.CreateAsync();

        foreach (var request in scenario.Api.Requests)
        {
            request.Body?.Should().NotContain(LightsailScenario.SecretAccessKey);
            request.Authorization?.Should().NotContain(LightsailScenario.SecretAccessKey);
            request.Uri.ToString().Should().NotContain(LightsailScenario.SecretAccessKey);
        }

        resource.Target.Endpoint.Should().NotContain(LightsailScenario.SecretAccessKey);
        resource.Handle.ProviderResourceId.Should().NotContain(LightsailScenario.SecretAccessKey);
        resource.Target.Options.Values.Should().NotContain(v => v.Contains(LightsailScenario.SecretAccessKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Neither_credential_leaks_into_an_exception_message_on_failure()
    {
        var scenario = new LightsailScenario();
        scenario.RouteMissingInstance();

        var error = await Record.ExceptionAsync(() => scenario.Provisioner().RefreshAsync(LightsailScenario.RecordedHandle()));

        // RefreshAsync itself does not throw here (GetInstance answering NotFoundException maps to null), but
        // the exercise is the same one the EC2 suite runs: force a failure path and check nothing sensitive
        // reached an exception's Message. Provoke a genuine exception via a malformed poll instead.
        error.Should().BeNull();

        scenario.RouteReadOnly(LightsailScenario.InstanceJson(withPublicIp: false, withPrivateIp: false));
        var addressError = await Record.ExceptionAsync(() => scenario.Provisioner().RefreshAsync(LightsailScenario.RecordedHandle()));

        addressError.Should().NotBeNull();
        addressError!.Message.Should().NotContain(LightsailScenario.SecretAccessKey);
        addressError.Message.Should().NotContain(LightsailScenario.AccessKeyId);
    }

    [Fact]
    public async Task The_access_key_id_resolves_fresh_on_every_request_rather_than_being_cached()
    {
        var scenario = new LightsailScenario();
        await scenario.CreateAsync();

        // Mirrors AwsEc2ProvisionerTests' equivalent assertion: SigV4 needs no exchange, so there is nothing to
        // cache and this client re-resolves the key pair from the secret store for every single call.
        scenario.Secrets.Resolved.Count(u => u == LightsailScenario.AccessKeyIdUrn.Value)
            .Should().Be(scenario.Api.Requests.Count);
    }

    // ---------------------------------------------------------------------------------------------------
    // Creating
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_returns_an_ssh_target_using_the_username_lightsail_itself_reported()
    {
        var resource = await new LightsailScenario().CreateAsync();

        resource.Target.TransportId.Should().Be("ssh");
        resource.Target.Endpoint.Should().Be($"ssh://{LightsailScenario.Username}@{LightsailScenario.PublicIp}:22");
    }

    [Fact]
    public async Task CreateAsync_uses_whatever_username_the_blueprint_reports_not_a_hardcoded_default()
    {
        var scenario = new LightsailScenario();
        scenario.RouteSuccessfulCreate(getInstanceJson: LightsailScenario.GetInstanceJson(
            LightsailScenario.InstanceJson(username: "bitnami")));

        var resource = await scenario.Provisioner().CreateOperation(LightsailScenario.PalworldInstanceRequest()).CreateAsync();

        resource.Target.Endpoint.Should().StartWith("ssh://bitnami@");
    }

    [Fact]
    public async Task CreateAsync_falls_back_to_a_default_username_only_when_lightsail_reports_none()
    {
        var scenario = new LightsailScenario();
        scenario.RouteSuccessfulCreate(getInstanceJson: LightsailScenario.GetInstanceJson(
            LightsailScenario.InstanceJson(username: null)));

        var resource = await scenario.Provisioner().CreateOperation(LightsailScenario.PalworldInstanceRequest()).CreateAsync();

        resource.Target.Endpoint.Should().StartWith($"ssh://{AwsLightsailProvisioner.FallbackSshUsername}@");
    }

    [Fact]
    public async Task CreateAsync_polls_get_instance_by_the_caller_chosen_name_because_create_returns_no_instance()
    {
        var scenario = new LightsailScenario();
        scenario.RouteSuccessfulCreate();

        await scenario.Provisioner().CreateOperation(LightsailScenario.PalworldInstanceRequest()).CreateAsync();

        // CreateInstances returns only operations - proving the create response carries no instance is proving
        // this poll is load-bearing rather than a convenience.
        scenario.Api.Requests.Where(r => r.LightsailAction == "GetInstance")
            .Should().OnlyContain(r => r.Body != null && r.Body.Contains($"\"instanceName\":\"{LightsailScenario.InstanceName}\""));
    }

    [Fact]
    public async Task CreateAsync_stamps_the_handle_with_the_instance_name_not_an_arn_or_generated_id()
    {
        var resource = await new LightsailScenario().CreateAsync();

        resource.Handle.ProviderResourceId.Should().Be(LightsailScenario.InstanceName);
        resource.Handle.ProvisionerId.Should().Be(AwsLightsailProvisioner.Id);
        resource.Handle.Region.Should().Be(LightsailScenario.Region);
    }

    [Fact]
    public async Task CreateAsync_reports_the_all_in_bundle_cost_as_the_resources_fact()
    {
        var resource = await new LightsailScenario().CreateAsync();

        resource.Facts.Cost.Confidence.Should().Be(CostConfidence.ListPrice);
        resource.Facts.Cost.Monthly.Should().Be(24m);
    }

    [Fact]
    public async Task CreateAsync_never_opens_a_connection_to_the_machine_it_creates()
    {
        var scenario = new LightsailScenario();
        await scenario.CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().NotContain(r => r.Uri.Host.Contains(LightsailScenario.PublicIp, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Compensation: no tag-sweep fallback needed, unlike EC2
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CompensateAsync_deletes_by_the_name_the_operation_always_knew_even_if_create_never_returned()
    {
        var scenario = new LightsailScenario();

        // The instance never reports an address within the (tiny, test-configured) poll budget, so CreateAsync
        // throws - but the operation was never told a provider-generated id, because there isn't one.
        scenario.Api.Responder = request => request.LightsailAction switch
        {
            "CreateInstances" => AwsApiDouble.Json(HttpStatusCode.OK, LightsailScenario.CreateInstancesJson()),
            "GetInstance" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                LightsailScenario.GetInstanceJson(LightsailScenario.InstanceJson(withPublicIp: false, withPrivateIp: false))),
            "DeleteInstance" => AwsApiDouble.Json(HttpStatusCode.OK, LightsailScenario.DeleteInstanceJson()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.LightsailAction}'."),
        };

        var provisioner = scenario.Provisioner();
        var operation = provisioner.CreateOperation(LightsailScenario.PalworldInstanceRequest());

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.CreateAsync());

        await operation.CompensateAsync();

        var delete = scenario.Api.Requests.Single(r => r.LightsailAction == "DeleteInstance");
        delete.Body.Should().Contain(LightsailScenario.InstanceName);

        // No fallback sweep: unlike AwsEc2Provisioner, there is no GetInstances-by-tag call anywhere in this
        // trace, because compensation never needed one.
        scenario.Api.Requests.Should().NotContain(r => r.LightsailAction == "GetInstances");
    }

    [Fact]
    public async Task CompensateAsync_is_harmless_when_lightsail_never_created_anything_at_all()
    {
        var scenario = new LightsailScenario();
        scenario.RouteMissingInstance();

        var operation = scenario.Provisioner().CreateOperation(LightsailScenario.PalworldInstanceRequest());

        await operation.CompensateAsync();
    }

    // ---------------------------------------------------------------------------------------------------
    // Refreshing
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_reads_the_instance_back_by_name()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var resource = await scenario.Provisioner().RefreshAsync(LightsailScenario.RecordedHandle());

        resource.Should().NotBeNull();
        resource!.Target.Endpoint.Should().Be($"ssh://{LightsailScenario.Username}@{LightsailScenario.PublicIp}:22");
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_deleted_instance()
    {
        var scenario = new LightsailScenario();
        scenario.RouteMissingInstance();

        var resource = await scenario.Provisioner().RefreshAsync(LightsailScenario.RecordedHandle());

        resource.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_an_instance_whose_tags_no_longer_identify_it_as_servyx_managed()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly(LightsailScenario.InstanceJson(tags: new Dictionary<string, string>(StringComparer.Ordinal)));

        var resource = await scenario.Provisioner().RefreshAsync(LightsailScenario.RecordedHandle());

        resource.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_throws_rather_than_reports_gone_for_an_instance_still_booting()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly(LightsailScenario.InstanceJson(withPublicIp: false, withPrivateIp: false));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.Provisioner().RefreshAsync(LightsailScenario.RecordedHandle()));

        error.Message.Should().Contain("transient boot state");
    }

    [Fact]
    public async Task RefreshAsync_ignores_a_handle_with_a_blank_resource_id_without_calling_the_api()
    {
        var scenario = new LightsailScenario();

        var resource = await scenario.Provisioner().RefreshAsync(
            new ResourceHandle(AwsLightsailProvisioner.Id, string.Empty, LightsailScenario.Region, LightsailScenario.CanonicalTags));

        resource.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Reconciling: pagination, scope declination
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_follows_next_page_token_pagination_to_the_end()
    {
        var scenario = new LightsailScenario();

        var page1 = LightsailScenario.GetInstancesJson("page-2", LightsailScenario.InstanceJson(instanceName: "srv-a"));
        var page2 = LightsailScenario.GetInstancesJson(null, LightsailScenario.InstanceJson(instanceName: "srv-b"));
        var seen = new List<string?>();

        scenario.Api.Responder = request =>
        {
            seen.Add(request.Body);
            var isFirstPage = request.Body != null && !request.Body.Contains("pageToken", StringComparison.Ordinal);
            return AwsApiDouble.Json(HttpStatusCode.OK, isFirstPage ? page1 : page2);
        };

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsLightsailProvisioner.Id));

        handles.Select(h => h.ProviderResourceId).Should().Equal("srv-a", "srv-b");
        seen.Should().HaveCount(2);
        seen[1].Should().Contain("page-2");
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_marker_directory_scope_and_makes_no_call()
    {
        var scenario = new LightsailScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(
            new OrphanScope.MarkerDirectory(AwsLightsailProvisioner.Id, "/var/lib/servyx/instances"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_scope_naming_a_different_provisioner()
    {
        var scenario = new LightsailScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("aws-ec2"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_scope_naming_a_different_region()
    {
        var scenario = new LightsailScenario();

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsLightsailProvisioner.Id, "eu-west-1"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_filters_client_side_because_get_instances_has_no_server_side_tag_filter()
    {
        var scenario = new LightsailScenario();

        var untaggedInstance = LightsailScenario.InstanceJson(
            instanceName: "someone-elses-box",
            tags: new Dictionary<string, string>(StringComparer.Ordinal));

        scenario.Api.Responder = _ => AwsApiDouble.Json(
            HttpStatusCode.OK,
            LightsailScenario.GetInstancesJson(null, LightsailScenario.InstanceJson(), untaggedInstance));

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsLightsailProvisioner.Id));

        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be(LightsailScenario.InstanceName);
    }

    // ---------------------------------------------------------------------------------------------------
    // Destroying
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DestroyAsync_deletes_by_name_and_reports_true()
    {
        var scenario = new LightsailScenario();
        scenario.Api.Responder = _ => AwsApiDouble.Json(HttpStatusCode.OK, LightsailScenario.DeleteInstanceJson());

        var destroyed = await scenario.Provisioner().DestroyAsync(LightsailScenario.RecordedHandle());

        destroyed.Should().BeTrue();
    }

    [Fact]
    public async Task DestroyAsync_reports_false_for_an_instance_lightsail_no_longer_knows()
    {
        var scenario = new LightsailScenario();
        scenario.RouteMissingInstance();

        var destroyed = await scenario.Provisioner().DestroyAsync(LightsailScenario.RecordedHandle());

        destroyed.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------------
    // Capabilities: honest, and every omission pinned
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Capabilities_holds_exactly_create_destroy_tag_query_and_estimates_cost()
    {
        var capabilities = new LightsailScenario().Provisioner().Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.Create);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.Destroy);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.TagQuery);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.EstimatesCost);
    }

    [Fact]
    public void Capabilities_does_not_claim_resize_snapshot_static_address_or_firewall_rules()
    {
        var capabilities = new LightsailScenario().Provisioner().Capabilities;

        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.Resize);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.Snapshot);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.StaticAddress);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.FirewallRules);
    }

    [Fact]
    public void Capabilities_does_not_claim_any_maintenance_bit_because_there_is_no_IMaintainer_implementation()
    {
        var capabilities = new LightsailScenario().Provisioner().Capabilities;

        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.UpdateInPlace);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.RecreateToUpdate);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.DetectDrift);

        new LightsailScenario().Provisioner().Should().NotBeAssignableTo<IMaintainer>();
    }

    // ---------------------------------------------------------------------------------------------------
    // Missing credentials
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_fails_with_a_named_urn_when_no_credential_is_stored_rather_than_a_bare_401()
    {
        var scenario = new LightsailScenario();
        scenario.RouteSuccessfulCreate();

        var provisioner = scenario.Provisioner(withCredentials: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.CreateOperation(LightsailScenario.PalworldInstanceRequest()).CreateAsync());

        error.Message.Should().Contain(LightsailScenario.AccessKeyIdUrn.Value);
    }
}
