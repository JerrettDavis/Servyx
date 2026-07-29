using Servyx.Domain.Provisioning;

namespace Servyx.Domain.Tests.Provisioning;

/// <summary>
/// Tests for the orphan-sweep search space. The point of the type is that a caller can see what a sweep
/// will cover before running it, so these assert on what the scope makes visible rather than on any
/// adapter's behaviour.
/// </summary>
public class OrphanScopeTests
{
    [Fact]
    public void A_provider_wide_scope_carries_the_provisioner_and_no_region_by_default()
    {
        var scope = new OrphanScope.ProviderWide("docker-container");

        scope.ProvisionerId.Should().Be("docker-container");
        scope.Region.Should().BeNull();
    }

    [Fact]
    public void A_provider_wide_scope_can_narrow_to_a_region_for_a_region_scoped_provider()
    {
        var scope = new OrphanScope.ProviderWide("hetzner", "nbg1");

        scope.Region.Should().Be("nbg1");
    }

    [Fact]
    public void A_marker_directory_scope_states_the_directory_the_sweep_will_enumerate()
    {
        // This is the whole defect the shape exists to fix: before it, the swept directory lived in adapter
        // constructor state and was invisible to whoever held the scope.
        var scope = new OrphanScope.MarkerDirectory("ssh-process", "/srv/servyx/instances");

        scope.ProvisionerId.Should().Be("ssh-process");
        scope.MarkerRoot.Should().Be("/srv/servyx/instances");
        scope.Region.Should().BeNull();
    }

    [Fact]
    public void Every_scope_shape_is_an_OrphanScope_so_the_interface_stays_one_method()
    {
        OrphanScope providerWide = new OrphanScope.ProviderWide("docker-container");
        OrphanScope markerDirectory = new OrphanScope.MarkerDirectory("ssh-process", "/var/lib/servyx/instances");

        providerWide.ProvisionerId.Should().Be("docker-container");
        markerDirectory.ProvisionerId.Should().Be("ssh-process");
    }

    [Fact]
    public void The_shapes_are_distinguishable_so_an_adapter_can_decline_a_space_it_cannot_serve()
    {
        OrphanScope scope = new OrphanScope.MarkerDirectory("ssh-process", "/var/lib/servyx/instances");

        scope.Should().BeOfType<OrphanScope.MarkerDirectory>();
        (scope is OrphanScope.ProviderWide).Should().BeFalse();
    }

    [Fact]
    public void Two_scopes_of_the_same_shape_and_values_are_equal()
    {
        new OrphanScope.ProviderWide("docker-container")
            .Should().Be(new OrphanScope.ProviderWide("docker-container"));

        new OrphanScope.MarkerDirectory("ssh-process", "/var/lib/servyx/instances")
            .Should().Be(new OrphanScope.MarkerDirectory("ssh-process", "/var/lib/servyx/instances"));
    }

    [Fact]
    public void Scopes_differing_in_shape_region_or_root_are_not_equal()
    {
        OrphanScope providerWide = new OrphanScope.ProviderWide("ssh-process");
        OrphanScope markerDirectory = new OrphanScope.MarkerDirectory("ssh-process", "/var/lib/servyx/instances");

        providerWide.Should().NotBe(markerDirectory);
        new OrphanScope.ProviderWide("hetzner", "nbg1").Should().NotBe(new OrphanScope.ProviderWide("hetzner", "fsn1"));
        new OrphanScope.MarkerDirectory("ssh-process", "/a").Should().NotBe(new OrphanScope.MarkerDirectory("ssh-process", "/b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_scope_with_no_provisioner_is_rejected(string provisionerId)
    {
        var providerWide = () => new OrphanScope.ProviderWide(provisionerId);
        var markerDirectory = () => new OrphanScope.MarkerDirectory(provisionerId, "/var/lib/servyx/instances");

        providerWide.Should().Throw<ArgumentException>();
        markerDirectory.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_marker_directory_scope_with_no_root_is_rejected(string markerRoot)
    {
        var act = () => new OrphanScope.MarkerDirectory("ssh-process", markerRoot);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_domain_does_not_impose_path_syntax_on_a_marker_root()
    {
        // Servyx.Domain knows nothing about POSIX paths, and pretending otherwise would bake one adapter's
        // filesystem rules into the taxonomy. Rejecting an unusable root is the adapter's job.
        var act = () => new OrphanScope.MarkerDirectory("ssh-process", "C:\\not\\a\\posix\\path");

        act.Should().NotThrow();
    }
}
