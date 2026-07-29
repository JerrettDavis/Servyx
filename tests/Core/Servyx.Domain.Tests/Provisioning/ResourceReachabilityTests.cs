using System.Reflection;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Provisioning;

/// <summary>
/// Tests for "provisioned but unreachable by any transport" — the state that had no expression in this
/// domain before, and whose absence is why <c>docs/provisioning.md</c> §11 concluded a managed container
/// service could not be adapted honestly.
/// </summary>
/// <remarks>
/// <para>
/// These assert the two properties the shape exists to buy, and nothing else. First, that the unreachable
/// state can be <em>said</em> at all, with a reason attached. Second — and this is the one a nullable
/// <c>TargetDescriptor?</c> would not have bought — that it cannot be <em>ignored</em>: there is no member
/// on <see cref="ProvisionedResource"/> that hands back a descriptor without either establishing
/// reachability or admitting in its own signature that it might not exist.
/// </para>
/// <para>
/// Note the deliberate absence of any <c>Should().Match(x =&gt; x is …)</c> in this file. That overload takes
/// an <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c>, and a pattern-matching operator inside an expression tree
/// is a compile error (CS8122). Reachability questions are asked here with <c>BeOfType</c> or with an
/// ordinary (non-expression) local function instead.
/// </para>
/// </remarks>
public class ResourceReachabilityTests
{
    private static readonly TargetDescriptor Ssh = new(
        TransportId: "ssh",
        Endpoint: "ssh://azureuser@10.0.0.4:22",
        CredentialUrn: "secret://connector/c1/ssh/key",
        DockerContext: null,
        Options: new Dictionary<string, string>(StringComparer.Ordinal) { ["rootPath"] = "/" });

    private const string AciReason =
        "An Azure Container Instances container group exposes no Docker daemon and no sshd, and is not the "
        + "Servyx host, so no transport in this system can address it.";

    [Fact]
    public void A_reachable_resource_names_the_transport_target_it_was_given()
    {
        var reachability = new ResourceReachability.ViaTransport(Ssh);

        reachability.Target.Should().BeSameAs(Ssh);
    }

    [Fact]
    public void An_unreachable_resource_carries_the_reason_a_null_could_not()
    {
        var reachability = new ResourceReachability.NoTransport(AciReason);

        reachability.Reason.Should().Be(AciReason);
    }

