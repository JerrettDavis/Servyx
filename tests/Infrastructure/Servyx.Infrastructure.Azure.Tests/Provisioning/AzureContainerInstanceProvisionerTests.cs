using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// Tests for the Azure Container Instances adapter — shape M, and the first adapter in this codebase whose
/// output is a resource no transport can reach.
/// </summary>
/// <remarks>
/// <para>
/// Three claims carry most of the weight here and are asserted rather than documented. First, that the
/// adapter never names a transport: not a real one, not a made-up one, not an empty one. Second, that the
/// Azure Files storage account key — which ACI requires as a literal in the ARM body — reaches the container
/// group's PUT and nothing else, in particular nothing durable. Third, that planning is free: no HTTP
/// request, no token exchange, no secret resolution.
/// </para>
/// <para>
/// No <c>Should().Match(x =&gt; x is …)</c> anywhere: that overload compiles to an expression tree, where a
/// pattern-matching operator is a compile error (CS8122). Shapes are asserted with <c>BeOfType</c>.
/// </para>
/// </remarks>
public class AzureContainerInstanceProvisionerTests
{
    // -----------------------------------------------------------------------------------------------------
    // Identity and capabilities
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_id_is_stable()
    {
        new AzureContainerInstanceScenario().Provisioner().ProvisionerId
            .Should().Be("azure-container-instance");
    }

    [Fact]
    public void Capabilities_are_exactly_what_is_implemented()
    {
        new AzureContainerInstanceScenario().Provisioner().Capabilities.Should().Be(
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
    public void Capabilities_Azure_offers_but_this_adapter_does_not_implement_are_absent(ProvisioningCapabilities absent)
    {
        // StaticAddress in particular: ACI's own documentation warns a container group's public IP may change
        // when the group restarts, so claiming it would mislead an operator about something they would pin an
        // RCON client to. FirewallRules likewise: publishing a port is not restricting it.
        new AzureContainerInstanceScenario().Provisioner().Capabilities.HasFlag(absent).Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------------------
    // Planning is free
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Planning_issues_no_http_request_at_all()
    {
        var scenario = new AzureContainerInstanceScenario();

        await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        // Not "no ARM request" - no request of any kind, including the OAuth2 token exchange. A plan cannot
        // create, cannot bill, and cannot transmit either credential.
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Planning_resolves_neither_the_client_secret_nor_the_storage_account_key()
    {
        var scenario = new AzureContainerInstanceScenario();

        await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_states_that_the_storage_account_is_required_billed_separately_and_never_destroyed()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "require-azure-files-share");
        stage.Description.Should().Contain(AzureContainerInstanceScenario.StorageAccountName);
        stage.Description.Should().Contain(AzureContainerInstanceScenario.FileShareName);
        stage.Description.Should().Contain(AzureContainerInstanceScenario.MountPath);
        stage.Description.Should().Contain("BILLED SEPARATELY");
        stage.Description.Should().Contain("NEVER");
    }

    [Fact]
    public async Task A_plan_names_the_storage_key_locator_and_never_the_key()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        var everything = string.Join("\n", plan.Stages.Select(s => s.Description))
            + "\n" + plan.PlanId + "\n" + plan.PlanHash + "\n" + plan.EstimatedCost.Source;

        everything.Should().Contain(AzureContainerInstanceScenario.StorageKeyUrn.Value);
        everything.Should().NotContain(AzureContainerInstanceScenario.StorageAccountKey);
    }

    [Fact]
    public async Task A_plan_says_the_result_will_be_unreachable_and_why()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        var stage = plan.Stages.Single(s => s.StageId == "handoff-unreachable");
        stage.Description.Should().Contain("NO TRANSPORT TARGET");
        stage.Description.Should().Contain("RCON");
        stage.Description.Should().Contain("Provision tier");
    }

    [Fact]
    public async Task A_plan_creates_no_resource_group_and_says_so()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        plan.Stages.Should().ContainSingle(s => s.StageId == "require-resource-group");
        plan.Stages.Select(s => s.StageId).Should().NotContain("create-resource-group");
    }

    [Fact]
    public async Task A_source_cidr_is_reported_as_not_applied_because_ACI_has_no_source_filter()
    {
        var scenario = new AzureContainerInstanceScenario();
        var request = AzureContainerInstanceScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ingress:25575/tcp"] = "198.51.100.0/24" });

        var plan = await scenario.Provisioner().PlanAsync(request);

        var stage = plan.Stages.Single(s => s.StageId == "ingress-source-not-applied");
        stage.Description.Should().Contain("198.51.100.0/24");
        stage.Description.Should().Contain("The port IS published; the source restriction is NOT.");
    }

