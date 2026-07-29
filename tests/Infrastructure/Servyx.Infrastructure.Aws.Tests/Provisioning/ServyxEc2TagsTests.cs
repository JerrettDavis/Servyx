using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The tag vocabulary, and the three-way comparison against the two existing cloud adapters.
/// </summary>
/// <remarks>
/// The question this file answers is the one the task asked directly: did <c>ServyxTagKeys</c> need any
/// encoding for EC2? It did not — but unlike Azure, it did need validation, and the tests below pin both
/// halves of that answer.
/// </remarks>
public class ServyxEc2TagsTests
{
    [Fact]
    public void The_servyx_keys_are_stored_literally_with_no_encoding_step_anywhere()
    {
        var tags = ServyxEc2Tags.For("srv-1", "job-1", "conn-1").ToTags();

        // DigitalOcean would have to write "servyx_managed:true" here. EC2 stores the key as Servyx spells it,
        // so there is no codec in this assembly at all - only the dictionary ServyxTagKeys.Build produced.
        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags.Keys.Should().Contain("servyx.managed");
        tags.Keys.Should().NotContain(k => k.Contains('_', StringComparison.Ordinal));
    }

    [Fact]
    public void The_sweep_filter_names_the_key_a_human_would_type_into_the_console() =>
        ServyxEc2Tags.ManagedFilterName.Should().Be("tag:servyx.managed");

    [Fact]
    public void An_instance_id_containing_a_dot_is_accepted_here_and_refused_by_the_digitalocean_adapter()
    {
        // The concrete consequence of the encoding difference, as a value rather than as prose: '.' is a legal
        // EC2 tag character, so an id the DigitalOcean adapter rejects outright is ordinary here.
        var tags = ServyxEc2Tags.For("srv.01", "job.42", "conn.1").ToTags();

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
    public void Every_character_ec2_allows_in_a_tag_value_is_accepted(string instanceId) =>
        ServyxEc2Tags.For(instanceId, "job-1", "conn-1").InstanceId.Should().Be(instanceId);

    [Theory]
    [InlineData("srv#01")]
    [InlineData("srv%01")]
    [InlineData("srv*01")]
    [InlineData("srv?01")]
    public void A_character_ec2_forbids_is_refused_at_construction_rather_than_at_launch(string instanceId)
    {
        // Azure would accept all of these - its tag values have no charset restriction at all. EC2 does, so
        // this adapter sits between the two, and the refusal happens before a plan is built rather than as a
        // 400 from RunInstances.
        var error = Assert.Throws<ArgumentException>(() => ServyxEc2Tags.For(instanceId, "job-1", "conn-1"));

        error.Message.Should().Contain("orphan sweep");
    }

    [Fact]
    public void A_tag_value_longer_than_ec2_allows_is_refused()
    {
        var tooLong = new string('a', ServyxEc2Tags.MaxTagValueLength + 1);

        Assert.Throws<ArgumentException>(() => ServyxEc2Tags.For(tooLong, "job-1", "conn-1"));
    }

    [Fact]
    public void A_tag_key_using_the_reserved_aws_prefix_is_refused_because_ec2_would_refuse_the_whole_write()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["aws:cloudformation:stack"] = "x" };

        var error = Assert.Throws<ArgumentException>(() => ServyxEc2Tags.Validate(tags));

        error.Message.Should().Contain("reserves");
    }

    [Fact]
    public void An_extra_tag_can_never_shadow_a_canonical_one()
    {
        var tags = ServyxEc2Tags.For("srv-1", "job-1", "conn-1").ToTags(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServyxTagKeys.Managed] = "false",
                [ServyxTagKeys.InstanceId] = "somebody-elses",
            });

        // A caller that could set servyx.managed=false could hide a billing instance from every orphan sweep.
        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags[ServyxTagKeys.InstanceId].Should().Be("srv-1");
    }

    [Fact]
    public void Ownership_is_an_exact_match_and_never_a_truthiness_test()
    {
        ServyxEc2Tags.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "TRUE",
        }).Should().BeFalse();

        ServyxEc2Tags.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "1",
        }).Should().BeFalse();

        ServyxEc2Tags.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)
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

        // Attributing a resource to the wrong instance is strictly worse than failing to attribute it at all.
        ServyxEc2Tags.FromTags(tags).Should().BeNull();
    }

    [Fact]
    public void The_role_key_lives_in_the_shared_servyx_namespace_like_azures_does()
    {
        ServyxEc2Tags.RoleTag.Should().Be("servyx.role");
        ServyxEc2Tags.RoleTag.Should().StartWith(ServyxTagKeys.Prefix);

        // Descriptive rather than identifying, so it is not one of the canonical keys and travels as an extra.
        ServyxTagKeys.Canonical.Should().NotContain(ServyxEc2Tags.RoleTag);
    }
}
