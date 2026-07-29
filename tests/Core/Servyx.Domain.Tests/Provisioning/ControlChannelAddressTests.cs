using System.Reflection;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Provisioning;

/// <summary>
/// Tests for the address a control channel may be pinned to — the question that only becomes interesting
/// once <see cref="ResourceReachability"/> has answered <see cref="ResourceReachability.NoTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// These assert three properties. First, that "there is an address but it will not survive" can be
/// <em>said</em>, separately from both "there is a durable one" and "there is none" — the middle case is the
/// one that would otherwise ship as a silent bug. Second, that a durable claim cannot be made without
/// stating why, because that claim is the one that is expensive to get wrong. Third, and most importantly,
/// that nothing on this type hands back a <see cref="TargetDescriptor"/> or names a transport: answering
/// this question must not be a back door to the answer <see cref="ResourceReachability"/> gives.
/// </para>
/// <para>
/// As in <see cref="ResourceReachabilityTests"/>, no <c>Should().Match(x =&gt; x is …)</c> appears here:
/// that overload takes an expression tree and a pattern-matching operator inside one is CS8122.
/// </para>
/// </remarks>
public class ControlChannelAddressTests
{
    private const string Fqdn = "palworld.eastus.azurecontainer.io";

    private const string DurableBecause =
        "the container group was provisioned with a dnsNameLabel, and Azure keeps that name pointed at whatever "
        + "public IP the group currently holds.";

    private const string NotDurableBecause =
        "the container group has no dnsNameLabel, so its public IP is the only address it has, and ACI warns that "
        + "IP may change when the group restarts.";

    [Fact]
    public void A_durable_address_carries_the_host_and_the_evidence_for_calling_it_durable()
    {
        var address = new ControlChannelAddress.Durable(Fqdn, DurableBecause);

        address.Host.Should().Be(Fqdn);
        address.Justification.Should().Be(DurableBecause);
    }

    [Fact]
    public void A_durable_address_cannot_be_claimed_without_saying_why_it_survives()
    {
        var act = () => new ControlChannelAddress.Durable(Fqdn, "   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_durable_address_cannot_be_built_without_a_host()
    {
        var act = () => new ControlChannelAddress.Durable(string.Empty, DurableBecause);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_ephemeral_address_keeps_the_host_it_holds_today_and_the_reason_it_will_not_keep_it()
    {
        var address = new ControlChannelAddress.Ephemeral("203.0.113.42", NotDurableBecause);

        address.Host.Should().Be("203.0.113.42");
        address.Reason.Should().Be(NotDurableBecause);
    }

    [Fact]
    public void An_absent_address_carries_the_reason_a_null_could_not()
    {
        var address = new ControlChannelAddress.NoAddress(NotDurableBecause);

        address.Reason.Should().Be(NotDurableBecause);
    }

    [Fact]
    public void An_absent_address_cannot_be_built_without_a_reason()
    {
        var act = () => new ControlChannelAddress.NoAddress(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Only_a_durable_address_is_openable()
    {
        new ControlChannelAddress.Durable(Fqdn, DurableBecause).OpenableHostOrNull().Should().Be(Fqdn);
        new ControlChannelAddress.Ephemeral("203.0.113.42", NotDurableBecause).OpenableHostOrNull().Should().BeNull();
        new ControlChannelAddress.NoAddress(NotDurableBecause).OpenableHostOrNull().Should().BeNull();
    }

    [Fact]
    public void An_ephemeral_host_is_reachable_only_by_naming_the_ephemeral_case()
    {
        // The point of OpenableHostOrNull answering null for a host that exists: reading it is a deliberate
        // act. If this ever becomes a plain Host property on the base type, the distinction is gone.
        ControlChannelAddress address = new ControlChannelAddress.Ephemeral("203.0.113.42", NotDurableBecause);

        address.OpenableHostOrNull().Should().BeNull();
        address.Should().BeOfType<ControlChannelAddress.Ephemeral>()
            .Which.Host.Should().Be("203.0.113.42");
    }

    [Fact]
    public void Every_case_explains_itself()
    {
        new ControlChannelAddress.Durable(Fqdn, DurableBecause).Explanation.Should().Be(DurableBecause);
        new ControlChannelAddress.Ephemeral("203.0.113.42", NotDurableBecause).Explanation.Should().Be(NotDurableBecause);
        new ControlChannelAddress.NoAddress(NotDurableBecause).Explanation.Should().Be(NotDurableBecause);
    }

    [Fact]
    public void The_hierarchy_is_closed_so_a_fourth_answer_cannot_be_invented_outside_the_domain()
    {
        // Same guarantee ResourceReachability gives: the base's own constructor is private, so every case
        // lives here and adding one is a reviewed act rather than a value slipped past a caller. The
        // protected copy constructor the compiler synthesises for a record is excluded deliberately - it
        // clones an existing case and cannot introduce a new one.
        typeof(ControlChannelAddress).GetConstructors().Should().BeEmpty();

        typeof(ControlChannelAddress)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c => c.GetParameters().Length == 0)
            .Should().ContainSingle()
            .Which.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void No_case_hands_back_a_transport_target_or_names_a_transport()
    {
        // The load-bearing test in this file. A control-channel address answers "where would RCON connect",
        // and must never become a second, quieter route to the answer ResourceReachability gives. If a
        // TargetDescriptor - or anything called a transport id - ever appears on this hierarchy, an
        // unreachable resource has acquired a way to look reachable.
        var types = new[]
        {
            typeof(ControlChannelAddress),
            typeof(ControlChannelAddress.Durable),
            typeof(ControlChannelAddress.Ephemeral),
            typeof(ControlChannelAddress.NoAddress),
        };

        foreach (var type in types)
        {
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Should().NotContain(p => p.PropertyType == typeof(TargetDescriptor));

            type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Should().NotContain(m => m.ReturnType == typeof(TargetDescriptor));

            type.GetMembers(BindingFlags.Instance | BindingFlags.Public)
                .Select(m => m.Name)
                .Should().NotContain("TransportId");
        }
    }

    [Fact]
    public void The_address_source_promises_only_an_address_and_never_a_target()
    {
        // The same guarantee, one level up: an adapter answering "where would a control channel connect"
        // returns a ControlChannelAddress and nothing else. A method here returning a TargetDescriptor
        // would let a provisioner hand back a target through the control-channel door.
        var methods = typeof(IControlChannelAddressSource).GetMethods();

        methods.Should().ContainSingle();
        methods[0].ReturnType.Should().Be<Task<ControlChannelAddress>>();
    }
}
