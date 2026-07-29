using Microsoft.Extensions.Configuration;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Reads <c>Servyx:Provisioners:*</c> the way <c>Program.cs</c>'s gated block does.
/// </summary>
/// <remarks>
/// Two properties are pinned here and neither is cosmetic. The first is that every provisioner is
/// individually opt-in on top of <c>Servyx:Provisioning:Enabled</c>, so an operator who turns the gate on
/// still gets exactly the Docker-only composition they had before. The second is that a provisioner enabled
/// without a value it cannot be constructed without stops the process by name, rather than becoming a target
/// on /deploy whose first click is guaranteed to fail.
/// </remarks>
public class ProvisionerWiringTests
{
    private const string GateKey = "Servyx:Provisioning:Enabled";

    /// <summary>Every provisioner's section key, and the id it contributes when enabled.</summary>
    public static TheoryData<string, string> EveryProvisioner() => new()
    {
        { ProvisionerWiringOptions.SshKey, SshProcessProvisioner.Id },
        { ProvisionerWiringOptions.ProcessKey, LocalProcessProvisioner.Id },
        { ProvisionerWiringOptions.DigitalOceanKey, DigitalOceanDropletProvisioner.Id },
        { ProvisionerWiringOptions.AzureKey, AzureVirtualMachineProvisioner.Id },
        { ProvisionerWiringOptions.AwsEc2Key, AwsEc2Provisioner.Id },
        { ProvisionerWiringOptions.AwsLightsailKey, AwsLightsailProvisioner.Id },
    };

    /// <summary>
    /// The minimum an operator must write for one provisioner, with the gate assumed open. Every credential
    /// is a locator; no entry anywhere in this file is a literal secret, because no key accepts one.
    /// </summary>
    internal static Dictionary<string, string?> MinimalSettings(string provisionerKey)
    {
        var prefix = $"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}";
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{prefix}:{ProvisionerWiringOptions.EnabledKey}"] = "true",
        };

        foreach (var (key, value) in RequiredSettings(provisionerKey))
        {
            settings[$"{prefix}:{key}"] = value;
        }

