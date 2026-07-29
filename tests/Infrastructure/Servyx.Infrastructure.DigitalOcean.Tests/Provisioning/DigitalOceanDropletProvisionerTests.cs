using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The behaviour of the DigitalOcean droplet adapter itself: planning, tagging, sweeping, refreshing,
/// destroying, and the handling of the account token.
/// </summary>
public class DigitalOceanDropletProvisionerTests
{
    [Fact]
    public void The_provisioner_names_itself_stably()
    {
        var scenario = new DigitalOceanScenario();

        scenario.Provisioner().ProvisionerId.Should().Be("digitalocean-droplet");
        DigitalOceanDropletProvisioner.Id.Should().Be("digitalocean-droplet");
    }

    [Fact]
    public async Task PlanAsync_issues_no_http_request_at_all()
    {
        var scenario = new DigitalOceanScenario();

        // Any request would throw, but the assertion below is the real one: not "the call failed" but "no call
        // was made". A plan that cannot reach the provider cannot create a billable resource, and cannot leak
        // the account token, whatever else it gets wrong.
        var plan = await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest());

        plan.Should().NotBeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_does_not_even_resolve_the_api_token()
    {
        var scenario = new DigitalOceanScenario();

        await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest());

        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_describes_creating_a_machine_and_stops_there()
    {
        var scenario = new DigitalOceanScenario();

        var plan = await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest());

