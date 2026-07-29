using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The behaviour of the Azure virtual-machine adapter itself: planning, authenticating, creating five
/// resources, tagging every one of them, sweeping, refreshing, destroying, and the handling of the service
/// principal's client secret.
/// </summary>
public class AzureVirtualMachineProvisionerTests
{
    [Fact]
    public void The_provisioner_names_itself_stably()
    {
        var scenario = new AzureScenario();

        scenario.Provisioner().ProvisionerId.Should().Be("azure-vm");
        AzureVirtualMachineProvisioner.Id.Should().Be("azure-vm");
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning changes nothing - and here that includes not authenticating
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanAsync_issues_no_http_request_at_all()
    {
        var scenario = new AzureScenario();

        // Any request would throw, but the assertion below is the real one: not "the call failed" but "no call
        // was made". Note the extra force this carries on Azure compared with DigitalOcean - a plan that made
        // any call at all would have to exchange the client secret for a token first, so "no HTTP" is also
        // "the secret was never transmitted".
        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

        plan.Should().NotBeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_does_not_even_resolve_the_client_secret()
    {
        var scenario = new AzureScenario();

        await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

        scenario.Secrets.Resolved.Should().BeEmpty();
        scenario.Api.TokenExchanges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_describes_creating_five_resources_and_stops_there()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

        // The shape claim as a list of stage ids - and the divergence from DigitalOcean made visible at exactly
        // the point a human approves it. The droplet adapter's plan has three stages; this has seven, because
        // an Azure host really is five objects. There is still no install stage, because shape I installs
        // nothing.
        plan.Stages.Select(s => s.StageId).Should().Equal(
            "create-resource-group",
            "create-virtual-network",
            "create-public-ip",
            "create-network-interface",
            "create-virtual-machine",
            "await-public-address",
            "handoff-ssh-target");

        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == AzureVirtualMachineProvisioner.Id);
    }

    [Fact]
    public async Task A_plan_says_which_of_the_five_resources_bill_and_which_survive_a_teardown()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

        // The public address is created two writes before the VM and bills whether or not the VM runs, so the
        // person approving the plan is told so rather than discovering it on an invoice.
        plan.Stages.Single(s => s.StageId == "create-public-ip").Description.Should().Contain("BILLABLE");

        // And the resource group's one-way behaviour is stated up front rather than buried in a destroy path.
        plan.Stages.Single(s => s.StageId == "create-resource-group").Description
            .Should().Contain("NOT deleted by any later teardown");
    }

