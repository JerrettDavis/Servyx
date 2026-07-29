using System.Collections;
using System.Net;
using System.Reflection;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The adapter's behaviour: planning, launching, refreshing, sweeping, destroying, and the handling of the
/// AWS key pair.
/// </summary>
/// <remarks>
/// Every test here runs against a substituted <see cref="HttpMessageHandler"/>, so no AWS account, IAM
/// credential, or outbound network access is required or attempted.
/// </remarks>
public class AwsEc2ProvisionerTests
{
    // ---------------------------------------------------------------------------------------------------
    // Planning changes nothing - the strongest form of the claim, for a provider whose plans cost money
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanAsync_issues_no_http_request_at_all()
    {
        var scenario = new AwsScenario();

        // The responder throws on any request, so a single call would fail the test where it happened.
        await scenario.Provisioner().PlanAsync(AwsScenario.PalworldInstanceRequest());

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_does_not_even_resolve_the_key_pair()
    {
        var scenario = new AwsScenario();

        await scenario.Provisioner().PlanAsync(AwsScenario.PalworldInstanceRequest());

        // Stronger than "no request was signed": there is no code path from planning to the secret store, so a
        // plan cannot touch the credential even to derive a signing key from it.
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateOperation_makes_no_call_and_resolves_nothing_until_it_is_driven()
    {
        var scenario = new AwsScenario();
        var provisioner = scenario.Provisioner();

        provisioner.CreateOperation(AwsScenario.PalworldInstanceRequest());

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_plan_names_the_launch_the_billable_address_the_wait_and_the_handoff()
    {
        var plan = await new AwsScenario().Provisioner().PlanAsync(AwsScenario.PalworldInstanceRequest());

        plan.Stages.Select(s => s.StageId).Should().Equal(
            "run-instance", "assign-public-ipv4", "await-public-address", "handoff-ssh-target");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == AwsEc2Provisioner.Id);

        // The public IPv4 charge is the most commonly missed line on an EC2 bill, and the cost figure below
        // does not include it - so the plan says so where somebody approving it will read it.
        plan.Stages.Single(s => s.StageId == "assign-public-ipv4").Description
            .Should().Contain("BILLABLE").And.Contain("$0.005/hour");
    }

    [Fact]
    public async Task A_requested_ingress_rule_is_described_as_NOT_APPLIED_rather_than_silently_dropped()
    {
        var request = AwsScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ingress:8211/udp"] = "0.0.0.0/0",
        });

        var plan = await new AwsScenario().Provisioner().PlanAsync(request);

        var stage = plan.Stages.Single(s => s.StageId == "ingress-not-applied");
        stage.Description.Should().StartWith("NOT APPLIED:");
        stage.Description.Should().Contain("udp/8211");

        // The sharper edge than DigitalOcean's: a security group denies inbound by default, so an unapplied
        // rule is an actively closed port rather than merely an un-opened one.
        stage.Description.Should().Contain("actively closed");
    }

    [Fact]
    public async Task A_plan_hash_is_stable_for_one_request_and_changes_when_the_request_does()
    {
        var provisioner = new AwsScenario().Provisioner();

        var first = await provisioner.PlanAsync(AwsScenario.PalworldInstanceRequest());
        var same = await provisioner.PlanAsync(AwsScenario.PalworldInstanceRequest());
        var resized = await provisioner.PlanAsync(AwsScenario.PalworldInstanceRequest(size: "t3.large"));

        first.PlanHash.Should().Be(same.PlanHash);
        first.PlanHash.Should().NotBe(resized.PlanHash);
        first.PlanId.Should().StartWith("aws-ec2:palworld-01:");
    }

    [Fact]
    public void BuildSpec_refuses_a_request_missing_a_required_parameter()
    {
        var provisioner = new AwsScenario().Provisioner();
        var request = new ProvisioningRequest("palworld", "aws-ec2", null, new Dictionary<string, string>(StringComparer.Ordinal));

        var error = Assert.Throws<ArgumentException>(() => provisioner.BuildSpec(request));

        error.Message.Should().Contain("instanceId");
    }

