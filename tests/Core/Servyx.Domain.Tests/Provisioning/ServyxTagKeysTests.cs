using Servyx.Domain.Provisioning;

namespace Servyx.Domain.Tests.Provisioning;

/// <summary>
/// Tests for the shared provisioning tag vocabulary. These pin the values themselves, not just the
/// relationships between them: the keys are written onto real provider resources, so changing one is a
/// breaking change against every resource already created with the old spelling — a sweep would stop
/// recognising them and they would bill forever with no local trace.
/// </summary>
public class ServyxTagKeysTests
{
    [Fact]
    public void The_key_spellings_are_pinned_because_resources_already_carry_them()
    {
        ServyxTagKeys.Managed.Should().Be("servyx.managed");
        ServyxTagKeys.ManagedValue.Should().Be("true");
        ServyxTagKeys.InstanceId.Should().Be("servyx.instance-id");
        ServyxTagKeys.JobId.Should().Be("servyx.job-id");
        ServyxTagKeys.ConnectorId.Should().Be("servyx.connector-id");
        ServyxTagKeys.RootPath.Should().Be("servyx.root-path");
    }

    [Fact]
    public void Every_key_sits_under_the_one_servyx_prefix()
    {
        new[] { ServyxTagKeys.Managed, ServyxTagKeys.InstanceId, ServyxTagKeys.JobId, ServyxTagKeys.ConnectorId, ServyxTagKeys.RootPath }
            .Should().OnlyContain(key => key.StartsWith(ServyxTagKeys.Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void The_canonical_set_is_the_identity_keys_and_not_the_descriptive_ones()
    {
        ServyxTagKeys.Canonical.Should().Equal(
            ServyxTagKeys.Managed,
            ServyxTagKeys.InstanceId,
            ServyxTagKeys.JobId,
            ServyxTagKeys.ConnectorId);

        // root-path describes a resource; it does not identify one. A resource missing it is still
        // unambiguously Servyx-owned, so it travels as an ordinary extra.
        ServyxTagKeys.Canonical.Should().NotContain(ServyxTagKeys.RootPath);
    }

    [Fact]
    public void The_two_drift_expectation_keys_are_pinned_and_are_descriptive_rather_than_identifying()
    {
        // A ResourceHandle has no image or size field, so these keys are the only place a drift check's
        // expectation can live. Their spellings are pinned for the same reason every other key's is.
        ServyxTagKeys.Image.Should().Be("servyx.image");
        ServyxTagKeys.Size.Should().Be("servyx.size");

        ServyxTagKeys.Image.Should().StartWith(ServyxTagKeys.Prefix);
        ServyxTagKeys.Size.Should().StartWith(ServyxTagKeys.Prefix);

        // Neither identifies a resource: one missing them is still unambiguously Servyx-owned and still fully
        // sweepable, so both travel as ordinary extras and a caller can never shadow an identity key with one.
        ServyxTagKeys.Canonical.Should().NotContain(ServyxTagKeys.Image);
        ServyxTagKeys.Canonical.Should().NotContain(ServyxTagKeys.Size);
    }

    [Fact]
    public void Build_emits_exactly_the_canonical_keys_when_given_no_extras()
    {
        var tags = ServyxTagKeys.Build("srv-0001", "job-42", "conn-1");

        tags.Keys.Should().BeEquivalentTo(ServyxTagKeys.Canonical);
        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags[ServyxTagKeys.InstanceId].Should().Be("srv-0001");
        tags[ServyxTagKeys.JobId].Should().Be("job-42");
        tags[ServyxTagKeys.ConnectorId].Should().Be("conn-1");
    }

    [Fact]
    public void Build_writes_the_canonical_keys_last_so_an_extra_can_never_shadow_one()
    {
        // The failure this prevents is not cosmetic: an extra able to set servyx.managed=false would hide a
        // resource Servyx owns from its own orphan sweep.
        var tags = ServyxTagKeys.Build("srv-0001", "job-42", "conn-1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "false",
            [ServyxTagKeys.InstanceId] = "somebody-elses-server",
            [ServyxTagKeys.JobId] = "somebody-elses-job",
            [ServyxTagKeys.ConnectorId] = "somebody-elses-connector",
            ["team"] = "ops",
        });

        tags[ServyxTagKeys.Managed].Should().Be("true");
        tags[ServyxTagKeys.InstanceId].Should().Be("srv-0001");
        tags[ServyxTagKeys.JobId].Should().Be("job-42");
        tags[ServyxTagKeys.ConnectorId].Should().Be("conn-1");
        tags["team"].Should().Be("ops");
    }

    [Fact]
    public void Build_keeps_extras_that_do_not_collide()
    {
        var tags = ServyxTagKeys.Build("srv-0001", "job-42", "conn-1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.RootPath] = "/opt/palworld",
            ["team"] = "ops",
        });

        tags[ServyxTagKeys.RootPath].Should().Be("/opt/palworld");
        tags["team"].Should().Be("ops");
    }

    [Theory]
    [InlineData("", "job-42", "conn-1")]
    [InlineData("   ", "job-42", "conn-1")]
    [InlineData("srv-0001", "", "conn-1")]
    [InlineData("srv-0001", "job-42", "  ")]
    public void Build_refuses_a_blank_identity_because_an_unattributable_resource_cannot_be_swept(
        string instanceId,
        string jobId,
        string connectorId)
    {
        var act = () => ServyxTagKeys.Build(instanceId, jobId, connectorId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsManaged_matches_the_managed_value_exactly_and_nothing_that_merely_looks_true()
    {
        // A sweep's output is a delete list. Treating "1" or "TRUE" as ownership destroys resources Servyx
        // never created.
        ServyxTagKeys.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal) { [ServyxTagKeys.Managed] = "true" })
            .Should().BeTrue();

        foreach (var notManaged in new[] { "TRUE", "True", "1", "yes", "false", "" })
        {
            ServyxTagKeys.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal) { [ServyxTagKeys.Managed] = notManaged })
                .Should().BeFalse($"'{notManaged}' is not the managed value");
        }