        // The shape claim as a list of stage ids: create the machine, wait for its address, hand back an SSH
        // target. There is no install stage, because shape I does not install anything.
        plan.Stages.Select(s => s.StageId).Should().Equal("create-droplet", "await-public-address", "handoff-ssh-target");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == DigitalOceanDropletProvisioner.Id);
    }

    [Fact]
    public async Task No_plan_stage_mentions_any_install_verb()
    {
        var scenario = new DigitalOceanScenario();

        var plan = await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest());
        var text = string.Join("\n", plan.Stages.Select(s => s.Description)).ToLowerInvariant();

        // Deliberately literal. If a future edit teaches this adapter to install something, one of these words
        // will appear in the plan it shows the user before the code does anything, and this test fails first.
        foreach (var verb in new[] { "steamcmd", "apt-get", "apt ", "yum", "dnf", "wget", "curl", "tar ", "unzip", "systemctl", "docker run", "chmod" })
        {
            text.Should().NotContain(verb, $"a shape I adapter installs nothing, so its plan cannot mention '{verb}'");
        }
    }

    [Fact]
    public async Task A_plan_carries_the_list_price_of_the_size_it_names()
    {
        var scenario = new DigitalOceanScenario();

        var plan = await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest());

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.ListPrice);
        plan.EstimatedCost.Monthly.Should().Be(24.00m);
        plan.EstimatedCost.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_plan_for_an_unpriced_size_says_unknown_rather_than_guessing()
    {
        var scenario = new DigitalOceanScenario();

        var plan = await scenario.Provisioner().PlanAsync(
            DigitalOceanScenario.PalworldDropletRequest(size: "g-8vcpu-32gb"));

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
        plan.EstimatedCost.Hourly.Should().BeNull();
        plan.EstimatedCost.Monthly.Should().BeNull();
        plan.EstimatedCost.Source.Should().Contain("g-8vcpu-32gb");
    }

    [Fact]
    public async Task Two_plans_for_the_same_request_hash_identically()
    {
        var scenario = new DigitalOceanScenario();
        var provisioner = scenario.Provisioner();

        var first = await provisioner.PlanAsync(DigitalOceanScenario.PalworldDropletRequest());
        var second = await provisioner.PlanAsync(DigitalOceanScenario.PalworldDropletRequest());

        second.PlanHash.Should().Be(first.PlanHash);
    }

    [Fact]
    public async Task Changing_the_size_changes_the_plan_hash()
    {
        var scenario = new DigitalOceanScenario();
        var provisioner = scenario.Provisioner();

        var first = await provisioner.PlanAsync(DigitalOceanScenario.PalworldDropletRequest());
        var second = await provisioner.PlanAsync(DigitalOceanScenario.PalworldDropletRequest(size: "s-4vcpu-8gb"));

        second.PlanHash.Should().NotBe(first.PlanHash);
    }

    [Fact]
    public async Task Requested_ingress_rules_are_reported_as_not_applied_rather_than_silently_dropped()
    {
        var scenario = new DigitalOceanScenario();

        var plan = await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ingress:8211/udp"] = "0.0.0.0/0" }));

        var stage = plan.Stages.Single(s => s.StageId == "ingress-not-applied");
        stage.Description.Should().StartWith("NOT APPLIED:");
        stage.Description.Should().Contain("udp/8211");

        // And the capability bit agrees with the stage: nothing claims a firewall was configured.
        scenario.Provisioner().Capabilities.Should().NotHaveFlag(ProvisioningCapabilities.FirewallRules);
    }

    [Fact]
    public async Task Create_stamps_the_canonical_servyx_tags_in_the_documented_encoding()
    {
        var scenario = new DigitalOceanScenario();

        await scenario.CreateAsync();

        var create = scenario.Api.Requests.Single(r => r.Method == HttpMethod.Post);
        var tags = JsonDocument.Parse(create.Body!).RootElement.GetProperty("tags")
            .EnumerateArray().Select(t => t.GetString()).ToList();

        // The exact wire strings, spelled out. Orphan-sweep correctness depends on this encoding, so it is
        // pinned as literals rather than recomputed with the same code under test.
        tags.Should().BeEquivalentTo(
        [
            "servyx_managed:true",
            "servyx_instance-id:srv-0001",
            "servyx_job-id:job-42",
            "servyx_connector-id:conn-1",
        ]);
    }

    [Fact]
    public async Task Create_sends_the_droplet_shape_the_machine_spec_describes()
    {
        var scenario = new DigitalOceanScenario();

        await scenario.CreateAsync();

        var body = JsonDocument.Parse(scenario.Api.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;

        body.GetProperty("name").GetString().Should().Be("palworld-01");
        body.GetProperty("region").GetString().Should().Be("nyc3");
        body.GetProperty("size").GetString().Should().Be("s-2vcpu-4gb");
        body.GetProperty("image").GetString().Should().Be("ubuntu-24-04-x64");
        body.GetProperty("ssh_keys").EnumerateArray().Select(k => k.GetString())
            .Should().Equal("3b:16:bf:e4:8b:00:8b:b8:59:8c:a9:d3:f0:19:45:fa");
    }

    [Fact]
    public async Task Create_sends_no_user_data_when_the_caller_supplied_none()
    {
        var scenario = new DigitalOceanScenario();

        await scenario.CreateAsync();

        var body = JsonDocument.Parse(scenario.Api.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;

        // The single most important assertion for the "no install logic" claim. This adapter authors no
        // cloud-init, so a request that asked for none must send none - not a Servyx bootstrap script, not a
        // package list, not a game payload.
        body.TryGetProperty("user_data", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Create_forwards_caller_supplied_user_data_verbatim_without_adding_to_it()
    {
        var scenario = new DigitalOceanScenario();
        const string CloudInit = "#cloud-config\nusers:\n  - name: steam\n";

        await scenario.CreateAsync(DigitalOceanScenario.PalworldDropletRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["cloudInit"] = CloudInit }));

        var body = JsonDocument.Parse(scenario.Api.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;

        body.GetProperty("user_data").GetString().Should().Be(CloudInit);
    }

    [Fact]
    public async Task Create_hands_back_a_handle_that_names_the_droplet_and_its_region()
    {
        var scenario = new DigitalOceanScenario();

        var resource = await scenario.CreateAsync();

        resource.Handle.ProvisionerId.Should().Be("digitalocean-droplet");
        resource.Handle.ProviderResourceId.Should().Be(DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture));
        resource.Handle.Region.Should().Be("nyc3");
        resource.Handle.Tags[ServyxTagKeys.Managed].Should().Be("true");
        resource.Handle.Tags[ServyxTagKeys.InstanceId].Should().Be(DigitalOceanScenario.InstanceId);
        resource.ConnectorId.Should().Be(DigitalOceanScenario.ConnectorId);
    }

    [Fact]
    public async Task Create_reports_the_droplets_addresses_and_its_list_price_as_facts()
    {
        var scenario = new DigitalOceanScenario();

        var resource = await scenario.CreateAsync();

        resource.Facts.PublicAddress.Should().Be(DigitalOceanScenario.PublicIp);
        resource.Facts.PrivateAddress.Should().Be(DigitalOceanScenario.PrivateIp);
        resource.Facts.Cost.Confidence.Should().Be(CostConfidence.ListPrice);
        resource.Facts.Cost.Hourly.Should().Be(0.03571m);
        resource.Facts.CreatedAt.Should().Be(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Create_waits_for_an_address_before_describing_a_target()
    {
        var scenario = new DigitalOceanScenario();
        var gets = 0;

        scenario.Api.Responder = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                // DigitalOcean answers a create with status "new" and an empty networks array.
                return DigitalOceanApiDouble.Json(
                    HttpStatusCode.Accepted,
                    DigitalOceanScenario.DropletEnvelopeJson(status: "new", withNetworks: false));
            }

            gets++;
            return DigitalOceanApiDouble.Json(
                HttpStatusCode.OK,
                DigitalOceanScenario.DropletEnvelopeJson(withNetworks: gets > 1));
        };

        var provisioner = scenario.Provisioner();
        var spec = DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest());

        var resource = await provisioner.CreateOperation(spec).CreateAsync();

        gets.Should().Be(2);
        resource.Target.Endpoint.Should().Be($"ssh://root@{DigitalOceanScenario.PublicIp}:22");
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_deleted_droplet()
    {
        var scenario = new DigitalOceanScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.NotFound,
            """{"id":"not_found","message":"The resource you were accessing could not be found."}""");

        var refreshed = await scenario.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_droplet_that_is_not_servyx_managed()
    {
        var scenario = new DigitalOceanScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.OK,
            DigitalOceanScenario.DropletEnvelopeJson(tags: ["production", "team:ops"]));

        var refreshed = await scenario.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_handle_that_does_not_name_a_droplet_id()
    {
        var scenario = new DigitalOceanScenario();
        var handle = new ResourceHandle(
            DigitalOceanDropletProvisioner.Id,
            "/var/lib/servyx/instances/srv-0001.servyx.json",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal));

        var refreshed = await scenario.Provisioner().RefreshAsync(handle);

        refreshed.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_rebuilds_the_descriptor_handed_over_at_creation()
    {
        var scenario = new DigitalOceanScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(HttpStatusCode.OK, DigitalOceanScenario.DropletEnvelopeJson());

        var refreshed = await scenario.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();

        // Compared field by field rather than with record equality: TargetDescriptor's Options is an
        // IReadOnlyDictionary, which the compiler-generated record Equals compares by reference - the same
        // pre-existing defect the Docker and SSH handoff tests already pin.
        refreshed!.Target.TransportId.Should().Be(resource.Target.TransportId);
        refreshed.Target.Endpoint.Should().Be(resource.Target.Endpoint);
        refreshed.Target.CredentialUrn.Should().Be(resource.Target.CredentialUrn);
        refreshed.Target.DockerContext.Should().Be(resource.Target.DockerContext);
        refreshed.Target.Options.Should().BeEquivalentTo(resource.Target.Options);
    }

    [Fact]
    public async Task ReconcileAsync_asks_the_provider_for_droplets_carrying_the_managed_tag()
    {
        var scenario = new DigitalOceanScenario();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.OK,
            DigitalOceanScenario.DropletListJson(null, DigitalOceanScenario.DropletJson()));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(DigitalOceanDropletProvisioner.Id));

        scenario.Api.Requests.Should().ContainSingle()
            .Which.Uri.Query.Should().Contain("tag_name=servyx_managed%3Atrue");

        handles.Should().ContainSingle();
        handles[0].ProvisionerId.Should().Be("digitalocean-droplet");
        handles[0].ProviderResourceId.Should().Be(DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture));
        handles[0].Tags[ServyxTagKeys.InstanceId].Should().Be(DigitalOceanScenario.InstanceId);
    }

    [Fact]
    public async Task ReconcileAsync_re_checks_the_tag_on_every_droplet_the_provider_returned()
    {
        var scenario = new DigitalOceanScenario();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.OK,
            DigitalOceanScenario.DropletListJson(
                null,
                DigitalOceanScenario.DropletJson(),
                DigitalOceanScenario.DropletJson(id: 999, tags: ["production"])));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(DigitalOceanDropletProvisioner.Id));

        // The provider's filter is its promise; this second check is Servyx's own guarantee. A sweep's output
        // is a delete list, so a droplet Servyx did not tag must never appear in it even if the API says it did.
        handles.Select(h => h.ProviderResourceId).Should().Equal(DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ReconcileAsync_follows_pagination_rather_than_stopping_at_the_first_page()
    {
        var scenario = new DigitalOceanScenario();
        var page = 0;

        scenario.Api.Responder = _ =>
        {
            page++;
            return DigitalOceanApiDouble.Json(
                HttpStatusCode.OK,
                page == 1
                    ? DigitalOceanScenario.DropletListJson(
                        "https://api.digitalocean.com/v2/droplets?page=2&per_page=200&tag_name=servyx_managed%3Atrue",
                        DigitalOceanScenario.DropletJson(id: 1))
                    : DigitalOceanScenario.DropletListJson(null, DigitalOceanScenario.DropletJson(id: 2)));
        };

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(DigitalOceanDropletProvisioner.Id));

        // A sweep that stopped at page one would report "no orphans beyond page one" as "no orphans" - which is
        // the exact failure TagQuery exists to prevent, for resources that bill by the hour.
        scenario.Api.Requests.Should().HaveCount(2);
        handles.Select(h => h.ProviderResourceId).Should().Equal("1", "2");
    }

    [Fact]
    public async Task ReconcileAsync_narrows_to_a_region_when_the_scope_names_one()
    {
        var scenario = new DigitalOceanScenario();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.OK,
            DigitalOceanScenario.DropletListJson(
                null,
                DigitalOceanScenario.DropletJson(id: 1, region: "nyc3"),
                DigitalOceanScenario.DropletJson(id: 2, region: "fra1")));

        var handles = await scenario.Provisioner()
            .ReconcileAsync(new OrphanScope.ProviderWide(DigitalOceanDropletProvisioner.Id, "fra1"));

        handles.Select(h => h.ProviderResourceId).Should().Equal("2");
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_marker_directory_scope_and_makes_no_api_call()
    {
        var scenario = new DigitalOceanScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(
            new OrphanScope.MarkerDirectory(DigitalOceanDropletProvisioner.Id, "/var/lib/servyx/instances"));

        // Declined the same way the Docker adapter declines it: no handles, no provider call. Quietly widening
        // a narrow request into "every managed droplet in the account" would hand a caller more droplets than
        // it asked to sweep, and a sweep's output is a delete list.
        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_declines_another_provisioners_scope_and_makes_no_api_call()
    {
        var scenario = new DigitalOceanScenario();

        var handles = await scenario.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("ssh-process"));

        handles.Should().BeEmpty();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DestroyAsync_reports_true_when_the_droplet_was_destroyed()
    {
        var scenario = new DigitalOceanScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = _ => DigitalOceanApiDouble.Empty(HttpStatusCode.NoContent);

        (await scenario.Provisioner().DestroyAsync(resource.Handle)).Should().BeTrue();
        scenario.Api.Requests.Last().Method.Should().Be(HttpMethod.Delete);
        scenario.Api.Requests.Last().Uri.AbsolutePath.Should().Be($"/v2/droplets/{DigitalOceanScenario.DropletId}");
    }

    [Fact]
    public async Task DestroyAsync_reports_false_when_the_droplet_was_already_gone()
    {
        var scenario = new DigitalOceanScenario();
        var resource = await scenario.CreateAsync();

        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.NotFound,
            """{"id":"not_found","message":"The resource you were accessing could not be found."}""");

        (await scenario.Provisioner().DestroyAsync(resource.Handle)).Should().BeFalse();
    }

    [Fact]
    public async Task Compensating_a_completed_create_destroys_the_droplet_it_created()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteSuccessfulCreate();

        var operation = scenario.Provisioner()
            .CreateOperation(DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest()));

        await operation.CreateAsync();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Empty(HttpStatusCode.NoContent);

        await operation.CompensateAsync();

        scenario.Api.Requests.Last().Method.Should().Be(HttpMethod.Delete);
        scenario.Api.Requests.Last().Uri.AbsolutePath.Should().Be($"/v2/droplets/{DigitalOceanScenario.DropletId}");
    }

    [Fact]
    public async Task Compensating_a_create_that_returned_no_id_sweeps_by_tag_rather_than_assuming_nothing_exists()
    {
        var scenario = new DigitalOceanScenario();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(HttpStatusCode.Accepted, """{"links":{}}""");

        var operation = scenario.Provisioner()
            .CreateOperation(DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest()));

        await Assert.ThrowsAsync<DigitalOceanApiException>(() => operation.CreateAsync());

        scenario.Api.Responder = request => request.Method == HttpMethod.Delete
            ? DigitalOceanApiDouble.Empty(HttpStatusCode.NoContent)
            : DigitalOceanApiDouble.Json(
                HttpStatusCode.OK,
                DigitalOceanScenario.DropletListJson(null, DigitalOceanScenario.DropletJson()));

        await operation.CompensateAsync();

        // For a per-hour billed machine the difference between assuming and asking is a machine that bills
        // forever versus one that does not.
        scenario.Api.Requests.Last().Method.Should().Be(HttpMethod.Delete);
        scenario.Api.Requests.Last().Uri.AbsolutePath.Should().Be($"/v2/droplets/{DigitalOceanScenario.DropletId}");
    }

    [Fact]
    public async Task The_operation_publishes_its_tags_before_it_creates_anything()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteSuccessfulCreate();

        var operation = scenario.Provisioner()
            .CreateOperation(DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest()));

        // Read exactly as the executor reads them: before CreateAsync, so they can go into the write-ahead
        // ledger and a droplet created but never acknowledged is still findable by tag.
        var tagsBefore = operation.Tags;
        scenario.Api.Requests.Should().BeEmpty();

        var resource = await operation.CreateAsync();

        tagsBefore.Should().BeEquivalentTo(resource.Handle.Tags);
        operation.Region.Should().Be("nyc3");
        operation.ProvisionerId.Should().Be("digitalocean-droplet");
    }

    // ---------------------------------------------------------------------------------------------------
    // The account token
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_token_is_sent_as_a_bearer_header_and_resolved_freshly_for_every_request()
    {
        var scenario = new DigitalOceanScenario();

        await scenario.CreateAsync();

        scenario.Api.Requests.Should().OnlyContain(r => r.Authorization == "Bearer " + DigitalOceanScenario.ApiToken);

        // One resolution per request: nothing is cached between calls, so revoking the stored token takes
        // effect on the very next call rather than whenever a process happens to restart.
        scenario.Secrets.Resolved.Should().HaveCount(scenario.Api.Requests.Count);
        scenario.Secrets.Resolved.Should().OnlyContain(u => u == DigitalOceanScenario.TokenUrn.Value);
    }

    [Fact]
    public async Task The_token_never_appears_in_anything_the_provisioner_hands_back()
    {
        var scenario = new DigitalOceanScenario();

        var resource = await scenario.CreateAsync();
        var plan = await scenario.Provisioner().PlanAsync(DigitalOceanScenario.PalworldDropletRequest());

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

        rendered.Should().NotContain(DigitalOceanScenario.ApiToken);
        rendered.Should().NotContain("dop_v1");

        // The credential URN on the descriptor is the SSH key's URN, never the DigitalOcean token's.
        resource.Target.CredentialUrn.Should().Be(DigitalOceanScenario.SshCredentialUrn);
        resource.Target.CredentialUrn.Should().NotBe(DigitalOceanScenario.TokenUrn.Value);
    }

    [Fact]
    public async Task The_token_is_never_held_in_a_field_anywhere_in_the_provisioners_object_graph()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteSuccessfulCreate();

        // The same instance that just authenticated several requests - the walk has to happen after the token
        // has actually been used, or it proves only that a freshly-built object is clean.
        var provisioner = scenario.Provisioner();
        await provisioner
            .CreateOperation(DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest()))
            .CreateAsync();

        scenario.Api.Requests.Should().NotBeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.Authorization != null);

        // Walks every reachable field, not just the provisioner's own: the point is that no layer beneath it
        // (the API client, the HttpClient's default headers) parked the token either.
        var reachable = FindStrings(provisioner, [], 0);

        reachable.Should().NotBeEmpty("the walk must actually be reaching state, or it proves nothing");
        reachable.Should().Contain(DigitalOceanScenario.TokenUrn.Value, "the URN is held, which is exactly the point - the URN, not the token");
        reachable.Should().NotContain(s => s.Contains("dop_v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_error_is_reported_without_echoing_the_request_that_carried_the_token()
    {
        var scenario = new DigitalOceanScenario();
        scenario.Api.Responder = _ => DigitalOceanApiDouble.Json(
            HttpStatusCode.Unauthorized,
            """{"id":"unauthorized","message":"Unable to authenticate you."}""");

        var provisioner = scenario.Provisioner();
        var spec = DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest());

        var error = await Assert.ThrowsAsync<DigitalOceanApiException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        error.ToString().Should().NotContain(DigitalOceanScenario.ApiToken);
        error.ToString().Should().NotContain("dop_v1");
    }

    [Fact]
    public async Task A_missing_token_is_reported_as_a_missing_secret_rather_than_as_an_http_failure()
    {
        var scenario = new DigitalOceanScenario();
        var provisioner = scenario.Provisioner(withToken: false);
        var spec = DigitalOceanDropletProvisioner.BuildSpec(DigitalOceanScenario.PalworldDropletRequest());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.CreateOperation(spec).CreateAsync());

        error.Message.Should().Contain(DigitalOceanScenario.TokenUrn.Value);
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void This_assembly_references_no_logging_package_so_no_code_path_can_log_the_token()
    {
        var referenced = typeof(DigitalOceanDropletProvisioner).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        // "The token is never logged" is usually a review promise. Here it is a fact about the build: there is
        // no logging abstraction in scope for this assembly, so there is no reachable API that could write it.
        referenced.Should().NotContain(n => n.Contains("Logging", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain(n => n.Contains("Diagnostics.Tracing", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------------
    // Capabilities: what is claimed, and - just as load-bearing - what is not
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_claims_exactly_the_four_capabilities_it_implements()
    {
        var scenario = new DigitalOceanScenario();

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
    public void Every_capability_the_provisioner_does_not_implement_is_absent(ProvisioningCapabilities absent)
    {
        var scenario = new DigitalOceanScenario();

        // DigitalOcean's API can do all four. This adapter calls none of them, and a capability bit is a promise
        // about the adapter, not about the provider - a caller that believed a port had been opened, or a
        // snapshot taken, when nothing had is worse off than one told plainly that it cannot be done here.
        scenario.Provisioner().Capabilities.Should().NotHaveFlag(absent);
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
