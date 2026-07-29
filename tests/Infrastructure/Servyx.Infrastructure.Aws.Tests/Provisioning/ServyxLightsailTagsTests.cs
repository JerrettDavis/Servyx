using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The tag vocabulary, and the charset comparison the report was asked to make: is Lightsail's tag charset
/// different from EC2's?
/// </summary>
/// <remarks>
/// The answer, pinned below: no. AWS's own Lightsail tagging documentation states the same allowed charset, the
/// same 128/256 length limits and the same reserved <c>aws:</c> prefix as EC2's. What differs is not the
/// charset but the shape of the type: this one has no <c>RoleTag</c> and no <c>NameTag</c>, because Lightsail
/// has only one taggable object per launch and its instance name is already the display name.
/// </remarks>
public class ServyxLightsailTagsTests
{
    [Fact]
    public void The_servyx_keys_are_stored_literally_with_no_encoding_step_anywhere()
    {
        var tags = ServyxLightsailTags.For("srv-1", "job-1", "conn-1").ToTags();

        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags.Keys.Should().Contain("servyx.managed");
        tags.Keys.Should().NotContain(k => k.Contains('_', StringComparison.Ordinal));
    }

    [Fact]
    public void An_instance_id_containing_a_dot_is_accepted_here_exactly_as_it_is_for_ec2()
    {
        var tags = ServyxLightsailTags.For("srv.01", "job.42", "conn.1").ToTags();

        tags[ServyxTagKeys.InstanceId].Should().Be("srv.01");
    }

    [Theory]
    [InlineData("srv-01")]
    [InlineData("srv.01")]
    [InlineData("srv_01")]
    [InlineData("srv:01")]
    [InlineData("srv/01")]
    [InlineData("srv+01")]
    [InlineData("srv=01")]
    [InlineData("srv@01")]
    [InlineData("srv 01")]
    public void Every_character_ec2_allows_in_a_tag_value_is_also_accepted_here_because_the_charset_is_identical(string instanceId) =>
        ServyxLightsailTags.For(instanceId, "job-1", "conn-1").InstanceId.Should().Be(instanceId);

    [Theory]
    [InlineData("srv#01")]
    [InlineData("srv%01")]
    [InlineData("srv*01")]
    [InlineData("srv?01")]
    public void A_character_ec2_forbids_is_also_refused_here_because_the_charset_is_identical(string instanceId)
    {
        var error = Assert.Throws<ArgumentException>(() => ServyxLightsailTags.For(instanceId, "job-1", "conn-1"));

        error.Message.Should().Contain("orphan sweep");
    }

    [Fact]
    public void The_length_limits_match_ec2s_exactly()
    {
        ServyxLightsailTags.MaxTagKeyLength.Should().Be(ServyxEc2Tags.MaxTagKeyLength);
        ServyxLightsailTags.MaxTagValueLength.Should().Be(ServyxEc2Tags.MaxTagValueLength);
        ServyxLightsailTags.AdditionalAllowedCharacters.Should().Be(ServyxEc2Tags.AdditionalAllowedCharacters);
    }

    [Fact]
    public void A_tag_value_longer_than_the_shared_limit_is_refused()
    {
        var tooLong = new string('a', ServyxLightsailTags.MaxTagValueLength + 1);

        Assert.Throws<ArgumentException>(() => ServyxLightsailTags.For(tooLong, "job-1", "conn-1"));
    }

    [Fact]
    public void A_tag_key_using_the_reserved_aws_prefix_is_refused_because_lightsail_would_refuse_the_whole_write()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["aws:cloudformation:stack"] = "x" };

        var error = Assert.Throws<ArgumentException>(() => ServyxLightsailTags.Validate(tags));

        error.Message.Should().Contain("reserves");
    }

    [Fact]
    public void An_extra_tag_can_never_shadow_a_canonical_one()
    {
        var tags = ServyxLightsailTags.For("srv-1", "job-1", "conn-1").ToTags(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServyxTagKeys.Managed] = "false",
                [ServyxTagKeys.InstanceId] = "somebody-elses",
            });

        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags[ServyxTagKeys.InstanceId].Should().Be("srv-1");
    }

    [Fact]
    public void Ownership_is_an_exact_match_and_never_a_truthiness_test()
    {
        ServyxLightsailTags.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "TRUE",
        }).Should().BeFalse();

        ServyxLightsailTags.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "true",
        }).Should().BeTrue();
    }

    [Fact]
    public void A_partially_tagged_resource_is_reported_as_unidentifiable_rather_than_defaulted()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "true",
            [ServyxTagKeys.InstanceId] = "srv-1",
        };

        ServyxLightsailTags.FromTags(tags).Should().BeNull();
    }

    [Fact]
    public void There_is_no_role_tag_because_lightsail_has_only_one_taggable_object_per_launch()
    {
        // Unlike ServyxEc2Tags.RoleTag, which exists because RunInstances tags two kinds of object (the
        // instance and its EBS volumes), a Lightsail bundle bakes the boot disk into the instance - there is
        // nothing a role key would ever need to disambiguate.
        typeof(ServyxLightsailTags).GetField("RoleTag").Should().BeNull();
        typeof(ServyxLightsailTags).GetField("NameTag").Should().BeNull();
    }
}