    [Fact]
    public void BuildSpec_takes_the_region_from_the_provisioner_because_ec2_cannot_take_it_from_the_request()
    {
        var scenario = new AwsScenario();

        // The one MachineSpec field this adapter cannot honour per-request: EC2's region is in the endpoint
        // hostname and in the SigV4 credential scope, so it is adapter state. A 'region' provisioning parameter
        // is therefore not recognised at all rather than being accepted and ignored.
        var request = AwsScenario.PalworldInstanceRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["region"] = "eu-west-1",
        });

        scenario.Provisioner(region: "us-east-1").BuildSpec(request).Machine.Region.Should().Be("us-east-1");
    }

    // ---------------------------------------------------------------------------------------------------
    // Launching
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_launch_hands_back_an_ssh_target_a_handle_and_creation_facts()
    {
        var scenario = new AwsScenario();

        var resource = await scenario.CreateAsync();

        resource.Target.Endpoint.Should().Be($"ssh://ec2-user@{AwsScenario.PublicIp}:22");
        resource.Handle.ProvisionerId.Should().Be("aws-ec2");
        resource.Handle.ProviderResourceId.Should().Be(AwsScenario.Ec2InstanceId);
        resource.Handle.Region.Should().Be(AwsScenario.Region);
        resource.ConnectorId.Should().Be(AwsScenario.ConnectorId);
        resource.Facts.PublicAddress.Should().Be(AwsScenario.PublicIp);
        resource.Facts.PrivateAddress.Should().Be(AwsScenario.PrivateIp);
        resource.Facts.CreatedAt.Should().Be(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_launch_polls_for_an_address_because_a_pending_instance_has_none_yet()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        scenario.Api.Requests[0].Action.Should().Be("RunInstances");
        scenario.Api.Requests[0].Method.Should().Be(HttpMethod.Post);
        scenario.Api.Requests.Skip(1).Should().OnlyContain(r => r.Action == "DescribeInstances");
        scenario.Api.Requests.Skip(1).Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Every_servyx_tag_is_applied_by_the_same_call_that_creates_the_instance()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        var launch = scenario.Api.Requests.Single(r => r.Action == "RunInstances");

        launch.ParameterOf("TagSpecification.1.ResourceType").Should().Be("instance");

        var instanceTagKeys = Enumerable.Range(1, 6)
            .Select(i => launch.ParameterOf($"TagSpecification.1.Tag.{i}.Key"))
            .Where(k => k is not null)
            .ToList();

        instanceTagKeys.Should().Contain(["servyx.managed", "servyx.instance-id", "servyx.job-id", "servyx.connector-id"]);

        // There is no CreateTags call anywhere, and that is the point: a follow-up tagging call would open a
        // window in which a billing instance existed untagged and therefore invisible to an orphan sweep.
        scenario.Api.Requests.Should().NotContain(r => r.Action == "CreateTags");
    }

    [Fact]
    public async Task The_launch_also_tags_the_volumes_it_creates_which_is_what_azure_could_not_do()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        var launch = scenario.Api.Requests.Single(r => r.Action == "RunInstances");

        launch.ParameterOf("TagSpecification.2.ResourceType").Should().Be("volume");
        launch.ParameterOf("TagSpecification.2.Tag.1.Key").Should().NotBeNull();

        // The role tag is what tells the two apart afterwards, since a ResourceHandle carries no kind field.
        var instanceRole = RoleValueIn(launch, specificationIndex: 1);
        var volumeRole = RoleValueIn(launch, specificationIndex: 2);

        instanceRole.Should().Be(ServyxEc2Tags.RoleInstance);
        volumeRole.Should().Be(ServyxEc2Tags.RoleVolume);
    }

    [Fact]
    public async Task The_servyx_tag_keys_reach_ec2_with_their_dots_intact_and_no_encoding()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        var body = scenario.Api.Requests.Single(r => r.Action == "RunInstances").Body!;

        // The tag-encoding comparison in one assertion. DigitalOcean has to write servyx_managed:true because
        // its tags accept neither '.' nor '='; EC2 stores the literal key, so no codec exists in this assembly.
        body.Should().Contain("servyx.managed");
        body.Should().NotContain("servyx_managed");
    }

    [Fact]
    public async Task Nothing_is_installed_on_the_machine_and_no_user_data_is_invented()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        var launch = scenario.Api.Requests.Single(r => r.Action == "RunInstances");

        // "Shape I contains no install logic", checkable rather than claimed: with no cloudInit parameter, no
        // UserData is sent at all - not an empty one, not a default bootstrap.
        launch.ParameterOf("UserData").Should().BeNull();
        launch.Body.Should().NotContain("steamcmd");
        launch.Body.Should().NotContain("apt-get");
    }

    [Fact]
    public async Task Caller_supplied_user_data_is_base64_encoded_and_forwarded_verbatim()
    {
        var scenario = new AwsScenario();
        scenario.RouteSuccessfulLaunch();

        const string CloudInit = "#cloud-config\nruncmd:\n  - echo servyx\n";
        var provisioner = scenario.Provisioner();
        var spec = provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["cloudInit"] = CloudInit }));

        await provisioner.CreateOperation(spec).CreateAsync();

        var encoded = scenario.Api.Requests.Single(r => r.Action == "RunInstances").ParameterOf("UserData");

        encoded.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded!)).Should().Be(CloudInit);
    }

    [Fact]
    public async Task A_public_address_is_requested_through_the_network_interface_block_ec2_demands_for_it()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        var launch = scenario.Api.Requests.Single(r => r.Action == "RunInstances");

        // EC2 refuses a request carrying both a top-level SubnetId and a NetworkInterface block, so the subnet
        // moves under the interface when a public address is asked for. Asserted because the alternative is a
        // 400 that only shows up against the real service.
        launch.ParameterOf("NetworkInterface.1.DeviceIndex").Should().Be("0");
        launch.ParameterOf("NetworkInterface.1.AssociatePublicIpAddress").Should().Be("true");
        launch.ParameterOf("NetworkInterface.1.SubnetId").Should().Be("subnet-0123456789abcdef0");
        launch.ParameterOf("NetworkInterface.1.SecurityGroupId.1").Should().Be("sg-0123456789abcdef0");
        launch.ParameterOf("SubnetId").Should().BeNull();
        launch.ParameterOf("SecurityGroupId.1").Should().BeNull();
    }

    [Fact]
    public async Task Suppressing_the_public_address_moves_the_subnet_back_to_the_top_level()
    {
        var scenario = new AwsScenario();
        scenario.RouteSuccessfulLaunch(describeXml: AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(withPublicIp: false)));

        var provisioner = scenario.Provisioner();
        var spec = provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["assignPublicIp"] = "false" }));

        var resource = await provisioner.CreateOperation(spec).CreateAsync();

        var launch = scenario.Api.Requests.Single(r => r.Action == "RunInstances");
        launch.ParameterOf("SubnetId").Should().Be("subnet-0123456789abcdef0");
        launch.ParameterOf("NetworkInterface.1.DeviceIndex").Should().BeNull();

        // With no public address the descriptor names the private one rather than failing: a VPN or bastion
        // deployment is a legitimate shape, and shape I's job is to describe the host it made.
        resource.Target.Endpoint.Should().Be($"ssh://ec2-user@{AwsScenario.PrivateIp}:22");
    }

    [Fact]
    public async Task A_compensating_operation_terminates_the_instance_it_launched()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "RunInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.RunInstancesXml()),
            "DescribeInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DescribeInstancesXml()),
            "TerminateInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.TerminateInstancesXml()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        var provisioner = scenario.Provisioner();
        var operation = provisioner.CreateOperation(provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest()));

        await operation.CreateAsync();
        await operation.CompensateAsync();

        scenario.Api.Requests.Should().Contain(r =>
            r.Action == "TerminateInstances" && r.ParameterOf("InstanceId.1") == AwsScenario.Ec2InstanceId);
    }

    [Fact]
    public async Task A_compensating_operation_that_never_got_an_id_asks_by_tag_instead_of_assuming_nothing_exists()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "DescribeInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DescribeInstancesXml()),
            "TerminateInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.TerminateInstancesXml()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        var provisioner = scenario.Provisioner();
        await provisioner
            .CreateOperation(provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest()))
            .CompensateAsync();

        // For a per-second billed machine the difference between asking and assuming is a machine that bills
        // forever versus one that does not.
        scenario.Api.Requests.Should().Contain(r => r.Action == "DescribeInstances");
        scenario.Api.Requests.Should().Contain(r => r.Action == "TerminateInstances");
    }

    // ---------------------------------------------------------------------------------------------------
    // Refresh
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_reads_the_instance_back_and_rebuilds_an_identical_descriptor()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var resource = await scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle());

        resource.Should().NotBeNull();
        resource!.Target.Endpoint.Should().Be($"ssh://ec2-user@{AwsScenario.PublicIp}:22");
        resource.ConnectorId.Should().Be(AwsScenario.ConnectorId);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_terminated_instance_even_though_ec2_still_describes_it()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: "terminated")));

        var resource = await scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle());

        // The AWS-specific branch: EC2 keeps a terminated instance visible for up to about an hour, complete
        // with its tags and its old addresses. Neither sibling adapter has this problem - a destroyed droplet
        // 404s and a deleted ARM resource 404s - so "gone" is a state here rather than a missing response.
        resource.Should().BeNull();
        scenario.Api.Requests.Should().ContainSingle("EC2 answered normally; the judgement is Servyx's");
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_shutting_down_instance_too()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: "shutting-down")));

        (await scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle())).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_ec2_does_not_know_the_instance_id()
    {
        var scenario = new AwsScenario();
        scenario.RouteMissingInstance();

        (await scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle())).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_an_instance_whose_tags_no_longer_identify_it_as_servyx_managed()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = "somebody-elses" })));

        (await scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle())).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_makes_no_call_at_all_for_a_handle_that_does_not_name_an_instance()
    {
        var scenario = new AwsScenario();

        var resource = await scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle(providerResourceId: "vol-0123"));

        resource.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_distinguishes_a_still_booting_instance_from_a_missing_one()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(state: "pending", withPublicIp: false, withPrivateIp: false)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.Provisioner().RefreshAsync(AwsScenario.RecordedHandle()));

        // Treating "still booting" as "gone" would let a caller conclude a billing instance had disappeared.
        error.Message.Should().Contain("transient boot state");
        error.Message.Should().Contain("must not be treated as gone");
    }

    // ---------------------------------------------------------------------------------------------------
    // The orphan sweep
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_follows_next_token_pagination_to_the_end()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => (request.Action, request.ParameterOf("NextToken")) switch
        {
            ("DescribeInstances", null) => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeInstancesXml("instances-page-2", AwsScenario.InstanceXml("i-00000000000000001"))),
            ("DescribeInstances", "instances-page-2") => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml("i-00000000000000002"))),
            ("DescribeVolumes", null) => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeVolumesXml("volumes-page-2", AwsScenario.VolumeXml("vol-00000000000000001"))),
            ("DescribeVolumes", "volumes-page-2") => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeVolumesXml(null, AwsScenario.VolumeXml("vol-00000000000000002"))),
            _ => throw new InvalidOperationException($"Unexpected page request: {request.Uri}"),
        };

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEc2Provisioner.Id));

        // A sweep that stopped at page one would report two orphans instead of four - silently, with a bill to
        // match. EC2 makes the trap easier to fall into than DigitalOcean does: nextToken is a bare opaque
        // string in the body rather than a ready-made next-page URL.
        handles.Select(h => h.ProviderResourceId).Should().Equal(
            "i-00000000000000001", "i-00000000000000002", "vol-00000000000000001", "vol-00000000000000002");

        scenario.Api.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task ReconcileAsync_finds_a_volume_that_outlived_its_instance()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "DescribeInstances" => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: "terminated"))),
            "DescribeVolumes" => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeVolumesXml(null, AwsScenario.VolumeXml(state: "available"))),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEc2Provisioner.Id));

        // The instance is terminated, so it is not an orphan. Its root volume outlived it - an AMI is free to
        // leave DeleteOnTermination off - and bills per GB-month with nothing pointing at it. This is exactly
        // the case Azure's managed OS disk cannot be found in, and the reason volumes are tagged at launch.
        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be(AwsScenario.VolumeId);
        handles[0].Tags[ServyxEc2Tags.RoleTag].Should().Be(ServyxEc2Tags.RoleVolume);
    }

    [Fact]
    public async Task ReconcileAsync_never_reports_a_terminated_instance_as_an_orphan()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "DescribeInstances" => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeInstancesXml(
                    null,
                    AwsScenario.InstanceXml("i-00000000000000001", state: "running"),
                    AwsScenario.InstanceXml("i-00000000000000002", state: "terminated"))),
            "DescribeVolumes" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DescribeVolumesXml()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEc2Provisioner.Id));

        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be("i-00000000000000001");
    }

    [Fact]
    public async Task ReconcileAsync_re_checks_the_managed_tag_on_every_resource_the_provider_returned()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "DescribeInstances" => AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeInstancesXml(
                    null,
                    AwsScenario.InstanceXml("i-00000000000000001"),
                    AwsScenario.InstanceXml(
                        "i-00000000000000002",
                        tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = "somebody-elses" }))),
            "DescribeVolumes" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DescribeVolumesXml()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEc2Provisioner.Id));

        // The filter is the provider's promise; the second check is this process's own guarantee. A sweep's
        // output is a delete list, and a false positive terminates someone else's instance.
        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be("i-00000000000000001");
    }

    [Fact]
    public async Task ReconcileAsync_sends_the_managed_tag_as_the_filter_ec2_understands()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "DescribeInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DescribeInstancesXml(null)),
            "DescribeVolumes" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DescribeVolumesXml()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide(AwsEc2Provisioner.Id));

        // What a human types into the EC2 console and what the code sends are the same two strings - the win
        // over DigitalOcean, whose filter is an encoded substitute for the real key.
        scenario.Api.Requests.Should().OnlyContain(r => r.ParameterOf("Filter.1.Name") == "tag:servyx.managed");
        scenario.Api.Requests.Should().OnlyContain(r => r.ParameterOf("Filter.1.Value.1") == "true");
        ServyxEc2Tags.ManagedFilterName.Should().Be("tag:servyx.managed");
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_marker_directory_scope_and_makes_no_call()
    {
        var scenario = new AwsScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(
            new OrphanScope.MarkerDirectory(AwsEc2Provisioner.Id, "/var/lib/servyx/instances"));

        // Quietly widening a narrower request into "every managed instance in the region" would hand a caller
        // more resources than it asked to sweep, and a sweep's output is a delete list.
        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_declines_another_provisioners_scope_and_makes_no_call()
    {
        var scenario = new AwsScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("digitalocean-droplet"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_scope_naming_a_region_this_provisioner_cannot_reach()
    {
        var scenario = new AwsScenario();

        var handles = await scenario.Provisioner(region: "us-east-1")
            .ReconcileAsync(new OrphanScope.ProviderWide(AwsEc2Provisioner.Id, "eu-west-1"));

        // Structural, not a policy choice: the EC2 endpoint is regional, so this client cannot see eu-west-1 at
        // all. Answering with us-east-1's instances would hand a caller a delete list for the wrong continent.
        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Destroy
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DestroyAsync_terminates_an_instance_and_deletes_a_volume_by_reading_the_role_off_the_handle()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = request => request.Action switch
        {
            "TerminateInstances" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.TerminateInstancesXml()),
            "DeleteVolume" => AwsApiDouble.Xml(HttpStatusCode.OK, AwsScenario.DeleteVolumeXml()),
            _ => throw new InvalidOperationException($"Unexpected action '{request.Action}'."),
        };

        var provisioner = scenario.Provisioner();

        (await provisioner.DestroyAsync(AwsScenario.RecordedHandle())).Should().BeTrue();
        (await provisioner.DestroyAsync(AwsScenario.RecordedHandle(providerResourceId: AwsScenario.VolumeId))).Should().BeTrue();

        scenario.Api.Requests.Select(r => r.Action).Should().Equal("TerminateInstances", "DeleteVolume");
    }

    [Fact]
    public async Task DestroyAsync_reports_an_already_gone_instance_as_false_rather_than_throwing()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = _ => AwsApiDouble.Xml(
            HttpStatusCode.BadRequest,
            AwsScenario.ErrorXml("InvalidInstanceID.NotFound", "The instance ID does not exist"));

        (await scenario.Provisioner().DestroyAsync(AwsScenario.RecordedHandle())).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyAsync_refuses_a_handle_that_names_neither_an_instance_nor_a_volume()
    {
        var scenario = new AwsScenario();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => scenario.Provisioner().DestroyAsync(AwsScenario.RecordedHandle(providerResourceId: "sg-0123456789abcdef0")));

        error.Message.Should().Contain("will not guess");
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Cost
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_known_instance_type_is_priced_at_its_published_list_price()
    {
        var plan = await new AwsScenario().Provisioner().PlanAsync(AwsScenario.PalworldInstanceRequest());

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.ListPrice);
        plan.EstimatedCost.Hourly.Should().Be(0.0416m);
        plan.EstimatedCost.Currency.Should().Be("USD");
        plan.EstimatedCost.Source.Should().Contain("COMPUTE ONLY");
    }

    [Fact]
    public async Task An_unknown_instance_type_is_reported_as_unknown_rather_than_approximated()
    {
        var plan = await new AwsScenario().Provisioner()
            .PlanAsync(AwsScenario.PalworldInstanceRequest(size: "p5.48xlarge"));

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
        plan.EstimatedCost.Hourly.Should().BeNull();
        plan.EstimatedCost.Monthly.Should().BeNull();
        plan.EstimatedCost.Source.Should().Contain("p5.48xlarge");
    }

    // ---------------------------------------------------------------------------------------------------
    // Capabilities: what is claimed, and - just as load-bearing - what is not
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_claims_exactly_the_capabilities_it_implements() =>
        new AwsScenario().Provisioner().Capabilities.Should().Be(
            ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost
            | ProvisioningCapabilities.UpdateInPlace
            | ProvisioningCapabilities.RecreateToUpdate
            | ProvisioningCapabilities.DetectDrift);

    [Theory]
    [InlineData(ProvisioningCapabilities.Resize)]
    [InlineData(ProvisioningCapabilities.Snapshot)]
    [InlineData(ProvisioningCapabilities.StaticAddress)]
    [InlineData(ProvisioningCapabilities.FirewallRules)]
    public void Every_capability_the_provisioner_does_not_implement_is_absent(ProvisioningCapabilities absent) =>
        // EC2 can do all four; none of them is implemented here. A capability bit is a promise about the
        // adapter, not about the provider - and note that the maintenance half plans a stop/ModifyInstanceAttribute
        // /start sequence in detail while Resize stays absent, because planning one is not performing one.
        new AwsScenario().Provisioner().Capabilities.Should().NotHaveFlag(absent);

    [Fact]
    public void The_provisioner_implements_IMaintainer_and_the_two_ids_agree()
    {
        // The three maintenance bits above are claimed, so the interface backing them has to be there: a
        // capability bit is a promise about this adapter. What that implementation may and may not do is pinned
        // by AwsEc2MaintenanceTests - in particular that it issues no mutating request at all.
        var provisioner = new AwsScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IMaintainer>();
        ((IMaintainer)provisioner).ProvisionerId.Should().Be(AwsEc2Provisioner.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // The AWS key pair
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_request_carries_a_sigv4_signature_scoped_to_the_region_and_to_ec2()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.Authorization!.StartsWith("AWS4-HMAC-SHA256 "));
        scenario.Api.Requests.Should().OnlyContain(r => r.Credential!.EndsWith("/us-east-1/ec2/aws4_request"));
        scenario.Api.Requests.Should().OnlyContain(r => r.Signature!.Length == 64);
        scenario.Api.Requests.Should().OnlyContain(r => r.SignedHeaders!.Contains("host"));
        scenario.Api.Requests.Should().OnlyContain(r => r.SignedHeaders!.Contains("x-amz-date"));
        scenario.Api.Requests.Should().OnlyContain(r => r.AmzDate != null);
    }

    [Fact]
    public async Task What_was_signed_and_what_would_be_sent_are_the_same_query_string()
    {
        var scenario = new AwsScenario();

        await scenario.CreateAsync();

        // The property that makes canonicalisation safe rather than merely correct: the signer rewrites the
        // request's query to its canonical form before signing, so a query written in some other order or with
        // lower-case escapes cannot be signed in one form and sent in another.
        foreach (var request in scenario.Api.Requests)
        {
            AwsSigV4.CanonicalQuery(request.Uri.Query)
                .Should().Be(request.Uri.Query.TrimStart('?'), $"'{request.Uri}' must be sent exactly as signed");
        }
    }

    [Fact]
    public async Task The_key_pair_never_appears_in_anything_the_provisioner_hands_back()
    {
        var scenario = new AwsScenario();

        var resource = await scenario.CreateAsync();
        var plan = await scenario.Provisioner().PlanAsync(AwsScenario.PalworldInstanceRequest());

        var rendered = string.Join(
            "\n",
            resource.Target.TransportId,
            resource.Target.Endpoint,
            resource.Target.CredentialUrn ?? string.Empty,
            resource.Target.DockerContext ?? string.Empty,
            string.Join(",", resource.Target.Options.Select(o => $"{o.Key}={o.Value}")),
            resource.Handle.ProviderResourceId,
            resource.Handle.Region ?? string.Empty,
            string.Join(",", resource.Handle.Tags.Select(t => $"{t.Key}={t.Value}")),
            resource.ConnectorId,
            resource.Facts.PublicAddress ?? string.Empty,
            resource.Facts.PrivateAddress ?? string.Empty,
            resource.Facts.Cost.Source,
            plan.PlanId,
            plan.PlanHash,
            string.Join("\n", plan.Stages.Select(s => s.Description)));

        rendered.Should().NotContain(AwsScenario.SecretAccessKey);
        rendered.Should().NotContain(AwsScenario.AccessKeyId);
        rendered.Should().NotContain("AKIA");

        // The credential URN on the descriptor is the SSH key's URN, never an AWS credential's.
        resource.Target.CredentialUrn.Should().Be(AwsScenario.SshCredentialUrn);
        resource.Target.CredentialUrn.Should().NotBe(AwsScenario.SecretAccessKeyUrn.Value);
    }

    [Fact]
    public async Task The_secret_access_key_is_never_held_in_a_field_anywhere_in_the_provisioners_object_graph()
    {
        var scenario = new AwsScenario();
        scenario.RouteSuccessfulLaunch();

        // The same instance that just signed several requests - the walk has to happen after the key has
        // actually been used, or it proves only that a freshly-built object is clean.
        var provisioner = scenario.Provisioner();
        await provisioner.CreateOperation(provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest())).CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.Authorization != null);

        // Walks every reachable field, not just the provisioner's own: the point is that no layer beneath it
        // (the EC2 client, the signer, the HttpClient's default headers) parked the key, a derived signing key,
        // or a signature either.
        var reachable = FindStrings(provisioner, [], 0);

        reachable.Should().NotBeEmpty("the walk must actually be reaching state, or it proves nothing");
        reachable.Should().Contain(
            AwsScenario.SecretAccessKeyUrn.Value,
            "the URN is held, which is exactly the point - the URN, not the key");
        reachable.Should().NotContain(s => s.Contains(AwsScenario.SecretAccessKey, StringComparison.Ordinal));
        reachable.Should().NotContain(s => s.Contains(AwsScenario.AccessKeyId, StringComparison.Ordinal));
        reachable.Should().NotContain(s => s.StartsWith("AWS4-HMAC-SHA256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_error_is_reported_without_echoing_the_request_that_was_signed()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = _ => AwsApiDouble.Xml(
            HttpStatusCode.Forbidden,
            AwsScenario.ErrorXml("AuthFailure", "AWS was not able to validate the provided access credentials"));

        var provisioner = scenario.Provisioner();
        var spec = provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest());

        var error = await Assert.ThrowsAsync<AwsApiException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        error.ErrorCode.Should().Be("AuthFailure");
        error.ToString().Should().NotContain(AwsScenario.SecretAccessKey);
        error.ToString().Should().NotContain(AwsScenario.AccessKeyId);
        error.ToString().Should().NotContain("AWS4-HMAC-SHA256");
    }

    [Fact]
    public async Task A_missing_credential_is_reported_as_a_missing_secret_rather_than_as_an_http_failure()
    {
        var scenario = new AwsScenario();
        var provisioner = scenario.Provisioner(withCredentials: false);
        var spec = provisioner.BuildSpec(AwsScenario.PalworldInstanceRequest());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.Message.Should().Contain(AwsScenario.AccessKeyIdUrn.Value);
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void This_assembly_references_no_logging_package_so_no_code_path_can_log_the_key_pair()
    {
        var referenced = typeof(AwsEc2Provisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        // "The key is never logged" is usually a review promise. Here it is a fact about the build: there is no
        // logging abstraction in scope for this assembly, so there is no reachable API that could write it.
        referenced.Should().NotContain(n => n.Contains("Logging", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain(n => n.Contains("Diagnostics.Tracing", StringComparison.OrdinalIgnoreCase));

        // And no AWS SDK either, which is the whole premise: SigV4 is hand-rolled over the shared framework.
        referenced.Should().NotContain(n => n.StartsWith("AWSSDK", StringComparison.OrdinalIgnoreCase));
    }

    private static string? RoleValueIn(RecordedRequest request, int specificationIndex)
    {
        for (var i = 1; i <= 8; i++)
        {
            if (request.ParameterOf($"TagSpecification.{specificationIndex}.Tag.{i}.Key") == ServyxEc2Tags.RoleTag)
            {
                return request.ParameterOf($"TagSpecification.{specificationIndex}.Tag.{i}.Value");
            }
        }

        return null;
    }

    private static List<string> FindStrings(object? root, HashSet<object> seen, int depth)
    {
        var found = new List<string>();
        if (root is null || depth > 6)
        {
            return found;
        }

        if (root is string text)
        {
            found.Add(text);
            return found;
        }

        if (root.GetType().IsPrimitive || root is DateTimeOffset or TimeSpan or Uri)
        {
            return found;
        }

        if (!seen.Add(root))
        {
            return found;
        }

        if (root is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                found.AddRange(FindStrings(item, seen, depth + 1));
            }
        }

        foreach (var field in root.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value;
            try
            {
                value = field.GetValue(root);
            }
            catch (Exception)
            {
                continue;
            }

            found.AddRange(FindStrings(value, seen, depth + 1));
        }

        return found;
    }
}
