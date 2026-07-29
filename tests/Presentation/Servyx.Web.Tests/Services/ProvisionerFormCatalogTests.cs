using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Pins the schemas <c>/deploy</c> renders from, without rendering anything. These are unit tests over a
/// value: a schema is data, so what a provisioner will be sent can be asserted exactly, rather than inferred
/// from the DOM.
/// </summary>
public class ProvisionerFormCatalogTests
{
    private static readonly DeployRequestIdentity Identity = new(
        GameDefinitionId: "palworld",
        InstanceId: "preview-0123456789ab",
        JobId: "job-0123456789ab",
        ConnectorId: "docker-container-local");

    private static ProvisionerFormSchema SchemaFor(string provisionerId) =>
        ProvisionerFormCatalog.CreateDefault().For(provisionerId);

    /// <summary>
    /// <strong>The Docker regression pin.</strong> This dictionary is a transcription of the one
    /// <c>DeployPage.BuildRequest()</c> hardcoded before the form became schema-driven, for that method's own
    /// defaults. If a future edit changes a key, a value, or adds an eighth entry, this fails.
    /// </summary>
    [Fact]
    public void Docker_builds_exactly_the_request_the_page_hardcoded_before()
    {
        var schema = SchemaFor(DockerContainerProvisioner.Id);

        schema.TryBuildRequest(schema.Defaults(), Identity, null, out var request, out var refusal)
            .Should().BeTrue();

        refusal.Should().BeNull();
        request.Should().NotBeNull();

        request!.GameDefinitionId.Should().Be("palworld");
        request.DeploymentProfileId.Should().Be("docker");
        request.ConnectorId.Should().Be("docker-container-local");

        request.Parameters.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image"] = ProvisionerFormCatalog.FallbackContainerImage,
            ["containerName"] = "servyx-preview",
            ["instanceId"] = "preview-0123456789ab",
            ["jobId"] = "job-0123456789ab",
            ["connectorId"] = "docker-container-local",
            ["restartPolicy"] = "unless-stopped",
            ["port:8211/tcp"] = "8211",
        });
    }

    /// <summary>
    /// The one Docker parameter whose <em>key</em> carries the entered value. A plain key-per-field mapping
    /// could not express it, which is why <see cref="ProvisionerFormField.ParameterKey"/> takes a placeholder.
    /// </summary>
    [Fact]
    public void Docker_writes_the_port_into_the_parameter_key_as_well_as_its_value()
    {
        var schema = SchemaFor(DockerContainerProvisioner.Id);
        var values = schema.Defaults();
        values["host-port"] = "27015";

        schema.TryBuildRequest(values, Identity, null, out var request, out _).Should().BeTrue();

        request!.Parameters.Should().ContainKey("port:27015/tcp").WhoseValue.Should().Be("27015");
        request.Parameters.Should().NotContainKey("port:8211/tcp");
    }

    /// <summary>
    /// The bundled definition's image still reaches Docker's Image field, which is what
    /// <c>DeployPage.OnInitializedAsync</c> did directly before the catalog existed.
    /// </summary>
    [Fact]
    public void Docker_takes_its_default_image_from_the_bundled_definition_when_one_is_supplied()
    {
        var schema = ProvisionerFormCatalog.CreateDefault("example.invalid/game:9").For(DockerContainerProvisioner.Id);

        schema.Defaults()["image"].Should().Be("example.invalid/game:9");

        // An absent or blank definition falls back to the literal the page carried.
        ProvisionerFormCatalog.CreateDefault("  ").For(DockerContainerProvisioner.Id)
            .Defaults()["image"].Should().Be(ProvisionerFormCatalog.FallbackContainerImage);
    }

    [Theory]
    [InlineData(DockerContainerProvisioner.Id)]
    [InlineData(SshProcessProvisioner.Id)]
    [InlineData(LocalProcessProvisioner.Id)]
    [InlineData(DigitalOceanDropletProvisioner.Id)]
    [InlineData(AzureVirtualMachineProvisioner.Id)]
    [InlineData(AwsEc2Provisioner.Id)]
    [InlineData(AwsLightsailProvisioner.Id)]
    public void Every_shipped_provisioner_is_described(string provisionerId)
    {
        var schema = SchemaFor(provisionerId);

        schema.ProvisionerId.Should().Be(provisionerId);
        schema.Fields.Should().NotBeEmpty();
        schema.AllowsAdditionalParameters.Should().BeFalse(
            "a described provisioner's schema is its description; it needs no escape hatch beside it");
    }

    /// <summary>
    /// The seven schemas are the seven registered adapters — asserted as a set, so adding an adapter without
    /// a schema is visible here rather than only on the page.
    /// </summary>
    [Fact]
    public void The_catalog_describes_exactly_the_seven_registered_adapters()
    {
        ProvisionerFormCatalog.CreateDefault().DescribedProvisionerIds.Should().BeEquivalentTo(
        [
            DockerContainerProvisioner.Id,
            SshProcessProvisioner.Id,
            LocalProcessProvisioner.Id,
            DigitalOceanDropletProvisioner.Id,
            AzureVirtualMachineProvisioner.Id,
            AwsEc2Provisioner.Id,
            AwsLightsailProvisioner.Id,
        ]);
    }

    /// <summary>
    /// Each cloud schema's required fields are exactly the parameters that adapter's <c>BuildSpec</c> calls
    /// <c>Required</c> for — no fewer, so the request is accepted, and no more, so the form does not demand
    /// something the adapter never reads.
    /// </summary>
    [Theory]
    [InlineData(DigitalOceanDropletProvisioner.Id, "name", "size", "region", "image")]
    [InlineData(AzureVirtualMachineProvisioner.Id, "name", "resourceGroup", "size", "region", "image", "sshPublicKey")]
    [InlineData(AwsEc2Provisioner.Id, "name", "size", "image")]
    [InlineData(AwsLightsailProvisioner.Id, "name", "size", "image")]
    [InlineData(SshProcessProvisioner.Id, "dataDir", "executable")]
    [InlineData(LocalProcessProvisioner.Id, "dataDir", "executable")]
    public void A_schemas_required_fields_match_its_adapters_required_parameters(
        string provisionerId,
        params string[] expected)
    {
        SchemaFor(provisionerId).Fields
            .Where(f => f.IsRequired)
            .Select(f => f.ParameterKey)
            .Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// A complete cloud form produces a request with every parameter the adapter needs and nothing from any
    /// other provisioner.
    /// </summary>
    [Fact]
    public void A_complete_cloud_form_builds_a_request_carrying_only_that_providers_parameters()
    {
        var schema = SchemaFor(DigitalOceanDropletProvisioner.Id);
        var identity = Identity with { ConnectorId = "digitalocean-droplet-local" };

        schema.TryBuildRequest(schema.Defaults(), identity, null, out var request, out _).Should().BeTrue();

        request!.DeploymentProfileId.Should().Be("machine");
        request.ConnectorId.Should().Be("digitalocean-droplet-local");
        request.Parameters.Keys.Should().BeEquivalentTo(
            ["name", "size", "region", "image", "instanceId", "jobId", "connectorId"]);

        // Not a Docker parameter in sight, and no empty-string placeholder for the optional key that was
        // left alone.
        request.Parameters.Should().NotContainKey("containerName");
        request.Parameters.Should().NotContainKey("restartPolicy");
        request.Parameters.Should().NotContainKey("sshPublicKey");
    }

    [Fact]
    public void A_missing_required_field_refuses_and_names_it()
    {
        var schema = SchemaFor(DigitalOceanDropletProvisioner.Id);
        var values = schema.Defaults();
        values["name"] = "   ";

        schema.TryBuildRequest(values, Identity, null, out var request, out var refusal).Should().BeFalse();

        request.Should().BeNull("a refusal must not hand back a half-formed request");
        refusal.Should().Contain("Droplet name");
        refusal.Should().Contain(DigitalOceanDropletProvisioner.Id);
    }

    [Fact]
    public void Several_missing_required_fields_are_all_named()
    {
        var schema = SchemaFor(AzureVirtualMachineProvisioner.Id);
        var values = schema.Defaults();
        values["resource-group"] = string.Empty;
        values["ssh-public-key"] = string.Empty;

        schema.TryBuildRequest(values, Identity, null, out _, out var refusal).Should().BeFalse();

        refusal.Should().Contain("Resource group");
        refusal.Should().Contain("SSH public key");
    }

    /// <summary>
    /// An optional field left empty contributes nothing at all — not an empty-string parameter, which
    /// several adapters would read as a deliberate blank rather than as an absence.
    /// </summary>
    [Fact]
    public void An_optional_field_left_empty_emits_no_parameter_and_no_implied_parameter()
    {
        var schema = SchemaFor(SshProcessProvisioner.Id);
        var values = schema.Defaults();
        values["data-dir"] = "/srv/palworld";
        values["executable"] = "./PalServer.sh";

        schema.TryBuildRequest(values, Identity, null, out var request, out _).Should().BeTrue();

        request!.Parameters.Should().NotContainKey("install:0:appId");
        request.Parameters.Should().NotContainKey("install:0:verb",
            "an install entry with no verb is one the provisioner refuses outright");
    }

    /// <summary>
    /// …and filling it in brings its companions with it. An <c>install:0:appId</c> alone is an install entry
    /// the adapter throws on, so the verb travels with the value that implies it.
    /// </summary>
    [Fact]
    public void An_optional_field_that_is_filled_in_brings_its_implied_parameters()
    {
        var schema = SchemaFor(LocalProcessProvisioner.Id);
        var values = schema.Defaults();
        values["data-dir"] = "/srv/palworld";
        values["executable"] = "./PalServer.sh";
        values["steam-app-id"] = "2394010";

        schema.TryBuildRequest(values, Identity, null, out var request, out _).Should().BeTrue();

        request!.Parameters["install:0:appId"].Should().Be("2394010");
        request.Parameters["install:0:verb"].Should().Be("steamcmd");
        request.Parameters["install:0:validate"].Should().Be("true");
    }

    /// <summary>
    /// A provisioner this build ships no schema for is still deployable — the page cannot guess its fields,
    /// which is a different and much smaller statement than "the page cannot drive it".
    /// </summary>
    [Fact]
    public void An_undescribed_provisioner_gets_a_free_form_editor_rather_than_nothing()
    {
        var schema = ProvisionerFormCatalog.CreateDefault().For("hetzner-server");

        schema.ProvisionerId.Should().Be("hetzner-server");
        schema.Fields.Should().BeEmpty();
        schema.AllowsAdditionalParameters.Should().BeTrue();

        schema.TryBuildRequest(
            schema.Defaults(),
            Identity with { ConnectorId = "hetzner-server-local" },
            "serverType = cx41\n# a comment\n\nimage=ubuntu-24.04",
            out var request,
            out var refusal).Should().BeTrue();

        refusal.Should().BeNull();
        request!.Parameters["serverType"].Should().Be("cx41");
        request.Parameters["image"].Should().Be("ubuntu-24.04");
        request.Parameters["connectorId"].Should().Be("hetzner-server-local");
    }

    [Fact]
    public void A_free_form_line_that_is_not_a_pair_is_refused_rather_than_guessed_at()
    {
        var schema = ProvisionerFormCatalog.Undescribed("hetzner-server");

        schema.TryBuildRequest(schema.Defaults(), Identity, "serverType cx41", out var request, out var refusal)
            .Should().BeFalse();

        request.Should().BeNull();
        refusal.Should().Contain("serverType cx41");
    }

    /// <summary>
    /// The three identifiers that tie a created resource back to its ledger row are the page's to write, and
    /// a free-form line may not quietly retarget one.
    /// </summary>
    [Theory]
    [InlineData("instanceId")]
    [InlineData("jobId")]
    [InlineData("connectorId")]
    public void A_free_form_line_may_not_overwrite_an_identifier_servyx_owns(string key)
    {
        var schema = ProvisionerFormCatalog.Undescribed("hetzner-server");

        schema.TryBuildRequest(schema.Defaults(), Identity, $"{key}=someone-elses", out var request, out var refusal)
            .Should().BeFalse();

        request.Should().BeNull();
        refusal.Should().Contain(key);
    }

    /// <summary>
    /// Field ids are per-schema state keys, so a duplicate would make two inputs share one value. Asserted
    /// across every shipped schema rather than trusted.
    /// </summary>
    [Fact]
    public void No_schema_declares_the_same_field_twice()
    {
        var catalog = ProvisionerFormCatalog.CreateDefault();

        foreach (var provisionerId in catalog.DescribedProvisionerIds)
        {
            var fields = catalog.For(provisionerId).Fields;

            fields.Select(f => f.Id).Should().OnlyHaveUniqueItems(provisionerId);
            fields.Select(f => f.ParameterKey).Should().OnlyHaveUniqueItems(provisionerId);
        }
    }

    /// <summary>
    /// No schema asks for a credential. Secrets reach a provisioner by URN from configuration, never from
    /// this form — and an SSH <em>public</em> key is not a secret.
    /// </summary>
    [Fact]
    public void No_schema_asks_for_a_credential()
    {
        var catalog = ProvisionerFormCatalog.CreateDefault();

        var keys = catalog.DescribedProvisionerIds
            .SelectMany(id => catalog.For(id).Fields)
            .Select(f => f.ParameterKey)
            .ToList();

        keys.Should().NotContain(k =>
            k.Contains("password", StringComparison.OrdinalIgnoreCase)
            || k.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || k.Contains("token", StringComparison.OrdinalIgnoreCase)
            || k.Contains("privateKey", StringComparison.OrdinalIgnoreCase)
            || k.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }
}
