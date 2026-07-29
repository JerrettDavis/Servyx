using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The tagging comparison, asserted rather than asserted-in-prose: Azure needs no encoding where DigitalOcean
/// needs a documented, provably-reversible one.
/// </summary>
public class ServyxAzureTagsTests
{
    [Fact]
    public void The_managed_filter_names_the_key_and_value_servyx_actually_defines()
    {
        // The single most load-bearing string in this assembly, and the direct counterpart of the DigitalOcean
        // suite's pinned literal "servyx_managed:true". There, the sweep filter is an ENCODED string that a
        // human auditing the account has to know to type instead of the real key. Here what the code sends and
        // what a human types into the portal are the same two strings.
        ServyxAzureTags.ManagedFilter.Should().Be("tagName eq 'servyx.managed' and tagValue eq 'true'");
        ServyxAzureTags.ManagedTag.Should().Be("servyx.managed");
        ServyxAzureTags.ManagedTag.Should().Be(ServyxTagKeys.Managed);
        ServyxAzureTags.ManagedTagValue.Should().Be("true");
    }

    [Fact]
    public void Every_canonical_key_reaches_arm_exactly_as_the_domain_spells_it()
    {
        var tags = ServyxAzureTags.For("srv-0001", "job-42", "conn-1").ToTags();

        foreach (var key in ServyxTagKeys.Canonical)
        {
            tags.Should().ContainKey(key, "no encoding step exists, so the domain key IS the wire key");
        }

        tags.Keys.Should().OnlyContain(k => k.StartsWith("servyx.", StringComparison.Ordinal));
        tags.Keys.Should().NotContain(k => k.Contains('_', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("srv-0001")]
    [InlineData("srv.0001.eu")]
    [InlineData("srv_0001")]
    [InlineData("srv/0001")]
    [InlineData("srv 0001")]
    [InlineData("srv+0001=1")]
    [InlineData("2026-07-27T10:00:00Z")]
    [InlineData("/var/lib/servyx/instances")]
    public void An_identifier_azure_accepts_as_a_tag_value_is_carried_unchanged(string instanceId)
    {
        // The comparison that matters. ServyxDropletTags.For refuses every one of these except the first,
        // because a DigitalOcean tag may contain only letters, digits, ':', '-' and '_' - so a '.', a '/', a
        // space, a '+' or a '=' makes the id unprovisionable there, and the adapter documents that as a real
        // asymmetry. ARM places NO charset restriction on a tag value at all, so all of them survive verbatim.
        var tags = ServyxAzureTags.For(instanceId, "job-42", "conn-1").ToTags();

        tags[ServyxTagKeys.InstanceId].Should().Be(instanceId);
    }

    [Fact]
    public void A_value_beyond_azures_length_ceiling_is_refused_rather_than_truncated()
    {
        var tooLong = new string('a', ServyxAzureTags.MaxTagValueLength + 1);

        // The one rule ARM does impose on values, and the only thing this type can reject. A truncated value
        // would make the resource unattributable by a later sweep, which is the failure the whole tagging
        // discipline exists to prevent.
        var error = Assert.Throws<ArgumentException>(() => ServyxAzureTags.For(tooLong, "job-42", "conn-1"));

        error.Message.Should().Contain("256");
    }

    [Theory]
    [InlineData("servyx.managed")]
    [InlineData("servyx.instance-id")]
    [InlineData("owner")]
    [InlineData("cost.centre")]
    [InlineData("a b c")]
    public void A_legal_tag_name_passes_validation(string name)
    {
        ServyxAzureTags.IsTaggableName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("bad<name")]
    [InlineData("bad>name")]
    [InlineData("bad%name")]
    [InlineData("bad&name")]
    [InlineData("bad\\name")]
    [InlineData("bad?name")]
    [InlineData("bad/name")]
    [InlineData("")]
    public void A_tag_name_arm_would_reject_is_refused_before_anything_is_created(string name)
    {
        ServyxAzureTags.IsTaggableName(name).Should().BeFalse();

        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "value" };

        // Checked before the create sequence starts rather than discovered halfway through it. ARM rejects an
        // illegal tag on the write that would have created the resource - harmless on the first write, four
        // billing resources deep on the last.
        Assert.Throws<ArgumentException>(() => ServyxAzureTags.Validate(tags));
    }

    [Fact]
    public void Extra_tags_can_never_shadow_a_canonical_one()
    {
        var tags = ServyxAzureTags.For("srv-0001", "job-42", "conn-1").ToTags(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServyxTagKeys.Managed] = "false",
                [ServyxTagKeys.InstanceId] = "somebody-else",
                ["owner"] = "ops",
            });

        // The ordering rule is ServyxTagKeys.Build's, applied by calling it - the same single implementation
        // every adapter calls. A caller who could set servyx.managed=false could hide a billing VM from every
        // sweep.
        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags[ServyxTagKeys.InstanceId].Should().Be("srv-0001");
        tags["owner"].Should().Be("ops");
    }

    [Fact]
    public void An_identity_can_be_read_back_out_of_a_live_resources_tags_with_no_decoding_step()
    {
        var armTags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = "srv-0001",
            ["servyx.job-id"] = "job-42",
            ["servyx.connector-id"] = "conn-1",
            ["environment"] = "production",
        };

        var identity = ServyxAzureTags.FromTags(ServyxAzureTags.FromArmTags(armTags));

        // The whole of ServyxDropletTags' TryDecode/FromDropletTagsToDictionary machinery reduces to a copy,
        // because ARM already stores a dictionary. A human-applied tag alongside Servyx's comes back as an
        // ordinary entry and is simply not a Servyx key.
        identity.Should().NotBeNull();
        identity!.InstanceId.Should().Be("srv-0001");
        identity.JobId.Should().Be("job-42");
        identity.ConnectorId.Should().Be("conn-1");
    }

    [Fact]
    public void A_partially_tagged_resource_is_reported_as_unidentifiable_rather_than_defaulted()
    {
        var armTags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = "srv-0001",
        };

        ServyxAzureTags.FromTags(ServyxAzureTags.FromArmTags(armTags)).Should().BeNull();
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    public void Ownership_is_an_exact_match_and_never_a_truthiness_test(string managedValue)
    {
        var armTags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = managedValue,
            ["servyx.instance-id"] = "srv-0001",
            ["servyx.job-id"] = "job-42",
            ["servyx.connector-id"] = "conn-1",
        };

        // A sweep's output is a delete list, so a sweep that guesses wrong here destroys someone else's virtual
        // machine.
        ServyxAzureTags.IsManaged(armTags).Should().BeFalse();
    }

    [Fact]
    public void The_bookkeeping_keys_live_in_the_servyx_namespace_like_every_other_adapters_extras()
    {
        foreach (var key in new[]
        {
            ServyxAzureTags.RoleTag,
            ServyxAzureTags.ResourceGroupTag,
            ServyxAzureTags.VirtualNetworkTag,
            ServyxAzureTags.SubnetTag,
            ServyxAzureTags.PublicIpTag,
            ServyxAzureTags.NetworkInterfaceTag,
        })
        {
            key.Should().StartWith(ServyxTagKeys.Prefix);
            ServyxAzureTags.IsTaggableName(key).Should().BeTrue();

            // Descriptive rather than identifying, exactly like ServyxTagKeys.RootPath and .Image: a resource
            // missing one is still unambiguously Servyx-owned and still fully sweepable.
            ServyxTagKeys.Canonical.Should().NotContain(key);
        }
    }
}