    [Fact]
    public void A_reachable_state_cannot_be_built_without_a_target()
    {
        var act = () => new ResourceReachability.ViaTransport(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unreachable_state_cannot_be_built_without_saying_why(string reason)
    {
        // The reason is the whole difference between this and a null. An operator staring at a resource
        // Servyx created but will not connect to needs to be told nothing is broken.
        var act = () => new ResourceReachability.NoTransport(reason);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_two_shapes_are_distinguishable_so_a_consumer_can_branch_on_them()
    {
        ResourceReachability reachable = new ResourceReachability.ViaTransport(Ssh);
        ResourceReachability unreachable = new ResourceReachability.NoTransport(AciReason);

        reachable.Should().BeOfType<ResourceReachability.ViaTransport>();
        unreachable.Should().BeOfType<ResourceReachability.NoTransport>();
        IsReachable(reachable).Should().BeTrue();
        IsReachable(unreachable).Should().BeFalse();
    }

    [Fact]
    public void The_hierarchy_is_closed_so_a_new_shape_cannot_be_added_from_outside_the_domain()
    {
        // The private constructor is what makes the set closed: an assembly outside Servyx.Domain cannot
        // derive from ResourceReachability at all, so a consumer's exhaustive branch stays exhaustive.
        // The compiler-generated protected copy constructor is excluded: it takes the record itself and
        // cannot be chained to from a new subclass, so it opens nothing.
        var constructors = typeof(ResourceReachability)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c => !c.IsPrivate)
            .Where(c => !IsCopyConstructor(c))
            .ToList();

        constructors.Should().BeEmpty(
            "a non-private constructor would let a fourth reachability shape appear from an infrastructure project");

        typeof(ResourceReachability).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void Two_states_of_the_same_shape_and_value_are_equal()
    {
        new ResourceReachability.ViaTransport(Ssh)
            .Should().Be(new ResourceReachability.ViaTransport(Ssh));

        new ResourceReachability.NoTransport(AciReason)
            .Should().Be(new ResourceReachability.NoTransport(AciReason));
    }

    [Fact]
    public void A_reachable_state_is_never_equal_to_an_unreachable_one()
    {
        ResourceReachability reachable = new ResourceReachability.ViaTransport(Ssh);
        ResourceReachability unreachable = new ResourceReachability.NoTransport(AciReason);

        reachable.Should().NotBe(unreachable);
        new ResourceReachability.NoTransport("a").Should().NotBe(new ResourceReachability.NoTransport("b"));
    }

    [Fact]
    public void The_descriptor_constructor_is_the_unchanged_path_and_wraps_rather_than_copies()
    {
        // This is the compatibility guarantee the six existing adapters rely on: they pass a descriptor and
        // get a reachable resource, with the very same descriptor instance handed straight back out.
        var resource = new ProvisionedResource(Handle, "connector-1", Ssh, Facts);

        resource.Reachability.Should().BeOfType<ResourceReachability.ViaTransport>();
        resource.RequireTarget().Should().BeSameAs(Ssh);
        resource.TargetOrNull().Should().BeSameAs(Ssh);
    }

    [Fact]
    public void An_unreachable_resource_is_expressible_on_ProvisionedResource_itself()
    {
        // §11.6's finding, now answered: the shape fails IProvisioner's return type, not its verbs.
        var resource = new ProvisionedResource(
            Handle,
            "connector-1",
            new ResourceReachability.NoTransport(AciReason),
            Facts);

        resource.Handle.Should().BeSameAs(Handle);
        resource.ConnectorId.Should().Be("connector-1");
        resource.Facts.Should().BeSameAs(Facts);
        resource.Reachability.Should().BeOfType<ResourceReachability.NoTransport>();
    }

    [Fact]
    public void Asking_an_unreachable_resource_for_a_target_throws_and_says_why()
    {
        var resource = new ProvisionedResource(
            Handle,
            "connector-1",
            new ResourceReachability.NoTransport(AciReason),
            Facts);

        var act = () => resource.RequireTarget();

        // It throws rather than returning a fabricated descriptor, and the message carries the adapter's own
        // reason so the failure explains itself instead of surfacing later as "no transport for id".
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*" + Handle.ProviderResourceId + "*")
            .WithMessage("*no sshd*");
    }

    [Fact]
    public void An_unreachable_resource_reports_no_target_to_a_caller_that_asked_nullably()
    {
        var resource = new ProvisionedResource(
            Handle,
            "connector-1",
            new ResourceReachability.NoTransport(AciReason),
            Facts);

        resource.TargetOrNull().Should().BeNull();
    }

    [Fact]
    public void There_is_no_property_that_hands_back_a_descriptor_without_asking_the_question()
    {
        // The regression this whole change exists to prevent. A property returning TargetDescriptor would be
        // the old shape restored; a property returning TargetDescriptor? would be the weaker option the
        // design rejected, since a single '!' silences it with no trace in a diff. Both are absent by
        // construction, and this pins that rather than trusting review.
        var descriptorProperties = typeof(ProvisionedResource)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.PropertyType == typeof(TargetDescriptor))
            .Select(p => p.Name)
            .ToList();

        descriptorProperties.Should().BeEmpty();

        typeof(ProvisionedResource)
            .GetProperty("Target", BindingFlags.Instance | BindingFlags.Public)
            .Should().BeNull();
    }

    [Fact]
    public void Reachability_survives_a_with_expression_unchanged()
    {
        var resource = new ProvisionedResource(Handle, "connector-1", Ssh, Facts);

        var moved = resource with { ConnectorId = "connector-2" };

        moved.ConnectorId.Should().Be("connector-2");
        moved.RequireTarget().Should().BeSameAs(Ssh);
    }

    [Fact]
    public void Two_resources_differing_only_in_reachability_are_not_equal()
    {
        var reachable = new ProvisionedResource(Handle, "connector-1", Ssh, Facts);
        var unreachable = new ProvisionedResource(
            Handle,
            "connector-1",
            new ResourceReachability.NoTransport(AciReason),
            Facts);

        reachable.Should().NotBe(unreachable);
    }

    private static ResourceHandle Handle { get; } = new(
        "azure-container-instance",
        "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ContainerInstance/containerGroups/cg",
        "eastus",
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static ResourceFacts Facts { get; } = new(
        PublicAddress: "203.0.113.9",
        PrivateAddress: null,
        Cost: CostEstimate.Unknown("test"),
        CreatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>
    /// An ordinary local method rather than an inline lambda: <c>Should().Match(...)</c> compiles its
    /// argument to an expression tree, and a pattern-matching operator is illegal there (CS8122).
    /// </summary>
    private static bool IsReachable(ResourceReachability reachability) =>
        reachability is ResourceReachability.ViaTransport;

    /// <summary>The compiler-generated <c>protected ResourceReachability(ResourceReachability original)</c>.</summary>
    private static bool IsCopyConstructor(ConstructorInfo constructor)
    {
        var parameters = constructor.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(ResourceReachability);
    }
}
