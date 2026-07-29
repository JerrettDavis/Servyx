using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The tag encoding, pinned character by character.
/// </summary>
/// <remarks>
/// DigitalOcean tags are flat strings restricted to letters, digits, <c>:</c>, <c>-</c> and <c>_</c>, so
/// Servyx's <c>servyx.managed=true</c> vocabulary — which uses both <c>.</c> and <c>=</c> — cannot be stored
/// literally and has to be encoded. Everything about orphan sweeping depends on that encoding being exact in
/// both directions, and on it being the <em>same</em> exact thing tomorrow that it was today: a droplet whose
/// managed tag does not match byte-for-byte is invisible to the sweep and bills forever. These tests are the
/// signal, so they are written as literals rather than as round-trips through the code under test.
/// </remarks>
public class ServyxDropletTagsTests
{
    [Fact]
    public void The_managed_filter_is_exactly_the_string_a_human_would_type_into_the_console()
    {
        ServyxDropletTags.ManagedFilter.Should().Be("servyx_managed:true");
    }

    [Fact]
    public void The_canonical_keys_encode_to_their_documented_wire_forms()
    {
        ServyxDropletTags.Encode(ServyxTagKeys.Managed, "true").Should().Be("servyx_managed:true");
        ServyxDropletTags.Encode(ServyxTagKeys.InstanceId, "srv-0001").Should().Be("servyx_instance-id:srv-0001");
        ServyxDropletTags.Encode(ServyxTagKeys.JobId, "job-42").Should().Be("servyx_job-id:job-42");
        ServyxDropletTags.Encode(ServyxTagKeys.ConnectorId, "conn-1").Should().Be("servyx_connector-id:conn-1");
    }

    [Fact]
    public void Every_canonical_key_survives_a_round_trip_unchanged()
    {
        foreach (var key in ServyxTagKeys.Canonical)
        {
            ServyxDropletTags.TryDecode(ServyxDropletTags.Encode(key, "value-1"), out var decodedKey, out var decodedValue)
                .Should().BeTrue();

            decodedKey.Should().Be(key);
            decodedValue.Should().Be("value-1");
        }
    }

    [Fact]
    public void No_canonical_key_contains_the_character_the_encoding_reserves()
    {
        // The reversibility of '.' -> '_' rests entirely on this. It is asserted rather than assumed, because
        // the day someone adds servyx.some_key the encoding stops being injective and this must fail loudly.
        ServyxTagKeys.Canonical.Should().OnlyContain(k => !k.Contains(ServyxDropletTags.KeyDotReplacement));
    }

    [Fact]
    public void A_key_containing_the_reserved_character_is_refused_rather_than_silently_mangled()
    {
        var attempt = () => ServyxDropletTags.Encode("servyx.some_key", "value");

        attempt.Should().Throw<ArgumentException>().WithMessage("*must not contain*");
    }

    [Fact]
    public void A_value_carrying_a_character_digitalocean_rejects_is_refused_rather_than_silently_mangled()
    {
        // A '.' in a value would collide with the key encoding on the way back, and '/' and '=' are simply not
        // legal DigitalOcean tag characters. Every one of these is a refusal, never a best-effort substitution.
        foreach (var value in new[] { "srv.0001", "/opt/palworld", "a=b", "2026-07-27T10:00:00.000Z", "has space" })
        {
            var attempt = () => ServyxDropletTags.Encode(ServyxTagKeys.InstanceId, value);
            attempt.Should().Throw<ArgumentException>($"'{value}' is not expressible as a DigitalOcean tag value");
        }
    }

    [Fact]
    public void A_value_may_contain_a_colon_because_decoding_splits_on_the_first_one()
    {
        var encoded = ServyxDropletTags.Encode(ServyxTagKeys.ConnectorId, "ssh:prod:1");

        encoded.Should().Be("servyx_connector-id:ssh:prod:1");
        ServyxDropletTags.TryDecode(encoded, out var key, out var value).Should().BeTrue();
        key.Should().Be(ServyxTagKeys.ConnectorId);
        value.Should().Be("ssh:prod:1");
    }

    [Fact]
    public void A_value_may_contain_an_underscore_because_only_the_key_half_is_transformed()
    {
        var encoded = ServyxDropletTags.Encode(ServyxTagKeys.JobId, "job_42");

        ServyxDropletTags.TryDecode(encoded, out var key, out var value).Should().BeTrue();
        key.Should().Be(ServyxTagKeys.JobId);
        value.Should().Be("job_42");
    }

    [Fact]
    public void An_identity_that_cannot_be_tagged_is_refused_at_construction()
    {
        // The direct analogue of the SSH adapter refusing an instance id that is not a safe filename stem: an
        // id that becomes part of a provider-side identifier inherits that identifier's charset. Refusing here
        // is what stops a droplet existing that a sweep could not attribute back to Servyx.
        var attempt = () => ServyxDropletTags.For("srv.0001", "job-42", "conn-1");

        attempt.Should().Throw<ArgumentException>().WithMessage("*cannot be carried as a DigitalOcean tag value*");
    }