    [Fact]
    public async Task No_plan_stage_mentions_any_install_verb()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());
        var text = string.Join("\n", plan.Stages.Select(s => s.Description)).ToLowerInvariant();

        // Deliberately literal. If a future edit teaches this adapter to install something, one of these words
        // will appear in the plan it shows the user before the code does anything, and this test fails first.
        foreach (var verb in new[] { "steamcmd", "apt-get", "apt ", "yum", "dnf", "wget", "curl", "tar ", "unzip", "systemctl", "docker run", "chmod" })
        {
            text.Should().NotContain(verb, $"a shape I adapter installs nothing, so its plan cannot mention '{verb}'");
        }
    }

    [Fact]
    public async Task Two_plans_for_the_same_request_hash_identically()
    {
        var scenario = new AzureScenario();
        var provisioner = scenario.Provisioner();

        var first = await provisioner.PlanAsync(AzureScenario.PalworldVmRequest());
        var second = await provisioner.PlanAsync(AzureScenario.PalworldVmRequest());

        second.PlanHash.Should().Be(first.PlanHash);
    }

    [Fact]
    public async Task Changing_the_size_changes_the_plan_hash()
    {
        var scenario = new AzureScenario();
        var provisioner = scenario.Provisioner();

        var first = await provisioner.PlanAsync(AzureScenario.PalworldVmRequest());
        var second = await provisioner.PlanAsync(AzureScenario.PalworldVmRequest(size: "Standard_D2s_v5"));

        second.PlanHash.Should().NotBe(first.PlanHash);
    }

    [Fact]
    public async Task Requested_ingress_rules_are_reported_as_not_applied_rather_than_silently_dropped()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ingress:8211/udp"] = "0.0.0.0/0" }));

        var stage = plan.Stages.Single(s => s.StageId == "ingress-not-applied");
        stage.Description.Should().StartWith("NOT APPLIED:");
        stage.Description.Should().Contain("udp/8211");

        // Sharper here than on DigitalOcean, and the stage says so: with no network security group, ARM's
        // defaults actively deny inbound internet traffic, so the port is closed rather than merely
        // unconfigured.
        stage.Description.Should().Contain("closed, not merely unconfigured");

        scenario.Provisioner().Capabilities.Should().NotHaveFlag(ProvisioningCapabilities.FirewallRules);
    }

    // ---------------------------------------------------------------------------------------------------
    // Cost
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_carries_the_list_price_of_the_size_it_names()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.ListPrice);
        plan.EstimatedCost.Hourly.Should().Be(0.0416m);
        plan.EstimatedCost.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_plan_for_an_unpriced_size_says_unknown_rather_than_guessing()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(
            AzureScenario.PalworldVmRequest(size: "Standard_NC24ads_A100_v4"));

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
        plan.EstimatedCost.Hourly.Should().BeNull();
        plan.EstimatedCost.Monthly.Should().BeNull();
        plan.EstimatedCost.Source.Should().Contain("Standard_NC24ads_A100_v4");
    }

    [Fact]
    public async Task A_cost_figure_says_out_loud_that_it_excludes_the_disk_and_the_address()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

        // A DigitalOcean droplet price is the whole machine. This one is the compute meter only, and this
        // adapter creates a separately-billed disk and a separately-billed static address on every host - so
        // the caveat has to travel with the number onto whatever screen shows it.
        plan.EstimatedCost.Source.Should().Contain("COMPUTE ONLY");
        plan.EstimatedCost.Source.Should().Contain("not directly comparable to an all-in DigitalOcean droplet price");
    }

    // ---------------------------------------------------------------------------------------------------
    // Authentication: the OAuth2 client-credentials exchange
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_is_exchanged_before_any_arm_call_is_attempted()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        // The structural divergence from DigitalOcean, asserted as ordering: the very first thing that happens
        // is a POST to a different host entirely. No ARM call can precede it, because ARM will not accept the
        // stored credential at all.
        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests[0].IsTokenExchange.Should().BeTrue();
        scenario.Api.Requests[0].Method.Should().Be(HttpMethod.Post);
        scenario.Api.Requests[0].Uri.AbsolutePath.Should().Be($"/{AzureScenario.TenantId}/oauth2/v2.0/token");
        scenario.Api.Requests.Skip(1).Should().OnlyContain(r => r.IsArm);
    }

    [Fact]
    public async Task The_token_exchange_sends_exactly_the_client_credentials_grant_arm_requires()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var exchange = scenario.Api.TokenExchanges.Should().ContainSingle().Subject;
        var form = ParseForm(exchange.Body!);

        form["grant_type"].Should().Be("client_credentials");
        form["client_id"].Should().Be(AzureScenario.ClientId);
        form["scope"].Should().Be("https://management.azure.com/.default");
        form["client_secret"].Should().Be(AzureScenario.ClientSecret);

        // The exchange itself carries no bearer header - it is the call that obtains one.
        exchange.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Every_arm_call_carries_the_exchanged_access_token_and_never_the_client_secret()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        scenario.Api.ArmRequests.Should().NotBeEmpty();
        scenario.Api.ArmRequests.Should().OnlyContain(r => r.Authorization == "Bearer " + AzureScenario.AccessToken);
        scenario.Api.ArmRequests.Should().NotContain(r => (r.Authorization ?? string.Empty).Contains(AzureScenario.ClientSecret, StringComparison.Ordinal));
        scenario.Api.ArmRequests.Should().NotContain(r => (r.Body ?? string.Empty).Contains(AzureScenario.ClientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_client_secret_is_resolved_once_per_exchange_not_once_per_request()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        // THE DOCUMENTED DIVERGENCE, pinned so it cannot drift silently in either direction. The DigitalOcean
        // adapter resolves its stored token from the secret store for every single request; this one resolves
        // the client secret once and reuses the derived access token for the whole six-call create sequence.
        // The consequence is precise and is stated on the API client: revoking the stored secret stops the next
        // exchange, not the next request.
        scenario.Secrets.Resolved.Should().ContainSingle();
        scenario.Secrets.Resolved[0].Should().Be(AzureScenario.ClientSecretUrn.Value);
        scenario.Api.ArmRequests.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task A_cached_token_is_reused_across_operations_on_the_same_provisioner()
    {
        var scenario = new AzureScenario();
        scenario.RouteSuccessfulCreate();

        var provisioner = scenario.Provisioner();
        var spec = AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest());

        await provisioner.CreateOperation(spec).CreateAsync();
        await provisioner.RefreshAsync(new ResourceHandle(
            AzureVirtualMachineProvisioner.Id,
            AzureScenario.VmId,
            AzureScenario.Region,
            AzureScenario.CanonicalVmTags));

        scenario.Api.TokenExchanges.Should().ContainSingle("the token is cached for the lifetime Entra ID stated");
        scenario.Secrets.Resolved.Should().ContainSingle();
    }

    [Fact]
    public async Task A_missing_client_secret_is_reported_as_a_missing_secret_rather_than_as_an_http_failure()
    {
        var scenario = new AzureScenario();
        var provisioner = scenario.Provisioner(withSecret: false);
        var spec = AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.Message.Should().Contain(AzureScenario.ClientSecretUrn.Value);
        scenario.Api.Requests.Should().BeEmpty("nothing is sent anywhere when the credential cannot be resolved");
    }

    [Fact]
    public async Task A_rejected_token_exchange_fails_before_any_resource_is_created()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = _ => AzureArmApiDouble.Json(
            HttpStatusCode.Unauthorized,
            """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided."}""");

        var provisioner = scenario.Provisioner();
        var spec = AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest());

        var error = await Assert.ThrowsAsync<AzureApiException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        scenario.Api.ArmRequests.Should().BeEmpty("a failed exchange must not be followed by a partial create");
    }

    [Fact]
    public async Task A_provider_error_is_reported_without_echoing_the_credential_that_carried_it()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.Forbidden,
                """{"error":{"code":"AuthorizationFailed","message":"The client does not have authorization."}}""");

        var provisioner = scenario.Provisioner();
        var spec = AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest());

        var error = await Assert.ThrowsAsync<AzureApiException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        error.ToString().Should().NotContain(AzureScenario.ClientSecret);
        error.ToString().Should().NotContain(AzureScenario.AccessToken);
    }

    [Fact]
    public async Task The_client_secret_never_appears_in_anything_the_provisioner_hands_back()
    {
        var scenario = new AzureScenario();

        var resource = await scenario.CreateAsync();
        var plan = await scenario.Provisioner().PlanAsync(AzureScenario.PalworldVmRequest());

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

        rendered.Should().NotContain(AzureScenario.ClientSecret);
        rendered.Should().NotContain("azsec_v1");
        rendered.Should().NotContain(AzureScenario.AccessToken);

        // The credential URN on the descriptor is the SSH key's URN, never the Azure client secret's.
        resource.Target.CredentialUrn.Should().Be(AzureScenario.SshCredentialUrn);
        resource.Target.CredentialUrn.Should().NotBe(AzureScenario.ClientSecretUrn.Value);
    }

    [Fact]
    public async Task The_client_secret_is_never_held_in_a_field_anywhere_in_the_provisioners_object_graph()
    {
        var scenario = new AzureScenario();
        scenario.RouteSuccessfulCreate();

        // The same instance that just authenticated several requests - the walk has to happen after the secret
        // has actually been used, or it proves only that a freshly-built object is clean.
        var provisioner = scenario.Provisioner();
        await provisioner
            .CreateOperation(AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest()))
            .CreateAsync();

        scenario.Api.ArmRequests.Should().NotBeEmpty();
        scenario.Api.ArmRequests.Should().OnlyContain(r => r.Authorization != null);

        var reachable = FindStrings(provisioner, [], 0);

        reachable.Should().NotBeEmpty("the walk must actually be reaching state, or it proves nothing");
        reachable.Should().Contain(
            AzureScenario.ClientSecretUrn.Value,
            "the URN is held, which is exactly the point - the URN, not the secret");
        reachable.Should().NotContain(s => s.Contains(AzureScenario.ClientSecret, StringComparison.Ordinal));
        reachable.Should().NotContain(s => s.Contains("azsec_v1", StringComparison.Ordinal));

        // And the honest other half, asserted rather than glossed: the DERIVED access token IS held, because
        // Azure's model forces a cache where DigitalOcean's does not. The tenant id and client id are held too
        // and are identifiers rather than secrets. Anyone tightening this file should tighten the line above,
        // not this one - deleting this assertion would hide a real, deliberate divergence.
        reachable.Should().Contain(s => s.Contains(AzureScenario.AccessToken, StringComparison.Ordinal));
    }

    [Fact]
    public void This_assembly_references_no_logging_package_so_no_code_path_can_log_the_secret()
    {
        var referenced = typeof(AzureVirtualMachineProvisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        // "The secret is never logged" is usually a review promise. Here it is a fact about the build: there is
        // no logging abstraction in scope for this assembly, so there is no reachable API that could write it.
        referenced.Should().NotContain(n => n.Contains("Logging", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain(n => n.Contains("Diagnostics.Tracing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void This_assembly_takes_no_azure_sdk_dependency()
    {
        var referenced = typeof(AzureVirtualMachineProvisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        // The .csproj argues at length for hand-rolling ARM REST instead of taking Azure.ResourceManager.* and
        // Azure.Identity. This turns that argument into a fact about the build - in particular, no
        // ambient-credential discovery chain can exist in an assembly that cannot reference one.
        referenced.Should().NotContain(n => n.StartsWith("Azure.", StringComparison.Ordinal));
        referenced.Should().NotContain(n => n.Contains("Microsoft.Identity", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------------
    // The multi-resource create sequence
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_writes_the_five_resources_in_dependency_order()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var writes = scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Put)
            .Select(r => r.Uri.AbsolutePath)
            .ToList();

        // Not an implementation detail: ARM refuses a NIC that references a subnet that does not exist yet, and
        // refuses a VM that references a NIC that does not exist yet. This ordering is the create half of the
        // property that most clearly breaks "a second cloud adapter is a mechanical repeat".
        writes.Should().Equal(
            AzureScenario.ResourceGroupId,
            AzureScenario.VirtualNetworkId,
            AzureScenario.PublicIpId,
            AzureScenario.NicId,
            AzureScenario.VmId);
    }

    [Fact]
    public async Task Create_sends_the_vm_shape_the_machine_spec_describes()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var body = JsonDocument.Parse(VmWriteBody(scenario)).RootElement;

        body.GetProperty("location").GetString().Should().Be("eastus");

        var properties = body.GetProperty("properties");
        properties.GetProperty("hardwareProfile").GetProperty("vmSize").GetString().Should().Be("Standard_B2s");

        var image = properties.GetProperty("storageProfile").GetProperty("imageReference");
        image.GetProperty("publisher").GetString().Should().Be("Canonical");
        image.GetProperty("offer").GetString().Should().Be("ubuntu-24_04-lts");
        image.GetProperty("sku").GetString().Should().Be("server");
        image.GetProperty("version").GetString().Should().Be("latest");
    }

    [Fact]
    public async Task Create_sends_the_operators_raw_public_key_and_disables_password_login()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var osProfile = JsonDocument.Parse(VmWriteBody(scenario)).RootElement
            .GetProperty("properties").GetProperty("osProfile");

        // The one place shape I fits Azure BETTER than DigitalOcean: MachineSpec.SshPublicKey holds raw key
        // material, which POST /v2/droplets cannot consume at all. ARM takes it directly, so no account-level
        // key registration step exists here.
        osProfile.GetProperty("adminUsername").GetString().Should().Be("azureuser");

        var linux = osProfile.GetProperty("linuxConfiguration");
        linux.GetProperty("disablePasswordAuthentication").GetBoolean().Should().BeTrue();

        var key = linux.GetProperty("ssh").GetProperty("publicKeys").EnumerateArray().Single();
        key.GetProperty("keyData").GetString().Should().Be(AzureScenario.SshPublicKey);
        key.GetProperty("path").GetString().Should().Be("/home/azureuser/.ssh/authorized_keys");
    }

    [Fact]
    public async Task Create_sends_no_custom_data_when_the_caller_supplied_none()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var osProfile = JsonDocument.Parse(VmWriteBody(scenario)).RootElement
            .GetProperty("properties").GetProperty("osProfile");

        // The single most important assertion for the "no install logic" claim. This adapter authors no
        // cloud-init, so a request that asked for none must send none - not a Servyx bootstrap script, not a
        // package list, not a game payload.
        osProfile.TryGetProperty("customData", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Create_base64_encodes_caller_supplied_cloud_init_without_adding_to_it()
    {
        var scenario = new AzureScenario();
        const string CloudInit = "#cloud-config\nusers:\n  - name: steam\n";

        await scenario.CreateAsync(AzureScenario.PalworldVmRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["cloudInit"] = CloudInit }));

        var customData = JsonDocument.Parse(VmWriteBody(scenario)).RootElement
            .GetProperty("properties").GetProperty("osProfile").GetProperty("customData").GetString();

        // Encoded, not authored, and the round trip proves it: ARM's customData is base64 where DigitalOcean's
        // user_data is plain text, so this is a real transformation - and it is the only transformation applied
        // to caller content anywhere in this assembly.
        Encoding.UTF8.GetString(Convert.FromBase64String(customData!)).Should().Be(CloudInit);
    }

    [Fact]
    public async Task Create_declares_that_the_os_disk_dies_with_the_vm()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var osDisk = JsonDocument.Parse(VmWriteBody(scenario)).RootElement
            .GetProperty("properties").GetProperty("storageProfile").GetProperty("osDisk");

        // Load-bearing rather than tidy. The managed OS disk is created implicitly by this write, so Servyx
        // never tags it and no sweep can ever find it. Azure's default is to DETACH it on VM deletion, which
        // would leave an untagged, unsweepable, per-GB-billing disk behind after every destroy. This is the
        // only point at which the adapter can close that hole.
        osDisk.GetProperty("deleteOption").GetString().Should().Be("Delete");
        osDisk.GetProperty("createOption").GetString().Should().Be("FromImage");
    }

    [Fact]
    public async Task Create_binds_the_network_interface_to_both_the_subnet_and_the_public_address()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var nicBody = scenario.Api.ArmRequests
            .Single(r => r.Method == HttpMethod.Put && r.Uri.AbsolutePath == AzureScenario.NicId).Body!;

        var ipConfiguration = JsonDocument.Parse(nicBody).RootElement
            .GetProperty("properties").GetProperty("ipConfigurations").EnumerateArray().Single()
            .GetProperty("properties");

        ipConfiguration.GetProperty("subnet").GetProperty("id").GetString()
            .Should().Be(AzureScenario.VirtualNetworkId + "/subnets/palworld-01-subnet");
        ipConfiguration.GetProperty("publicIPAddress").GetProperty("id").GetString()
            .Should().Be(AzureScenario.PublicIpId);
    }

    [Fact]
    public async Task Create_writes_the_subnet_inline_because_ARM_gives_it_nowhere_else_to_live()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var vnetBody = scenario.Api.ArmRequests
            .Single(r => r.Method == HttpMethod.Put && r.Uri.AbsolutePath == AzureScenario.VirtualNetworkId).Body!;

        var subnet = JsonDocument.Parse(vnetBody).RootElement
            .GetProperty("properties").GetProperty("subnets").EnumerateArray().Single();

        subnet.GetProperty("name").GetString().Should().Be("palworld-01-subnet");

        // And the finding this pins: the subnet carries no tags, because ARM sub-resources have no tags
        // collection at all. It is the one created object an orphan sweep can never see directly.
        subnet.TryGetProperty("tags", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Create_waits_for_the_address_before_describing_a_target()
    {
        var scenario = new AzureScenario();
        var addressReads = 0;

        scenario.Api.Responder = request =>
        {
            if (AzureScenario.RouteTokenExchange(request) is { } token)
            {
                return token;
            }

            if (request.Method == HttpMethod.Get
                && request.Uri.AbsolutePath == AzureScenario.PublicIpId)
            {
                addressReads++;
                return AzureArmApiDouble.Json(
                    HttpStatusCode.OK,
                    AzureScenario.PublicIpJson(ipAddress: addressReads > 1 ? AzureScenario.PublicIp : null));
            }

            return request.Method == HttpMethod.Put
                ? AzureArmApiDouble.Json(HttpStatusCode.Created, AzureScenario.PayloadFor(request))
                : AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request));
        };

        var provisioner = scenario.Provisioner();
        var spec = AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest());

        var resource = await provisioner.CreateOperation(spec).CreateAsync();

        addressReads.Should().Be(2);
        resource.Target.Endpoint.Should().Be($"ssh://azureuser@{AzureScenario.PublicIp}:22");
    }

    // ---------------------------------------------------------------------------------------------------
    // Tagging - every resource, no encoding
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_stamps_the_canonical_servyx_tags_on_the_virtual_machine_with_no_encoding_at_all()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var tags = ReadTags(VmWriteBody(scenario));

        // The exact wire strings, spelled out. Compare with the DigitalOcean suite, which has to pin
        // "servyx_managed:true" because a droplet tag cannot contain '.' or '='. Here the key that reaches the
        // provider is the key Servyx defines, character for character, and that is the tagging finding.
        tags["servyx.managed"].Should().Be("true");
        tags["servyx.instance-id"].Should().Be("srv-0001");
        tags["servyx.job-id"].Should().Be("job-42");
        tags["servyx.connector-id"].Should().Be("conn-1");

        tags.Keys.Should().NotContain(k => k.Contains("servyx_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_records_the_names_of_the_vms_four_siblings_on_the_vm_itself()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var tags = ReadTags(VmWriteBody(scenario));

        // ResourceHandle carries exactly one ProviderResourceId, which fits a droplet and does not fit a
        // five-object host. The sibling names go where a handle already carries free-form state - and they
        // survive at the provider even when Servyx's local record does not.
        tags["servyx.role"].Should().Be("virtual-machine");
        tags["servyx.azure-resource-group"].Should().Be(AzureScenario.ResourceGroup);
        tags["servyx.azure-virtual-network"].Should().Be("palworld-01-vnet");
        tags["servyx.azure-subnet"].Should().Be("palworld-01-subnet");
        tags["servyx.azure-public-ip"].Should().Be("palworld-01-ip");
        tags["servyx.azure-network-interface"].Should().Be("palworld-01-nic");
    }

    [Theory]
    [InlineData("resource-group")]
    [InlineData("virtual-network")]
    [InlineData("public-ip")]
    [InlineData("network-interface")]
    public async Task Every_subsidiary_resource_carries_the_full_canonical_identity_not_a_back_reference(string role)
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync();

        var body = scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Put)
            .Select(r => r.Body!)
            .Single(b => ReadTags(b).TryGetValue("servyx.role", out var value) && value == role);

        var tags = ReadTags(body);

        // Each resource is independently attributable to a Servyx instance. That is what lets the sweep find a
        // stranded public address without the VM - which matters, because the address is created two writes
        // before the VM and can outlive a failed create.
        tags["servyx.managed"].Should().Be("true");
        tags["servyx.instance-id"].Should().Be(AzureScenario.InstanceId);
        tags["servyx.job-id"].Should().Be(AzureScenario.JobId);
        tags["servyx.connector-id"].Should().Be(AzureScenario.ConnectorId);
    }

    [Fact]
    public async Task A_caller_supplied_tag_cannot_shadow_a_canonical_one_on_any_resource()
    {
        var scenario = new AzureScenario();

        await scenario.CreateAsync(AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tag:servyx.managed"] = "false",
            ["tag:servyx.instance-id"] = "somebody-elses-instance",
            ["tag:owner"] = "ops",
        }));

        foreach (var write in scenario.Api.ArmRequests.Where(r => r.Method == HttpMethod.Put))
        {
            var tags = ReadTags(write.Body!);

            // A caller who could set servyx.managed=false could hide a billing resource from every sweep.
            tags["servyx.managed"].Should().Be("true");
            tags["servyx.instance-id"].Should().Be(AzureScenario.InstanceId);
            tags["owner"].Should().Be("ops");
        }
    }

    [Fact]
    public void An_instance_id_containing_a_dot_is_accepted_where_the_digitalocean_adapter_refuses_it()
    {
        // The concrete, user-visible consequence of Azure needing no tag encoding. ServyxDropletTags.For
        // rejects this id outright, because a DigitalOcean tag cannot carry '.' and mangling it would make a
        // later sweep misattribute the droplet. ARM tag values have no charset restriction at all.
        var tags = ServyxAzureTags.For("srv.0001.eu", "job.42", "conn.1");

        tags.ToTags()["servyx.instance-id"].Should().Be("srv.0001.eu");
    }

    // ---------------------------------------------------------------------------------------------------
    // Handle and facts
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_hands_back_a_handle_that_names_the_vms_arm_id_and_its_region()
    {
        var scenario = new AzureScenario();

        var resource = await scenario.CreateAsync();

        resource.Handle.ProvisionerId.Should().Be("azure-vm");
        resource.Handle.ProviderResourceId.Should().Be(AzureScenario.VmId);
        resource.Handle.Region.Should().Be("eastus");
        resource.Handle.Tags[ServyxTagKeys.Managed].Should().Be("true");
        resource.Handle.Tags[ServyxTagKeys.InstanceId].Should().Be(AzureScenario.InstanceId);
        resource.ConnectorId.Should().Be(AzureScenario.ConnectorId);
    }

    [Fact]
    public async Task Create_reports_both_addresses_and_the_compute_list_price_as_facts()
    {
        var scenario = new AzureScenario();

        var resource = await scenario.CreateAsync();

        resource.Facts.PublicAddress.Should().Be(AzureScenario.PublicIp);
        resource.Facts.PrivateAddress.Should().Be(AzureScenario.PrivateIp);
        resource.Facts.Cost.Confidence.Should().Be(CostConfidence.ListPrice);
        resource.Facts.Cost.Hourly.Should().Be(0.0416m);
        resource.Facts.CreatedAt.Should().Be(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_operation_publishes_its_tags_before_it_creates_anything()
    {
        var scenario = new AzureScenario();
        scenario.RouteSuccessfulCreate();

        var operation = scenario.Provisioner()
            .CreateOperation(AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest()));

        // Read exactly as the executor reads them: before CreateAsync, so they can go into the write-ahead
        // ledger and a resource created but never acknowledged is still findable by tag. Note this is worth
        // more here than on DigitalOcean - there are five resources that can reach that state, not one.
        var tagsBefore = operation.Tags;
        scenario.Api.Requests.Should().BeEmpty();

        var resource = await operation.CreateAsync();

        tagsBefore.Should().BeEquivalentTo(resource.Handle.Tags);
        operation.Region.Should().Be("eastus");
        operation.ProvisionerId.Should().Be("azure-vm");
    }

    // ---------------------------------------------------------------------------------------------------
    // Refresh
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_deleted_vm()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.NotFound,
                """{"error":{"code":"ResourceNotFound","message":"The Resource was not found."}}""");

        var refreshed = await scenario.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_vm_that_is_not_servyx_managed()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.VirtualMachineJson(tags: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["environment"] = "production",
                    ["owner"] = "someone-else",
                }));

        var refreshed = await scenario.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_handle_that_does_not_name_a_virtual_machine()
    {
        var scenario = new AzureScenario();
        var handle = new ResourceHandle(
            AzureVirtualMachineProvisioner.Id,
            AzureScenario.PublicIpId,
            AzureScenario.Region,
            new Dictionary<string, string>(StringComparer.Ordinal));

        var refreshed = await scenario.Provisioner().RefreshAsync(handle);

        // Not even a token exchange: a handle that plainly is not a VM is answered from its own text.
        refreshed.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_walks_the_vm_to_its_nic_to_its_address_and_rebuilds_the_descriptor()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Requests.Clear();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request));

        var refreshed = await scenario.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();

        // Three ARM reads where a droplet needs one, because a VM carries only a reference to a NIC, which
        // carries a reference to a public address. That cost is the shape difference, stated as a count.
        scenario.Api.ArmRequests.Select(r => r.Uri.AbsolutePath)
            .Should().Equal(AzureScenario.VmId, AzureScenario.NicId, AzureScenario.PublicIpId);

        // Compared field by field rather than with record equality: TargetDescriptor's Options is an
        // IReadOnlyDictionary, which the compiler-generated record Equals compares by reference.
        refreshed!.Target.TransportId.Should().Be(resource.Target.TransportId);
        refreshed.Target.Endpoint.Should().Be(resource.Target.Endpoint);
        refreshed.Target.CredentialUrn.Should().Be(resource.Target.CredentialUrn);
        refreshed.Target.DockerContext.Should().Be(resource.Target.DockerContext);
        refreshed.Target.Options.Should().BeEquivalentTo(resource.Target.Options);
    }

    // ---------------------------------------------------------------------------------------------------
    // Reconcile
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_asks_arm_for_every_resource_carrying_the_managed_tag()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(null, AzureScenario.SweptHostResources()));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        var sweep = scenario.Api.ArmRequests.Should().ContainSingle().Subject;
        sweep.Uri.AbsolutePath.Should().Be($"/subscriptions/{AzureScenario.SubscriptionId}/resources");
        Uri.UnescapeDataString(sweep.Uri.Query).Should()
            .Contain("tagName eq 'servyx.managed' and tagValue eq 'true'");

        handles.Should().OnlyContain(h => h.ProvisionerId == "azure-vm");
        handles.Should().OnlyContain(h => h.Tags[ServyxTagKeys.InstanceId] == AzureScenario.InstanceId);
    }

    [Fact]
    public async Task ReconcileAsync_finds_the_orphanable_siblings_and_not_only_the_virtual_machine()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(null, AzureScenario.SweptHostResources()));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        // The heart of the multi-resource orphan story. A DigitalOcean sweep returns droplets because a droplet
        // IS the host. Here the same filter returns all four tagged resource types - and it must, because a
        // create that fails at the VM write leaves a billable public address behind with no VM to hang it from.
        // The order is dependents first, because ARM refuses to delete a resource another one still references.
        handles.Select(h => h.ProviderResourceId).Should().Equal(
            AzureScenario.VmId,
            AzureScenario.NicId,
            AzureScenario.PublicIpId,
            AzureScenario.VirtualNetworkId);
    }

    [Fact]
    public async Task ReconcileAsync_re_checks_the_tag_on_every_resource_arm_returned()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(
                    null,
                    AzureScenario.ResourceSummaryJson(AzureScenario.VmId, "Microsoft.Compute/virtualMachines", "palworld-01"),
                    AzureScenario.ResourceSummaryJson(
                        AzureScenario.ResourceGroupId + "/providers/Microsoft.Compute/virtualMachines/someone-else",
                        "Microsoft.Compute/virtualMachines",
                        "someone-else",
                        tags: new Dictionary<string, string>(StringComparer.Ordinal) { ["environment"] = "production" })));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        // The provider's filter is its promise; this second check is Servyx's own guarantee. A sweep's output is
        // a delete list, so a resource Servyx did not tag must never appear in it even if ARM says it did.
        handles.Select(h => h.ProviderResourceId).Should().Equal(AzureScenario.VmId);
    }

    [Fact]
    public async Task ReconcileAsync_follows_pagination_rather_than_stopping_at_the_first_page()
    {
        var scenario = new AzureScenario();
        var page = 0;

        scenario.Api.Responder = request =>
        {
            if (AzureScenario.RouteTokenExchange(request) is { } token)
            {
                return token;
            }

            page++;
            return AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                page == 1
                    ? AzureScenario.ResourceListJson(
                        "https://management.azure.com/subscriptions/" + AzureScenario.SubscriptionId + "/resources?$skiptoken=abc",
                        AzureScenario.ResourceSummaryJson(AzureScenario.VmId, "Microsoft.Compute/virtualMachines", "palworld-01"))
                    : AzureScenario.ResourceListJson(
                        null,
                        AzureScenario.ResourceSummaryJson(AzureScenario.PublicIpId, "Microsoft.Network/publicIPAddresses", "palworld-01-ip", tags: AzureScenario.SiblingTags("public-ip"))));
        };

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id));

        // A sweep that stopped at page one would report "no orphans beyond page one" as "no orphans" - the exact
        // failure TagQuery exists to prevent, for resources that bill by the hour.
        scenario.Api.ArmRequests.Should().HaveCount(2);
        handles.Select(h => h.ProviderResourceId).Should().Equal(AzureScenario.VmId, AzureScenario.PublicIpId);
    }

    [Fact]
    public async Task ReconcileAsync_narrows_to_a_region_when_the_scope_names_one()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.OK,
                AzureScenario.ResourceListJson(
                    null,
                    AzureScenario.ResourceSummaryJson(AzureScenario.VmId, "Microsoft.Compute/virtualMachines", "palworld-01"),
                    AzureScenario.ResourceSummaryJson(
                        AzureScenario.ResourceGroupId + "/providers/Microsoft.Compute/virtualMachines/palworld-eu",
                        "Microsoft.Compute/virtualMachines",
                        "palworld-eu",
                        location: "westeurope")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(AzureVirtualMachineProvisioner.Id, "westeurope"));

        handles.Select(h => h.Region).Should().Equal("westeurope");
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_marker_directory_scope_and_makes_no_api_call()
    {
        var scenario = new AzureScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(
            new OrphanScope.MarkerDirectory(AzureVirtualMachineProvisioner.Id, "/var/lib/servyx/instances"));

        // Declined exactly as the DigitalOcean and Docker adapters decline it: no handles, no provider call -
        // and here, not even a token exchange. Quietly widening a narrow request into "every managed resource in
        // the subscription" would hand a caller more than it asked to sweep, and a sweep's output is a delete
        // list.
        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_declines_another_provisioners_scope_and_makes_no_api_call()
    {
        var scenario = new AzureScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("digitalocean-droplet"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Destroy and compensate
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DestroyAsync_walks_the_host_back_down_in_the_order_arm_will_accept()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Empty(HttpStatusCode.OK);

        (await scenario.Provisioner().DestroyAsync(resource.Handle)).Should().BeTrue();

        var deletes = scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Delete)
            .Select(r => r.Uri.AbsolutePath)
            .ToList();

        // ARM refuses to delete a NIC while a VM references it, and a public address while a NIC references it.
        // A destroy is therefore a sequence, driven from names recorded on the handle at create time.
        deletes.Should().Equal(
            AzureScenario.VmId,
            AzureScenario.NicId,
            AzureScenario.PublicIpId,
            AzureScenario.VirtualNetworkId);
    }

    [Fact]
    public async Task DestroyAsync_never_deletes_the_resource_group_even_though_servyx_created_it()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Empty(HttpStatusCode.OK);

        await scenario.Provisioner().DestroyAsync(resource.Handle);

        // The named, deliberate gap. Deleting a resource group is recursive, so a group that was pre-existing or
        // is shared would take resources Servyx never created with it. An empty group left behind is free; a
        // wrongly deleted one is not recoverable. Nothing sweeps it either - ARM's /resources endpoint does not
        // list resource groups at all.
        scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Delete)
            .Should().NotContain(r => r.Uri.AbsolutePath == AzureScenario.ResourceGroupId);
    }

    [Fact]
    public async Task DestroyAsync_with_a_handle_that_lost_its_bookkeeping_tags_destroys_only_the_vm()
    {
        var scenario = new AzureScenario();
        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Empty(HttpStatusCode.OK);

        var handle = new ResourceHandle(
            AzureVirtualMachineProvisioner.Id,
            AzureScenario.VmId,
            AzureScenario.Region,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["servyx.managed"] = "true",
                ["servyx.instance-id"] = AzureScenario.InstanceId,
                ["servyx.job-id"] = AzureScenario.JobId,
                ["servyx.connector-id"] = AzureScenario.ConnectorId,
            });

        (await scenario.Provisioner().DestroyAsync(handle)).Should().BeTrue();

        // A missing tag means "this adapter does not know of such a resource", never "delete something with a
        // guessed name". The siblings are then left for ReconcileAsync to find by tag - which it can, because
        // each of them carries the canonical identity in its own right.
        scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Delete)
            .Select(r => r.Uri.AbsolutePath)
            .Should().Equal(AzureScenario.VmId);
    }

    [Fact]
    public async Task DestroyAsync_reports_false_when_the_vm_was_already_gone()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? AzureArmApiDouble.Json(
                HttpStatusCode.NotFound,
                """{"error":{"code":"ResourceNotFound","message":"The Resource was not found."}}""");

        (await scenario.Provisioner().DestroyAsync(resource.Handle)).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyAsync_waits_for_an_accepted_delete_to_actually_finish()
    {
        var scenario = new AzureScenario();
        var resource = await scenario.CreateAsync();
        var vmDeleteIssued = false;

        scenario.Api.Responder = request =>
        {
            if (AzureScenario.RouteTokenExchange(request) is { } token)
            {
                return token;
            }

            if (request.Method == HttpMethod.Delete)
            {
                vmDeleteIssued |= request.Uri.AbsolutePath == AzureScenario.VmId;
                return AzureArmApiDouble.Empty(HttpStatusCode.Accepted);
            }

            return AzureArmApiDouble.Json(
                HttpStatusCode.NotFound,
                """{"error":{"code":"ResourceNotFound","message":"Gone."}}""");
        };

        await scenario.Provisioner().DestroyAsync(resource.Handle);

        // Returning on the 202 would break teardown outright: ARM would refuse the NIC delete because the VM it
        // accepted a delete for still exists. So an accepted delete is polled until the resource 404s.
        vmDeleteIssued.Should().BeTrue();
        scenario.Api.ArmRequests.Should().Contain(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath == AzureScenario.VmId);
    }

    [Fact]
    public async Task Compensating_a_failed_create_removes_the_resources_it_did_create_in_reverse_order()
    {
        var scenario = new AzureScenario();

        // The public address is created, then the VM write fails - the single most likely partial failure, and
        // the one that strands a billable resource.
        scenario.Api.Responder = request =>
        {
            if (AzureScenario.RouteTokenExchange(request) is { } token)
            {
                return token;
            }

            return request.Method == HttpMethod.Put
                && request.Uri.AbsolutePath == AzureScenario.VmId
                    ? AzureArmApiDouble.Json(
                        HttpStatusCode.BadRequest,
                        """{"error":{"code":"SkuNotAvailable","message":"The requested size is not available."}}""")
                    : request.Method == HttpMethod.Put
                        ? AzureArmApiDouble.Json(HttpStatusCode.Created, AzureScenario.PayloadFor(request))
                        : AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request));
        };

        var operation = scenario.Provisioner()
            .CreateOperation(AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest()));

        await Assert.ThrowsAsync<AzureApiException>(() => operation.CreateAsync());

        scenario.Api.Requests.Clear();
        scenario.Api.Responder = request =>
        {
            if (AzureScenario.RouteTokenExchange(request) is { } token)
            {
                return token;
            }

            return request.Method == HttpMethod.Delete
                ? AzureArmApiDouble.Empty(HttpStatusCode.OK)
                : request.Uri.AbsolutePath == AzureScenario.VmId
                    ? AzureArmApiDouble.Json(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound"}}""")
                    : AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request));
        };

        await operation.CompensateAsync();

        // The VM never existed, so nothing is deleted for it. The three resources that DO exist are removed in
        // the reverse of the order they were created, which is the order ARM will accept.
        scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Delete)
            .Select(r => r.Uri.AbsolutePath)
            .Should().Equal(AzureScenario.NicId, AzureScenario.PublicIpId, AzureScenario.VirtualNetworkId);
    }

    [Fact]
    public async Task Compensation_asks_the_provider_before_deleting_and_leaves_resources_it_did_not_create()
    {
        var scenario = new AzureScenario();
        scenario.RouteSuccessfulCreate();

        var operation = scenario.Provisioner()
            .CreateOperation(AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest()));

        await operation.CreateAsync();
        scenario.Api.Requests.Clear();

        // The ARM writes above are upserts, so a name collision would have UPDATED somebody else's virtual
        // network rather than creating a new one. Compensation therefore reads each candidate back and deletes
        // it only if it carries this operation's own instance id.
        scenario.Api.Responder = request =>
        {
            if (AzureScenario.RouteTokenExchange(request) is { } token)
            {
                return token;
            }

            if (request.Method == HttpMethod.Delete)
            {
                return AzureArmApiDouble.Empty(HttpStatusCode.OK);
            }

            return request.Uri.AbsolutePath == AzureScenario.VirtualNetworkId
                ? AzureArmApiDouble.Json(
                    HttpStatusCode.OK,
                    AzureScenario.ResourceSummaryJson(
                        AzureScenario.VirtualNetworkId,
                        "Microsoft.Network/virtualNetworks",
                        "palworld-01-vnet",
                        tags: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["servyx.managed"] = "true",
                            ["servyx.instance-id"] = "a-different-instance",
                            ["servyx.job-id"] = AzureScenario.JobId,
                            ["servyx.connector-id"] = AzureScenario.ConnectorId,
                        }))
                : AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request));
        };

        await operation.CompensateAsync();

        var deleted = scenario.Api.ArmRequests
            .Where(r => r.Method == HttpMethod.Delete)
            .Select(r => r.Uri.AbsolutePath)
            .ToList();

        deleted.Should().Equal(AzureScenario.VmId, AzureScenario.NicId, AzureScenario.PublicIpId);
        deleted.Should().NotContain(AzureScenario.VirtualNetworkId);
    }

    [Fact]
    public async Task Compensation_never_deletes_the_resource_group()
    {
        var scenario = new AzureScenario();
        scenario.RouteSuccessfulCreate();

        var operation = scenario.Provisioner()
            .CreateOperation(AzureVirtualMachineProvisioner.BuildSpec(AzureScenario.PalworldVmRequest()));

        await operation.CreateAsync();
        scenario.Api.Requests.Clear();

        scenario.Api.Responder = request => AzureScenario.RouteTokenExchange(request)
            ?? (request.Method == HttpMethod.Delete
                ? AzureArmApiDouble.Empty(HttpStatusCode.OK)
                : AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.PayloadFor(request)));

        await operation.CompensateAsync();

        // Stated as a limitation rather than a feature: a failed create can leave an empty, tagged, free
        // resource group behind, and no sweep will ever report it.
        scenario.Api.ArmRequests.Should().NotContain(
            r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath == AzureScenario.ResourceGroupId);
    }

    // ---------------------------------------------------------------------------------------------------
    // Input validation that exists because a five-write sequence fails expensively
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_malformed_image_reference_is_rejected_before_anything_is_created()
    {
        // A DigitalOcean image is one slug; an Azure image is a four-part URN. If this were left to ARM it would
        // be caught on the LAST write of five, by which point four resources exist and one of them is billing.
        var error = Assert.Throws<ArgumentException>(() => AzureVirtualMachineProvisioner.BuildSpec(
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "ubuntu-24-04-x64",
            })));

        error.Message.Should().Contain("publisher:offer:sku:version");
    }

    [Fact]
    public void An_ssh_public_key_is_required_because_password_login_is_disabled()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = AzureScenario.InstanceId,
            ["jobId"] = AzureScenario.JobId,
            ["connectorId"] = AzureScenario.ConnectorId,
            ["name"] = AzureScenario.VmName,
            ["resourceGroup"] = AzureScenario.ResourceGroup,
            ["image"] = AzureScenario.ImageUrn,
            ["region"] = AzureScenario.Region,
            ["size"] = AzureScenario.VmSize,
        };

        // Optional and unused on the wire for DigitalOcean, which cannot consume raw key material. Mandatory
        // here, because ARM consumes it and this adapter turns password authentication off - a VM created
        // without one would be unreachable.
        var error = Assert.Throws<ArgumentException>(() => AzureVirtualMachineProvisioner.BuildSpec(
            new ProvisioningRequest("palworld", "azure-vm", AzureScenario.ConnectorId, parameters)));

        error.Message.Should().Contain("sshPublicKey");
    }

    [Fact]
    public void A_reserved_admin_username_is_refused_at_construction()
    {
        var scenario = new AzureScenario();

        // 'root' is the DigitalOcean adapter's default and is flatly refused by ARM, so the default does not
        // carry over between the two adapters. Caught here rather than on the fifth write of five.
        var error = Assert.Throws<ArgumentException>(() => scenario.Provisioner(sshUsername: "root"));

        error.Message.Should().Contain("root");
        error.Message.Should().Contain("azureuser");
    }

    // ---------------------------------------------------------------------------------------------------
    // Capabilities: what is claimed, and - just as load-bearing - what is not
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_claims_exactly_the_four_capabilities_it_implements()
    {
        var scenario = new AzureScenario();

        scenario.Provisioner().Capabilities.Should().Be(
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
    public void Every_capability_the_provisioner_does_not_implement_is_absent(ProvisioningCapabilities absent)
    {
        var scenario = new AzureScenario();

        // ARM can do all of these. This adapter calls none of them, and a capability bit is a promise about the
        // adapter, not about the provider. StaticAddress is the subtle one: this adapter does create a
        // Static-allocation public address as part of a new host, but the bit means "can allocate and attach one
        // to an existing resource", which it cannot - so claiming it would be a lie about an operation, not a
        // technicality.
        scenario.Provisioner().Capabilities.Should().NotHaveFlag(absent);
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------

    private static string VmWriteBody(AzureScenario scenario) =>
        scenario.Api.ArmRequests
            .Single(r => r.Method == HttpMethod.Put && r.Uri.AbsolutePath == AzureScenario.VmId).Body!;

    private static Dictionary<string, string> ReadTags(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("tags")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);

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
