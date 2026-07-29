using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Provisioning;

/// <summary>
/// The drift guard for the shared provisioning tag vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <c>DockerContainerProvisioner</c> finds orphans by asking the daemon for everything labelled
/// <c>servyx.managed=true</c>; <c>SshProcessProvisioner</c> finds them by reading the same key out of marker
/// files. The two adapters cannot reference each other — infrastructure projects reference
/// <c>Servyx.Domain</c> and nothing else — so the vocabulary was, until it was promoted to
/// <see cref="ServyxTagKeys"/>, duplicated and kept identical by convention alone.
/// </para>
/// <para>
/// That convention had no failure signal. A single character of drift in either copy would not break a
/// build, would not fail any adapter's own tests, and would not raise anything at runtime — it would just
/// mean each adapter's sweep silently stopped recognising resources created by the other, which for a
/// billable resource means it bills forever with no local trace. These tests are the signal: they live in
/// the one assembly that can see both adapters at once, and they fail if the two ever stop agreeing.
/// </para>
/// <para>
/// The tests deliberately compare the tag sets the adapters <em>emit</em>, not the constants they declare.
/// Both currently alias <see cref="ServyxTagKeys"/>, which makes drift impossible by construction — but an
/// adapter reverting to a literal, or bypassing <see cref="ServyxTagKeys.Build"/> to assemble its own
/// dictionary, would restore the old hazard while leaving the constants looking correct.
/// </para>
/// </remarks>
public class CanonicalTagVocabularyTests
{
    private const string InstanceId = "srv-0001";
    private const string JobId = "job-42";
    private const string ConnectorId = "conn-1";

    private static IReadOnlyDictionary<string, string> DockerLabels(IReadOnlyDictionary<string, string>? extras = null) =>
        ServyxResourceTags.For(InstanceId, JobId, ConnectorId).ToLabels(extras);

    private static IReadOnlyDictionary<string, string> SshTags(IReadOnlyDictionary<string, string>? extras = null) =>
        ServyxProcessMarker.For(InstanceId, JobId, ConnectorId).ToTags(extras);

    [Fact]
    public void Both_adapters_emit_the_same_canonical_key_set()
    {
        DockerLabels().Keys.Should().BeEquivalentTo(SshTags().Keys);
    }

    [Fact]
    public void Both_adapters_emit_the_same_canonical_values_for_the_same_identity()
    {
        DockerLabels().Should().BeEquivalentTo(SshTags());
    }

    [Fact]
    public void The_canonical_key_set_is_the_one_the_domain_declares()
    {
        DockerLabels().Keys.Should().BeEquivalentTo(ServyxTagKeys.Canonical);
        SshTags().Keys.Should().BeEquivalentTo(ServyxTagKeys.Canonical);
    }

    [Fact]
    public void Each_adapters_key_constants_name_the_same_strings()
    {
        // The names differ ("label" versus "tag") because each assembly talks about its own store. The
        // values must not.
        ServyxResourceTags.ManagedLabel.Should().Be(ServyxProcessMarker.ManagedTag);
        ServyxResourceTags.ManagedLabelValue.Should().Be(ServyxProcessMarker.ManagedTagValue);
        ServyxResourceTags.InstanceIdLabel.Should().Be(ServyxProcessMarker.InstanceIdTag);
        ServyxResourceTags.JobIdLabel.Should().Be(ServyxProcessMarker.JobIdTag);
        ServyxResourceTags.ConnectorIdLabel.Should().Be(ServyxProcessMarker.ConnectorIdTag);
        ServyxResourceTags.RootPathLabel.Should().Be(ServyxProcessMarker.RootPathTag);
    }

    [Fact]
    public void Both_adapters_read_ownership_the_same_way()
    {
        // The two sweeps must agree on which resources are Servyx's, including the negatives — an adapter
        // that accepted "TRUE" where the other did not would disagree about what is safe to destroy.
        foreach (var managed in new[] { "true", "TRUE", "True", "1", "yes", "false", "" })
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal) { [ServyxTagKeys.Managed] = managed };

