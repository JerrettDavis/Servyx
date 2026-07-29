using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

/// <summary>
/// Write mode is resolved per target, from grants a composition root wrote by hand. These tests pin the two
/// properties that make that safe: an unmatched target is read-only, and no grant can be written that
/// covers everything a transport can reach.
/// </summary>
public class WriteModeResolutionTests
{
    private static TargetDescriptor Docker(string containerName, string endpoint = "npipe://./pipe/docker_engine") =>
        new("docker", endpoint, null, null, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["containerName"] = containerName,
        });

    [Fact]
    public void The_default_resolver_makes_every_target_read_only()
    {
        ReadOnlyWriteModeResolver.Instance.Resolve(Docker("palworld-server")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_target_no_grant_names_is_read_only()
    {
        var resolver = new GrantedWriteModeResolver(
            [new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: new Dictionary<string, string> { ["containerName"] = "palworld-server" })]);

        resolver.Resolve(Docker("someone-elses-container")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void No_grants_at_all_is_read_only()
    {
        new GrantedWriteModeResolver(null).Resolve(Docker("palworld-server")).Should().Be(WriteMode.ReadOnly);
        new GrantedWriteModeResolver([]).Resolve(Docker("palworld-server")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_grant_naming_the_target_enables_writes_for_that_target_only()
    {
        var resolver = new GrantedWriteModeResolver(
            [new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: new Dictionary<string, string> { ["containerName"] = "palworld-server" })]);

        resolver.Resolve(Docker("palworld-server")).Should().Be(WriteMode.Enabled);
        resolver.Resolve(Docker("minecraft-server")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_grant_is_scoped_to_its_transport()
    {
        var resolver = new GrantedWriteModeResolver(
            [new WriteModeGrant(WriteMode.Enabled, "ssh", endpoint: "ssh://host:22")]);

        resolver.Resolve(new TargetDescriptor("ssh", "ssh://host:22", null, null, new Dictionary<string, string>()))
            .Should().Be(WriteMode.Enabled);
        resolver.Resolve(new TargetDescriptor("docker", "ssh://host:22", null, null, new Dictionary<string, string>()))
            .Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_grant_is_scoped_to_its_endpoint()
    {
        var resolver = new GrantedWriteModeResolver(
            [new WriteModeGrant(WriteMode.Enabled, "ssh", endpoint: "ssh://granted:22")]);

        resolver.Resolve(new TargetDescriptor("ssh", "ssh://elsewhere:22", null, null, new Dictionary<string, string>()))
            .Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void Every_required_option_must_match_not_merely_one_of_them()
    {
        var resolver = new GrantedWriteModeResolver(
        [
            new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: new Dictionary<string, string>
            {
                ["containerName"] = "palworld-server",
                ["rootPath"] = "/palworld",
            }),
        ]);

        resolver.Resolve(Docker("palworld-server")).Should().Be(WriteMode.ReadOnly, "rootPath is absent from the descriptor");
    }

    [Fact]
    public void When_two_grants_disagree_the_most_restrictive_wins()
    {
        var options = new Dictionary<string, string> { ["containerName"] = "palworld-server" };
        var resolver = new GrantedWriteModeResolver(
        [
            new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: options),
            new WriteModeGrant(WriteMode.PreviewOnly, "docker", requiredOptions: options),
        ]);

        resolver.Resolve(Docker("palworld-server")).Should().Be(WriteMode.PreviewOnly);
    }

    [Fact]
    public void A_write_grant_that_names_no_specific_target_cannot_be_constructed()
    {
        // "Enable writes for every container this daemon can see" is the sentence M4 does not allow anyone to
        // say — not services, and not the composition root either.
        var act = () => new WriteModeGrant(WriteMode.Enabled, "docker");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_unconstrained_read_only_grant_is_allowed_because_it_grants_nothing()
    {
        var grant = new WriteModeGrant(WriteMode.ReadOnly, "docker");

        grant.Matches(Docker("anything")).Should().BeTrue();
        new GrantedWriteModeResolver([grant]).Resolve(Docker("anything")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public async Task The_transport_guard_wraps_every_session_it_hands_out()
    {
        var inner = Substitute.For<ITransport>();
        var session = Substitute.For<IExecutionTarget>();
        inner.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));

        var transport = new WriteGuardedTransport(inner, new GrantedWriteModeResolver(
            [new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: new Dictionary<string, string> { ["containerName"] = "palworld-server" })]));

        await using var granted = await transport.ConnectAsync(Docker("palworld-server"));
        await using var refused = await transport.ConnectAsync(Docker("minecraft-server"));

        granted.Should().BeOfType<WriteGuardedExecutionTarget>().Which.Mode.Should().Be(WriteMode.Enabled);
        refused.Should().BeOfType<WriteGuardedExecutionTarget>().Which.Mode.Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public async Task The_transport_guard_defaults_to_read_only_when_given_no_resolver()
    {
        var inner = Substitute.For<ITransport>();
        inner.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IExecutionTarget>()));

        await using var session = await new WriteGuardedTransport(inner).ConnectAsync(Docker("palworld-server"));

        session.Should().BeOfType<WriteGuardedExecutionTarget>().Which.Mode.Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void The_transport_guard_delegates_its_identity_so_transport_selection_by_id_still_works()
    {
        var inner = Substitute.For<ITransport>();
        inner.TransportId.Returns("ssh");
        inner.Capabilities.Returns(TransportCapabilities.FileRead | TransportCapabilities.FileWrite);

        var transport = new WriteGuardedTransport(inner);

        transport.TransportId.Should().Be("ssh");
        transport.Capabilities.Should().Be(TransportCapabilities.FileRead | TransportCapabilities.FileWrite);
        transport.Inner.Should().BeSameAs(inner);
    }
}