        ServyxTagKeys.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)).Should().BeFalse();
        ServyxTagKeys.IsManaged(null).Should().BeFalse();
    }

    [Fact]
    public void TryReadIdentity_round_trips_what_Build_wrote()
    {
        var tags = ServyxTagKeys.Build("srv-0001", "job-42", "conn-1");

        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId).Should().BeTrue();
        instanceId.Should().Be("srv-0001");
        jobId.Should().Be("job-42");
        connectorId.Should().Be("conn-1");
    }

    [Theory]
    [InlineData(ServyxTagKeys.InstanceId)]
    [InlineData(ServyxTagKeys.JobId)]
    [InlineData(ServyxTagKeys.ConnectorId)]
    [InlineData(ServyxTagKeys.Managed)]
    public void TryReadIdentity_refuses_a_partial_identity_rather_than_defaulting_the_gap(string missingKey)
    {
        var tags = new Dictionary<string, string>(ServyxTagKeys.Build("srv-0001", "job-42", "conn-1"), StringComparer.Ordinal);
        tags.Remove(missingKey);

        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId).Should().BeFalse();
        instanceId.Should().BeNull();
        jobId.Should().BeNull();
        connectorId.Should().BeNull();
    }

    [Fact]
    public void TryReadIdentity_treats_a_blank_value_as_missing()
    {
        var tags = new Dictionary<string, string>(ServyxTagKeys.Build("srv-0001", "job-42", "conn-1"), StringComparer.Ordinal)
        {
            [ServyxTagKeys.JobId] = "   ",
        };

        ServyxTagKeys.TryReadIdentity(tags, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadIdentity_reports_nothing_for_a_resource_that_is_not_servyx_managed()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.InstanceId] = "srv-0001",
            [ServyxTagKeys.JobId] = "job-42",
            [ServyxTagKeys.ConnectorId] = "conn-1",
        };

        ServyxTagKeys.TryReadIdentity(tags, out _, out _, out _).Should().BeFalse();
        ServyxTagKeys.TryReadIdentity(null, out _, out _, out _).Should().BeFalse();
    }
}