    [Fact]
    public void A_whole_tag_set_round_trips_through_the_droplet_wire_form()
    {
        var identity = ServyxDropletTags.For("srv-0001", "job-42", "conn-1");
        var tags = identity.ToTags();

        var wire = ServyxDropletTags.ToDropletTags(tags);
        var back = ServyxDropletTags.FromDropletTagsToDictionary(wire);

        back.Should().BeEquivalentTo(tags);
        ServyxDropletTags.FromDropletTags(wire)!.InstanceId.Should().Be("srv-0001");
    }

    [Fact]
    public void The_wire_form_is_key_sorted_so_two_identical_tag_sets_produce_identical_arrays()
    {
        var identity = ServyxDropletTags.For("srv-0001", "job-42", "conn-1");

        ServyxDropletTags.ToDropletTags(identity.ToTags()).Should().Equal(
            "servyx_connector-id:conn-1",
            "servyx_instance-id:srv-0001",
            "servyx_job-id:job-42",
            "servyx_managed:true");
    }

    [Fact]
    public void The_instance_helper_produces_the_same_array_as_the_static_one()
    {
        var identity = ServyxDropletTags.For("srv-0001", "job-42", "conn-1");

        identity.ToDropletTagArray().Should().Equal(ServyxDropletTags.ToDropletTags(identity.ToTags()));
    }

    [Fact]
    public void Ownership_is_an_exact_match_and_nothing_close_to_it_counts()
    {
        ServyxDropletTags.IsManaged(["servyx_managed:true"]).Should().BeTrue();

        foreach (var nearMiss in new[] { "servyx_managed:TRUE", "servyx_managed:True", "servyx_managed:1", "servyx_managed:yes", "servyx.managed:true", "servyx_managed" })
        {
            ServyxDropletTags.IsManaged([nearMiss]).Should().BeFalse($"'{nearMiss}' must not be read as Servyx ownership");
        }

        ServyxDropletTags.IsManaged(null).Should().BeFalse();
        ServyxDropletTags.IsManaged([]).Should().BeFalse();
    }

    [Fact]
    public void Tags_this_encoding_did_not_produce_are_walked_past_rather_than_erroring()
    {
        // A real DigitalOcean account holds human- and tool-applied tags. A sweep has to survive them.
        var decoded = ServyxDropletTags.FromDropletTagsToDictionary(
            ["production", "team:ops", "servyx_managed:true", "k8s:cluster:prod"]);

        decoded.Should().ContainKey(ServyxTagKeys.Managed);
        decoded.Should().NotContainKey("production");
        decoded["team"].Should().Be("ops");
    }

    [Fact]
    public void A_partially_tagged_droplet_is_reported_as_unidentifiable_rather_than_defaulted()
    {
        ServyxDropletTags.FromDropletTags(["servyx_managed:true", "servyx_instance-id:srv-0001"]).Should().BeNull();
        ServyxDropletTags.FromDropletTags(["servyx_instance-id:srv-0001", "servyx_job-id:job-42", "servyx_connector-id:conn-1"]).Should().BeNull();
    }

    [Fact]
    public void An_extra_tag_can_never_shadow_a_canonical_one()
    {
        var hostile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "false",
            [ServyxTagKeys.InstanceId] = "somebody-elses-server",
            ["team"] = "ops",
        };

        var tags = ServyxDropletTags.For("srv-0001", "job-42", "conn-1").ToTags(hostile);

        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags[ServyxTagKeys.InstanceId].Should().Be("srv-0001");
        tags["team"].Should().Be("ops");
    }

    [Fact]
    public void A_tag_over_digitaloceans_length_limit_is_refused()
    {
        var attempt = () => ServyxDropletTags.Encode(ServyxTagKeys.InstanceId, new string('a', ServyxDropletTags.MaxTagLength));

        attempt.Should().Throw<ArgumentException>().WithMessage("*255-character limit*");
    }

    [Fact]
    public void The_vocabulary_is_the_domains_and_is_not_redefined_here()
    {
        // The same guard CanonicalTagVocabularyTests applies across the Docker and SSH adapters: this adapter
        // aliases ServyxTagKeys rather than spelling the keys out, so drift is a compile-time impossibility.
        ServyxDropletTags.ManagedTag.Should().Be(ServyxTagKeys.Managed);
        ServyxDropletTags.ManagedTagValue.Should().Be(ServyxTagKeys.ManagedValue);
        ServyxDropletTags.InstanceIdTag.Should().Be(ServyxTagKeys.InstanceId);
        ServyxDropletTags.JobIdTag.Should().Be(ServyxTagKeys.JobId);
        ServyxDropletTags.ConnectorIdTag.Should().Be(ServyxTagKeys.ConnectorId);

        ServyxDropletTags.For("srv-0001", "job-42", "conn-1").ToTags().Keys
            .Should().BeEquivalentTo(ServyxTagKeys.Canonical);
    }
}