        return settings;
    }

    /// <summary>The keys that must be present, and a well-formed value for each.</summary>
    internal static IReadOnlyDictionary<string, string> RequiredSettings(string provisionerKey) =>
        provisionerKey switch
        {
            ProvisionerWiringOptions.SshKey => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Endpoint"] = "servyx@host.example:22",
                ["CredentialUrn"] = "secret://connector/ssh-provisioning/ssh/private-key",
            },
            ProvisionerWiringOptions.ProcessKey => new Dictionary<string, string>(StringComparer.Ordinal),
            ProvisionerWiringOptions.DigitalOceanKey => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ApiTokenUrn"] = "secret://global/digitalocean/api/token",
            },
            ProvisionerWiringOptions.AzureKey => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TenantId"] = "00000000-0000-0000-0000-000000000001",
                ["ClientId"] = "00000000-0000-0000-0000-000000000002",
                ["ClientSecretUrn"] = "secret://global/azure/api/client-secret",
                ["SubscriptionId"] = "00000000-0000-0000-0000-000000000003",
            },
            ProvisionerWiringOptions.AwsEc2Key or ProvisionerWiringOptions.AwsLightsailKey =>
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Region"] = "us-east-1",
                    ["AccessKeyIdUrn"] = "secret://global/aws/api/access-key-id",
                    ["SecretAccessKeyUrn"] = "secret://global/aws/api/secret-access-key",
                },
            _ => throw new ArgumentOutOfRangeException(nameof(provisionerKey)),
        };

    internal static IConfiguration Config(params IEnumerable<KeyValuePair<string, string?>>[] parts)
    {
        var builder = new ConfigurationBuilder();

        foreach (var part in parts)
        {
            builder.AddInMemoryCollection(part);
        }

        return builder.Build();
    }

    internal static Dictionary<string, string?> GateOpen() =>
        new(StringComparer.Ordinal) { [GateKey] = "true" };

    /// <summary>Every provisioner enabled at once, each with the minimum it needs.</summary>
    internal static Dictionary<string, string?> AllEnabled()
    {
        var settings = GateOpen();

        foreach (var key in new[]
                 {
                     ProvisionerWiringOptions.SshKey,
                     ProvisionerWiringOptions.ProcessKey,
                     ProvisionerWiringOptions.DigitalOceanKey,
                     ProvisionerWiringOptions.AzureKey,
                     ProvisionerWiringOptions.AwsEc2Key,
                     ProvisionerWiringOptions.AwsLightsailKey,
                 })
        {
            foreach (var (k, v) in MinimalSettings(key))
            {
                settings[k] = v;
            }
        }

        return settings;
    }

    [Fact]
    public void A_closed_gate_yields_no_provisioner_however_many_are_switched_on()
    {
        // Every provisioner enabled and fully credentialed — and the gate absent, which is the default.
        var settings = AllEnabled();
        settings.Remove(GateKey);

        var configuration = Config(settings);
        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeFalse();

        var options = ProvisionerWiringOptions.FromConfiguration(configuration, gate);

        options.Should().BeSameAs(ProvisionerWiringOptions.None);
        options.Any.Should().BeFalse();
        options.ProvisionerIds.Should().BeEmpty();
    }

    [Fact]
    public void An_open_gate_with_nothing_else_configured_yields_no_provisioner_either()
    {
        var configuration = Config(GateOpen());
        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeTrue();

        var options = ProvisionerWiringOptions.FromConfiguration(configuration, gate);

        // The Docker provisioner is registered unconditionally inside the gate and has no key here on
        // purpose: adding one would change what every host that already sets the gate composes.
        options.Any.Should().BeFalse();
        options.ProvisionerIds.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryProvisioner))]
    public void Enabling_one_provisioner_enables_exactly_that_one(string provisionerKey, string expectedId)
    {
        var configuration = Config(GateOpen(), MinimalSettings(provisionerKey));
        var gate = ProvisioningGate.FromConfiguration(configuration);

        var options = ProvisionerWiringOptions.FromConfiguration(configuration, gate);

        options.ProvisionerIds.Should().ContainSingle().Which.Should().Be(expectedId);
    }

    [Theory]
    [MemberData(nameof(EveryProvisioner))]
    public void An_absent_enabled_key_leaves_a_fully_configured_provisioner_switched_off(
        string provisionerKey,
        string expectedId)
    {
        // Everything a provisioner needs, present and well-formed — minus its own Enabled key. Nothing but
        // that key may bring a money-spending capability into the container.
        var settings = MinimalSettings(provisionerKey);
        settings.Remove($"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}:{ProvisionerWiringOptions.EnabledKey}");

        var configuration = Config(GateOpen(), settings);
        var options = ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.ProvisionerIds.Should().NotContain(expectedId);
        options.Any.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EveryProvisioner))]
    public void An_unparseable_enabled_key_fails_closed(string provisionerKey, string expectedId)
    {
        var settings = MinimalSettings(provisionerKey);
        settings[$"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}:{ProvisionerWiringOptions.EnabledKey}"] = "yes";

        var configuration = Config(GateOpen(), settings);
        var options = ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.ProvisionerIds.Should().NotContain(expectedId);
    }

    [Fact]
    public void Enabling_every_provisioner_yields_every_id_and_no_duplicates()
    {
        var configuration = Config(AllEnabled());
        var options = ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.ProvisionerIds.Should().BeEquivalentTo(
        [
            SshProcessProvisioner.Id,
            LocalProcessProvisioner.Id,
            DigitalOceanDropletProvisioner.Id,
            AzureVirtualMachineProvisioner.Id,
            AwsEc2Provisioner.Id,
            AwsLightsailProvisioner.Id,
        ]);
        options.ProvisionerIds.Should().OnlyHaveUniqueItems();
    }

    /// <summary>Every (provisioner, required key) pair, so no required value can quietly become optional.</summary>
    public static TheoryData<string, string> EveryRequiredKey()
    {
        var data = new TheoryData<string, string>();

        foreach (var provisionerKey in new[]
                 {
                     ProvisionerWiringOptions.SshKey,
                     ProvisionerWiringOptions.DigitalOceanKey,
                     ProvisionerWiringOptions.AzureKey,
                     ProvisionerWiringOptions.AwsEc2Key,
                     ProvisionerWiringOptions.AwsLightsailKey,
                 })
        {
            foreach (var required in RequiredSettings(provisionerKey).Keys)
            {
                data.Add(provisionerKey, required);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRequiredKey))]
    public void A_provisioner_enabled_without_a_value_it_needs_stops_startup_and_names_the_key(
        string provisionerKey,
        string missingKey)
    {
        var settings = MinimalSettings(provisionerKey);
        var fullKey = $"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}:{missingKey}";
        settings.Remove(fullKey);

        var configuration = Config(GateOpen(), settings);

        var act = () => ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        // Loud, at startup, naming the exact key — not a provisioner registered to fail on the click that
        // follows an approved plan, and not a target silently missing from a list nobody has seen complete.
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{fullKey}*");
    }

    [Theory]
    [MemberData(nameof(EveryRequiredKey))]
    public void A_provisioner_missing_a_required_value_is_still_absent_when_the_gate_is_closed(
        string provisionerKey,
        string missingKey)
    {
        // The refusal above must not become a way to break a read-only host by editing a key it never reads.
        var settings = MinimalSettings(provisionerKey);
        settings.Remove($"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}:{missingKey}");

        var configuration = Config(settings);
        var options = ProvisionerWiringOptions.FromConfiguration(configuration, ProvisioningGate.Closed);

        options.Should().BeSameAs(ProvisionerWiringOptions.None);
    }

    [Theory]
    [InlineData(ProvisionerWiringOptions.SshKey, "CredentialUrn")]
    [InlineData(ProvisionerWiringOptions.DigitalOceanKey, "ApiTokenUrn")]
    [InlineData(ProvisionerWiringOptions.AzureKey, "ClientSecretUrn")]
    [InlineData(ProvisionerWiringOptions.AwsEc2Key, "AccessKeyIdUrn")]
    [InlineData(ProvisionerWiringOptions.AwsEc2Key, "SecretAccessKeyUrn")]
    [InlineData(ProvisionerWiringOptions.AwsLightsailKey, "AccessKeyIdUrn")]
    public void A_credential_written_inline_instead_of_as_a_urn_is_refused(string provisionerKey, string urnKey)
    {
        var settings = MinimalSettings(provisionerKey);
        var fullKey = $"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}:{urnKey}";

        // What an operator reaching for the obvious wrong thing would write: the credential itself.
        settings[fullKey] = "dop_v1_0123456789abcdef";

        var configuration = Config(GateOpen(), settings);

        var act = () => ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{fullKey}*")
            .WithMessage("*secret://*");
    }

    [Theory]
    [InlineData(ProvisionerWiringOptions.DigitalOceanKey)]
    [InlineData(ProvisionerWiringOptions.AzureKey)]
    [InlineData(ProvisionerWiringOptions.AwsEc2Key)]
    [InlineData(ProvisionerWiringOptions.AwsLightsailKey)]
    public void An_optional_credential_locator_may_be_absent_but_never_malformed(string provisionerKey)
    {
        var absent = Config(GateOpen(), MinimalSettings(provisionerKey));
        ProvisionerWiringOptions.FromConfiguration(absent, ProvisioningGate.FromConfiguration(absent))
            .ProvisionerIds.Should().ContainSingle();

        var settings = MinimalSettings(provisionerKey);
        var fullKey = $"{ProvisionerWiringOptions.SectionKey}:{provisionerKey}:SshCredentialUrn";
        settings[fullKey] = "/home/servyx/.ssh/id_ed25519";

        var configuration = Config(GateOpen(), settings);
        var act = () => ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        // Quietly dropping it would stamp a descriptor that authenticates with no key at all.
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{fullKey}*");
    }

    [Fact]
    public void The_local_process_provisioner_needs_no_credential_at_all()
    {
        // Stated as a test because it is the one provisioner for which "no required keys" is a fact about
        // the target rather than a gap: it installs onto the machine Servyx already runs on.
        var configuration = Config(
            GateOpen(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{ProvisionerWiringOptions.SectionKey}:{ProvisionerWiringOptions.ProcessKey}:{ProvisionerWiringOptions.EnabledKey}"] = "true",
            });

        var options = ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.Process.Should().NotBeNull();
        options.Process!.MachineId.Should().BeNull();
        options.Process.MarkerRoot.Should().BeNull();
    }

    [Fact]
    public void Optional_values_are_carried_through_and_defaults_come_from_the_adapters()
    {
        var settings = AllEnabled();
        settings[$"{ProvisionerWiringOptions.SectionKey}:{ProvisionerWiringOptions.DigitalOceanKey}:SshUsername"] = "servyx";

        var configuration = Config(settings);
        var options = ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.DigitalOcean!.SshUsername.Should().Be("servyx");
        options.Azure!.SshUsername.Should().Be(AzureVirtualMachineProvisioner.DefaultSshUsername);
        options.AwsEc2!.SshUsername.Should().Be(AwsEc2Provisioner.DefaultSshUsername);
        options.Ssh!.Endpoint.Should().Be("servyx@host.example:22");
        options.AwsLightsail!.Region.Should().Be("us-east-1");
        options.Azure.SubscriptionId.Should().Be("00000000-0000-0000-0000-000000000003");
    }
}