    [Fact]
    public async Task Ports_without_a_source_cidr_produce_no_not_applied_stage()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        plan.Stages.Select(s => s.StageId).Should().NotContain("ingress-source-not-applied");
    }

    [Fact]
    public async Task The_plan_hash_is_stable_for_the_same_request_and_moves_when_the_mount_moves()
    {
        var scenario = new AzureContainerInstanceScenario();
        var provisioner = scenario.Provisioner();

        var first = await provisioner.PlanAsync(AzureContainerInstanceScenario.PalworldRequest());
        var second = await provisioner.PlanAsync(AzureContainerInstanceScenario.PalworldRequest());
        var moved = await provisioner.PlanAsync(AzureContainerInstanceScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["fileShare"] = "a-different-share" }));

        first.PlanHash.Should().Be(second.PlanHash);
        moved.PlanHash.Should().NotBe(first.PlanHash);
    }

    // -----------------------------------------------------------------------------------------------------
    // Cost is compute-only
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cost_estimate_is_a_list_price_computed_from_the_two_per_second_meters()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        // 2 vCPU * $0.0000135/s + 4 GB * $0.0000015/s, times 3600 seconds.
        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.ListPrice);
        plan.EstimatedCost.Hourly.Should().Be(0.1188m);
        plan.EstimatedCost.Monthly.Should().Be(86.72m);
        plan.EstimatedCost.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task The_cost_estimate_says_out_loud_that_it_excludes_the_storage_account()
    {
        var scenario = new AzureContainerInstanceScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureContainerInstanceScenario.PalworldRequest());

        plan.EstimatedCost.Source.Should().Contain("COMPUTE ONLY");
        plan.EstimatedCost.Source.Should().Contain("storage account");
        plan.EstimatedCost.Source.Should().Contain("not directly comparable");
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 0)]
    [InlineData(-1, 4)]
    public void An_impossible_allocation_is_priced_as_unknown_rather_than_as_zero(int cpu, int memory)
    {
        var estimate = AzureContainerInstancePricing.For(cpu, memory);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();
    }

    // -----------------------------------------------------------------------------------------------------
    // The mandatory mount
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_container_group_spec_cannot_be_built_without_a_persistent_mount()
    {
        // The type, not a validation rule: an unmounted deployment is unrepresentable.
        var act = () => new AzureContainerGroupSpec(
            "g",
            "rg",
            "eastus",
            "image",
            null!,
            ServyxAzureTags.For("i", "j", "c"));

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("storageAccount")]
    [InlineData("fileShare")]
    [InlineData("storageAccountKeyUrn")]
    [InlineData("mountPath")]
    public void A_request_missing_any_part_of_the_mount_is_refused(string missing)
    {
        var request = AzureContainerInstanceScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { [missing] = string.Empty });

        var act = () => AzureContainerInstanceProvisioner.BuildSpec(request);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_default_SecretUrn_is_not_accepted_as_a_storage_key_locator()
    {
        var act = () => new AzureFileShareMount("acct", "share", default, "/data");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_relative_mount_path_is_refused()
    {
        var act = () => new AzureFileShareMount("acct", "share", AzureContainerInstanceScenario.StorageKeyUrn, "data");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_storage_key_locator_must_be_a_urn_and_never_the_key_itself()
    {
        var request = AzureContainerInstanceScenario.PalworldRequest(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["storageAccountKeyUrn"] = AzureContainerInstanceScenario.StorageAccountKey,
            });

        var act = () => AzureContainerInstanceProvisioner.BuildSpec(request);

        act.Should().Throw<ArgumentException>().WithMessage("*secret URN*");
    }

    [Fact]
    public void The_spec_holds_the_key_locator_and_nowhere_holds_the_key()
    {
        var spec = AzureContainerInstanceScenario.Spec();

        spec.Mount.StorageAccountKeyUrn.Should().Be(AzureContainerInstanceScenario.StorageKeyUrn);
        spec.ToString().Should().NotContain(AzureContainerInstanceScenario.StorageAccountKey);
    }

    // -----------------------------------------------------------------------------------------------------
    // Create
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_is_one_arm_write_to_the_container_group()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        var writes = scenario.Api.ArmRequests.Where(r => r.Method == HttpMethod.Put).ToList();
        writes.Should().ContainSingle();
        writes[0].Uri.AbsolutePath.Should().Be(AzureContainerInstanceScenario.GroupId);
    }

    [Fact]
    public async Task The_container_group_write_uses_the_container_instance_api_version()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        // ARM versions each resource provider independently; the resources api-version would be rejected.
        scenario.Api.ArmRequests.Single(r => r.Method == HttpMethod.Put)
            .Uri.Query.Should().Contain("api-version=2023-05-01");
    }

    [Fact]
    public async Task No_resource_group_and_no_storage_resource_is_ever_written()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        // The responder throws for anything that is not a container group, so reaching this line is already
        // the assertion; it is restated explicitly because it is the property, not an implementation detail.
        scenario.Api.ArmRequests.Should().AllSatisfy(r =>
            r.Uri.AbsolutePath.Should().Contain("/containerGroups/"));
        scenario.Api.ArmRequests.Should().NotContain(r => r.Uri.AbsolutePath.Contains("Microsoft.Storage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_storage_account_key_reaches_the_container_group_body_and_nothing_else()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        var carrying = scenario.Api.Requests
            .Where(r => r.Body is not null
                && r.Body.Contains(AzureContainerInstanceScenario.StorageAccountKey, StringComparison.Ordinal))
            .ToList();

        carrying.Should().ContainSingle("ACI accepts no managed identity for an SMB mount, so the key travels "
            + "exactly once - in the one body that needs it");
        carrying[0].Method.Should().Be(HttpMethod.Put);
        carrying[0].Uri.AbsolutePath.Should().Be(AzureContainerInstanceScenario.GroupId);

        // And never anywhere near the token service, which carries the *other* credential.
        scenario.Api.TokenExchanges.Should().NotContain(r =>
            r.Body != null && r.Body.Contains(AzureContainerInstanceScenario.StorageAccountKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Neither_credential_ever_appears_in_an_authorization_header_except_the_derived_token()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        foreach (var request in scenario.Api.ArmRequests)
        {
            request.Authorization.Should().Be("Bearer " + AzureContainerInstanceScenario.AccessToken);
        }
    }

    [Fact]
    public async Task The_storage_account_key_is_resolved_exactly_once_per_create()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        scenario.Secrets.Resolved
            .Count(u => string.Equals(u, AzureContainerInstanceScenario.StorageKeyUrn.Value, StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_create_with_no_stored_storage_key_fails_naming_the_urn_and_creates_nothing()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var operation = scenario.Provisioner(withStorageKey: false)
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest());

        var act = async () => await operation.CreateAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*" + AzureContainerInstanceScenario.StorageKeyUrn.Value + "*");

        scenario.Api.ArmRequests.Should().BeEmpty("the key is resolved before the ARM write, so nothing was created");
    }

    [Fact]
    public async Task A_created_container_group_is_handed_back_as_unreachable_with_a_reason()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var resource = await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        resource.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
        resource.TargetOrNull().Should().BeNull();

        var reason = ((ResourceReachability.NoTransport)resource.Reachability).Reason;
        reason.Should().Be(AzureContainerInstanceProvisioner.UnreachableReason);
        reason.Should().Contain("no sshd");
        reason.Should().Contain("RCON");
    }

    [Fact]
    public async Task No_transport_id_is_fabricated_anywhere_in_the_returned_resource()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var resource = await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        // The failure mode this whole change exists to prevent: a made-up transport id does not fail here, it
        // fails later and elsewhere as "no transport for id", after a billable resource exists.
        var act = () => resource.RequireTarget();

        act.Should().Throw<InvalidOperationException>().WithMessage("*not reachable by any transport*");
    }

    [Fact]
    public async Task The_created_handle_carries_the_identity_role_and_storage_pointers()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var resource = await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        resource.Handle.ProvisionerId.Should().Be(AzureContainerInstanceProvisioner.Id);
        resource.Handle.ProviderResourceId.Should().Be(AzureContainerInstanceScenario.GroupId);
        resource.Handle.Region.Should().Be(AzureContainerInstanceScenario.Region);
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>("servyx.role", "container-group"));
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.azure-storage-account", AzureContainerInstanceScenario.StorageAccountName));
        resource.Handle.Tags.Should().Contain(new KeyValuePair<string, string>(
            "servyx.azure-file-share", AzureContainerInstanceScenario.FileShareName));
        resource.ConnectorId.Should().Be(AzureContainerInstanceScenario.ConnectorId);
    }

    [Fact]
    public async Task No_durable_artifact_of_a_create_carries_the_storage_account_key()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var operation = scenario.Provisioner().CreateOperation(AzureContainerInstanceScenario.PalworldRequest());
        var resource = await operation.CreateAsync();

        // The ledger writes operation.Tags; the caller keeps the handle and the facts. None of them may carry
        // the key, or a credential would become durable somewhere Servyx never intended.
        var durable = string.Join(
            "\n",
            operation.Tags.Select(t => t.Key + "=" + t.Value)
                .Concat(resource.Handle.Tags.Select(t => t.Key + "=" + t.Value))
                .Append(resource.Handle.ProviderResourceId)
                .Append(resource.ConnectorId)
                .Append(resource.Facts.PublicAddress ?? string.Empty)
                .Append(resource.Facts.Cost.Source));

        durable.Should().NotContain(AzureContainerInstanceScenario.StorageAccountKey);
        durable.Should().NotContain(AzureContainerInstanceScenario.ClientSecret);
    }

    [Fact]
    public async Task The_public_address_is_reported_as_a_fact_and_there_is_no_private_address()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var resource = await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        resource.Facts.PublicAddress.Should().Be(AzureContainerInstanceScenario.PublicIp);
        resource.Facts.PrivateAddress.Should().BeNull();
    }

    [Fact]
    public async Task An_address_that_is_not_ready_on_the_write_is_polled_for()
    {
        var scenario = new AzureContainerInstanceScenario();
        var served = 0;

        scenario.Api.Responder = request =>
        {
            if (request.IsTokenExchange)
            {
                return AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.TokenJson());
            }

            var ready = request.Method != HttpMethod.Put && ++served > 1;
            return AzureArmApiDouble.Json(
                HttpStatusCode.Created,
                AzureContainerInstanceScenario.GroupJson(ip: ready ? AzureContainerInstanceScenario.PublicIp : null));
        };

        var resource = await scenario.Provisioner(pollAttempts: 5)
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        resource.Facts.PublicAddress.Should().Be(AzureContainerInstanceScenario.PublicIp);
    }

    [Fact]
    public async Task A_group_that_never_reports_an_address_is_surfaced_as_a_failure_so_it_can_be_compensated()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup(AzureContainerInstanceScenario.GroupJson(ip: null));

        var operation = scenario.Provisioner(pollAttempts: 2)
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest());

        var act = async () => await operation.CreateAsync();

        // Without an address nothing - not even RCON - can reach the workload, and it is billing per second.
        (await act.Should().ThrowAsync<AzureApiException>()).WithMessage("*billing per second*");
    }

    [Fact]
    public async Task The_write_ahead_tags_are_the_tags_that_reach_the_provider()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var operation = scenario.Provisioner().CreateOperation(AzureContainerInstanceScenario.PalworldRequest());
        var recordedBeforeCreate = operation.Tags;

        await operation.CreateAsync();

        var body = scenario.Api.ArmRequests.Single(r => r.Method == HttpMethod.Put).Body!;
        foreach (var tag in recordedBeforeCreate)
        {
            body.Should().Contain("\"" + tag.Key + "\":\"" + tag.Value + "\"");
        }

        operation.Region.Should().Be(AzureContainerInstanceScenario.Region);
        operation.ProvisionerId.Should().Be(AzureContainerInstanceProvisioner.Id);
    }

    [Fact]
    public async Task The_write_mounts_the_share_and_publishes_the_requested_ports()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CreateAsync();

        var body = scenario.Api.ArmRequests.Single(r => r.Method == HttpMethod.Put).Body!;

        body.Should().Contain("\"azureFile\"");
        body.Should().Contain("\"shareName\":\"" + AzureContainerInstanceScenario.FileShareName + "\"");
        body.Should().Contain("\"mountPath\":\"" + AzureContainerInstanceScenario.MountPath + "\"");
        body.Should().Contain("\"port\":8211");
        body.Should().Contain("\"protocol\":\"UDP\"");
        body.Should().Contain("\"port\":25575");
        body.Should().Contain("\"image\":\"" + AzureContainerInstanceScenario.Image + "\"");
    }

    // -----------------------------------------------------------------------------------------------------
    // Compensation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compensation_deletes_the_group_it_created()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CompensateAsync();

        scenario.Api.ArmRequests.Should().Contain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Compensation_leaves_a_name_collision_with_someone_elses_group_alone()
    {
        var scenario = new AzureContainerInstanceScenario();
        var foreignTags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = "someone-elses-instance",
            ["servyx.job-id"] = "j",
            ["servyx.connector-id"] = "c",
        };

        scenario.RespondWithGroup(AzureContainerInstanceScenario.GroupJson(tags: foreignTags));

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CompensateAsync();

        // An ARM PUT is an upsert, so a collision would have updated an existing group rather than created a
        // new one. Deleting by name would then destroy infrastructure Servyx never made.
        scenario.Api.ArmRequests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Compensation_never_touches_the_storage_account()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner()
            .CreateOperation(AzureContainerInstanceScenario.PalworldRequest())
            .CompensateAsync();

        scenario.Api.ArmRequests.Should().NotContain(r =>
            r.Uri.AbsolutePath.Contains("Microsoft.Storage", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------------------
    // Refresh
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_reports_the_group_as_unreachable_too()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var refreshed = await scenario.Provisioner().RefreshAsync(Handle());

        refreshed.Should().NotBeNull();
        refreshed!.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
        refreshed.Facts.PublicAddress.Should().Be(AzureContainerInstanceScenario.PublicIp);
    }

    [Fact]
    public async Task A_refresh_of_a_handle_that_is_not_a_container_group_answers_null_without_calling_azure()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var refreshed = await scenario.Provisioner().RefreshAsync(
            new ResourceHandle(
                AzureContainerInstanceProvisioner.Id,
                AzureContainerInstanceScenario.ForeignVmId,
                AzureContainerInstanceScenario.Region,
                AzureContainerInstanceScenario.CanonicalTags));

        refreshed.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_of_a_group_that_is_no_longer_servyx_managed_answers_null()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup(AzureContainerInstanceScenario.GroupJson(
            tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "someone-else" }));

        (await scenario.Provisioner().RefreshAsync(Handle())).Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_of_a_group_with_no_address_yet_does_not_throw()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup(AzureContainerInstanceScenario.GroupJson(ip: null));

        // Divergence from the VM adapter, and a deliberate one: there, no address means no SSH endpoint can
        // be described, so it throws. Here there is no descriptor either way, so a missing address is a fact.
        var refreshed = await scenario.Provisioner().RefreshAsync(Handle());

        refreshed.Should().NotBeNull();
        refreshed!.Facts.PublicAddress.Should().BeNull();
        refreshed.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
    }

    [Fact]
    public async Task A_refresh_prices_the_group_from_the_allocation_azure_reports()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var refreshed = await scenario.Provisioner().RefreshAsync(Handle());

        refreshed!.Facts.Cost.Hourly.Should().Be(0.1188m);
        refreshed.Facts.Cost.Source.Should().Contain("COMPUTE ONLY");
    }

    // -----------------------------------------------------------------------------------------------------
    // Reconcile
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sweep_finds_managed_container_groups()
    {
        var scenario = new AzureContainerInstanceScenario();
        RespondWithSweep(scenario, AzureContainerInstanceScenario.SweepJson(
            AzureContainerInstanceScenario.SweepRow(
                AzureContainerInstanceScenario.GroupId,
                "Microsoft.ContainerInstance/containerGroups")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureContainerInstanceProvisioner.Id));

        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be(AzureContainerInstanceScenario.GroupId);
        handles[0].ProvisionerId.Should().Be(AzureContainerInstanceProvisioner.Id);
    }

    [Fact]
    public async Task A_sweep_ignores_managed_resources_of_other_types_because_this_adapter_did_not_create_them()
    {
        var scenario = new AzureContainerInstanceScenario();
        RespondWithSweep(scenario, AzureContainerInstanceScenario.SweepJson(
            AzureContainerInstanceScenario.SweepRow(
                AzureContainerInstanceScenario.ForeignVmId,
                "Microsoft.Compute/virtualMachines"),
            AzureContainerInstanceScenario.SweepRow(
                AzureContainerInstanceScenario.GroupId,
                "Microsoft.ContainerInstance/containerGroups")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureContainerInstanceProvisioner.Id));

        // A sweep's output is a delete list, and a handle claiming the wrong provisioner id is how one
        // adapter deletes another's resource.
        handles.Should().ContainSingle();
        handles[0].ProviderResourceId.Should().Be(AzureContainerInstanceScenario.GroupId);
    }

    [Fact]
    public async Task A_sweep_ignores_a_container_group_that_is_not_servyx_managed()
    {
        var scenario = new AzureContainerInstanceScenario();
        RespondWithSweep(scenario, AzureContainerInstanceScenario.SweepJson(
            AzureContainerInstanceScenario.SweepRow(
                AzureContainerInstanceScenario.GroupId,
                "Microsoft.ContainerInstance/containerGroups",
                tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "someone-else" })));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureContainerInstanceProvisioner.Id));

        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_honours_a_region_narrowing()
    {
        var scenario = new AzureContainerInstanceScenario();
        RespondWithSweep(scenario, AzureContainerInstanceScenario.SweepJson(
            AzureContainerInstanceScenario.SweepRow(
                AzureContainerInstanceScenario.GroupId,
                "Microsoft.ContainerInstance/containerGroups",
                location: "westeurope")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureContainerInstanceProvisioner.Id, "eastus"));

        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_never_reports_the_storage_account_because_no_tag_can_reach_it()
    {
        var scenario = new AzureContainerInstanceScenario();

        // Even if the account were somehow tagged, it is not a container group, so it is out of scope here.
        // The real point is stronger and is not testable from inside this adapter: Servyx never creates the
        // account, so it never carries a Servyx tag, so a tag sweep cannot see it at all.
        RespondWithSweep(scenario, AzureContainerInstanceScenario.SweepJson(
            AzureContainerInstanceScenario.SweepRow(
                AzureContainerInstanceScenario.StorageAccountId,
                "Microsoft.Storage/storageAccounts")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureContainerInstanceProvisioner.Id));

        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_for_another_provisioner_reports_nothing_and_calls_nothing()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("azure-vm"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_search_space_shape_this_adapter_does_not_serve_is_declined_without_widening_it()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var handles = await scenario.Provisioner().ReconcileAsync(
            new OrphanScope.MarkerDirectory(AzureContainerInstanceProvisioner.Id, "/var/lib/servyx"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------------
    // Destroy
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_destroy_removes_the_container_group()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var destroyed = await scenario.Provisioner().DestroyAsync(Handle());

        destroyed.Should().BeTrue();
        scenario.Api.ArmRequests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_destroy_never_deletes_the_storage_account_or_the_share_that_holds_the_saves()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner().DestroyAsync(Handle());

        scenario.Api.ArmRequests.Should().NotContain(r =>
            r.Uri.AbsolutePath.Contains("Microsoft.Storage", StringComparison.Ordinal));
        scenario.Api.ArmRequests.Where(r => r.Method == HttpMethod.Delete)
            .Should().AllSatisfy(r => r.Uri.AbsolutePath.Should().Be(AzureContainerInstanceScenario.GroupId));
    }

    [Fact]
    public async Task A_destroy_of_a_handle_this_adapter_could_not_have_created_deletes_nothing()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var destroyed = await scenario.Provisioner().DestroyAsync(
            new ResourceHandle(
                AzureContainerInstanceProvisioner.Id,
                AzureContainerInstanceScenario.ForeignVmId,
                AzureContainerInstanceScenario.Region,
                AzureContainerInstanceScenario.CanonicalTags));

        destroyed.Should().BeFalse();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------------
    // The six existing adapters are unchanged by the domain change
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_virtual_machine_adapter_still_hands_back_a_reachable_ssh_target()
    {
        // The positive counterpart of the invariant that used to be expressed by ProvisionedResource.Target
        // being non-nullable: shape I still terminates in something a transport can address, and the change
        // that made unreachability expressible did not make any existing adapter unreachable.
        var scenario = new AzureScenario();
        scenario.RouteSuccessfulCreate();

        var resource = await scenario.Provisioner()
            .CreateOperation(AzureScenario.PalworldVmRequest())
            .CreateAsync();

        resource.Reachability.Should().BeOfType<ResourceReachability.ViaTransport>();
        resource.RequireTarget().TransportId.Should().Be("ssh");
        resource.TargetOrNull().Should().NotBeNull();
    }

    private static ResourceHandle Handle() => new(
        AzureContainerInstanceProvisioner.Id,
        AzureContainerInstanceScenario.GroupId,
        AzureContainerInstanceScenario.Region,
        AzureContainerInstanceScenario.CanonicalTags);

    private static void RespondWithSweep(AzureContainerInstanceScenario scenario, string sweepJson) =>
        scenario.Api.Responder = request =>
            request.IsTokenExchange
                ? AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.TokenJson())
                : AzureArmApiDouble.Json(HttpStatusCode.OK, sweepJson);
}