            ServyxResourceTags.IsManaged(tags).Should().Be(ServyxProcessMarker.IsManaged(tags), $"for managed value '{managed}'");
        }

        ServyxResourceTags.IsManaged(null).Should().Be(ServyxProcessMarker.IsManaged(null));
        ServyxResourceTags.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal))
            .Should().Be(ServyxProcessMarker.IsManaged(new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Both_adapters_apply_the_canonical_keys_last_so_an_extra_can_never_shadow_one()
    {
        var hostileExtras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = "false",
            [ServyxTagKeys.InstanceId] = "somebody-elses-server",
            [ServyxTagKeys.JobId] = "somebody-elses-job",
            [ServyxTagKeys.ConnectorId] = "somebody-elses-connector",
            ["team"] = "ops",
        };

        var docker = DockerLabels(hostileExtras);
        var ssh = SshTags(hostileExtras);

        docker.Should().BeEquivalentTo(ssh);
        foreach (var tags in new[] { docker, ssh })
        {
            tags[ServyxTagKeys.Managed].Should().Be("true");
            tags[ServyxTagKeys.InstanceId].Should().Be(InstanceId);
            tags[ServyxTagKeys.JobId].Should().Be(JobId);
            tags[ServyxTagKeys.ConnectorId].Should().Be(ConnectorId);
            tags["team"].Should().Be("ops");
        }
    }

    [Fact]
    public void Both_adapters_pass_the_shared_root_path_key_through_as_an_extra_rather_than_as_identity()
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal) { [ServyxTagKeys.RootPath] = "/opt/palworld" };

        DockerLabels(extras)[ServyxResourceTags.RootPathLabel].Should().Be("/opt/palworld");
        SshTags(extras)[ServyxProcessMarker.RootPathTag].Should().Be("/opt/palworld");
        ServyxTagKeys.Canonical.Should().NotContain(ServyxTagKeys.RootPath);
    }

    [Fact]
    public void The_keys_only_the_process_shape_needs_stay_inside_the_shared_namespace()
    {
        // A marker file has to record facts a container object already knows about itself. Those keys are
        // adapter-local by design, but they must not wander out of the servyx.* namespace.
        new[] { ServyxProcessMarker.ProvisionerIdTag, ServyxProcessMarker.ExecutableTag, ServyxProcessMarker.CreatedAtTag }
            .Should().OnlyContain(key => key.StartsWith(ServyxTagKeys.Prefix, StringComparison.Ordinal))
            .And.NotIntersectWith(ServyxTagKeys.Canonical);
    }

    [Fact]
    public void A_handle_from_either_adapter_is_read_the_same_way_by_the_other()
    {
        // The interchangeability claim, stated as a test: everything above provisioning receives a
        // ResourceHandle.Tags dictionary and must not need to know which shape produced it.
        var fromDocker = DockerLabels();
        var fromSsh = SshTags();

        ServyxProcessMarker.FromTags(fromDocker).Should().NotBeNull();
        ServyxResourceTags.FromLabels(fromSsh).Should().NotBeNull();

        ServyxProcessMarker.FromTags(fromDocker)!.InstanceId.Should().Be(ServyxResourceTags.FromLabels(fromDocker)!.InstanceId);
        ServyxResourceTags.FromLabels(fromSsh)!.ConnectorId.Should().Be(ServyxProcessMarker.FromTags(fromSsh)!.ConnectorId);
    }

    [Fact]
    public void Only_the_process_shape_constrains_an_instance_id_to_a_safe_filename_and_that_is_deliberate()
    {
        // A documented, intentional asymmetry rather than drift: a marker instance id becomes part of a path
        // on the target host, so it must be a safe filename stem. A container label is never a path.
        var escaping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = ServyxTagKeys.ManagedValue,
            [ServyxTagKeys.InstanceId] = "../../etc/cron.d/servyx",
            [ServyxTagKeys.JobId] = JobId,
            [ServyxTagKeys.ConnectorId] = ConnectorId,
        };

        ServyxProcessMarker.FromTags(escaping).Should().BeNull();
        ServyxResourceTags.FromLabels(escaping).Should().NotBeNull();
    }
}
