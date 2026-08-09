using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

/// <summary>
/// The exec-path half of per-command re-resolution, at the unit level: a guard built over an
/// <see cref="IWriteModeResolver"/> asks it again on every gated call rather than holding the answer it was
/// constructed with.
/// </summary>
/// <remarks>
/// This is what makes revocation real. <c>Resolve</c> used to be called once per connect and the answer
/// frozen here, while sessions are memoized for the life of the process and never evicted on success — so a
/// revoked grant survived in any session that was already open, indefinitely. The counterpart on the RCON
/// path lives in <c>Servyx.Infrastructure.Rcon.Tests</c>; the two capture sites fail independently.
/// </remarks>
public class WriteGuardedExecutionTargetReResolutionTests
{
    private static TargetDescriptor Target() => new(
        "docker",
        "npipe://./pipe/docker_engine",
        null,
        null,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["containerId"] = "abc" });

    /// <summary>A resolver whose answer a test can change between calls, and that counts how often it was asked.</summary>
    private sealed class MutableResolver(WriteMode initial) : IWriteModeResolver
    {
        public WriteMode Mode { get; set; } = initial;

        public int Calls { get; private set; }

        public WriteMode Resolve(TargetDescriptor target)
        {
            Calls++;
            return Mode;
        }
    }

    [Fact]
    public async Task A_revoked_grant_is_refused_on_the_next_command_of_the_same_guard()
    {
        var inner = Substitute.For<IExecutionTarget>();
        var resolver = new MutableResolver(WriteMode.Enabled);
        var guard = new WriteGuardedExecutionTarget(inner, resolver, Target(), "palworld-server");

        await guard.ExecuteAsync(new CommandSpec("rm", ["-rf", "/x"]));

        resolver.Mode = WriteMode.ReadOnly;

        var act = async () => await guard.ExecuteAsync(new CommandSpec("rm", ["-rf", "/x"]));
        await act.Should().ThrowAsync<WritesDisabledException>();

        await inner.Received(1).ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_grant_is_honoured_on_the_next_command_of_the_same_guard()
    {
        var inner = Substitute.For<IExecutionTarget>();
        var resolver = new MutableResolver(WriteMode.ReadOnly);
        var guard = new WriteGuardedExecutionTarget(inner, resolver, Target(), "palworld-server");

        var refused = async () => await guard.ExecuteAsync(new CommandSpec("rm", ["-rf", "/x"]));
        await refused.Should().ThrowAsync<WritesDisabledException>();

        resolver.Mode = WriteMode.Enabled;

        await guard.ExecuteAsync(new CommandSpec("rm", ["-rf", "/x"]));
        await inner.Received(1).ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_read_only_command_never_consults_the_resolver_at_all()
    {
        // Ordering that matters: a command the caller declared ReadOnly passes in every posture, so there is
        // nothing to decide — and a readiness probe must not start depending on the grant store being
        // reachable just because the grant moved into one.
        var inner = Substitute.For<IExecutionTarget>();
        var resolver = new MutableResolver(WriteMode.ReadOnly);
        var guard = new WriteGuardedExecutionTarget(inner, resolver, Target(), "palworld-server");

        await guard.ExecuteAsync(new CommandSpec("cat", ["/proc/uptime"]) { Intent = CommandIntent.ReadOnly });

        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public void The_reported_mode_tracks_the_resolver_rather_than_a_captured_value()
    {
        var resolver = new MutableResolver(WriteMode.PreviewOnly);
        var guard = new WriteGuardedExecutionTarget(
            Substitute.For<IExecutionTarget>(), resolver, Target(), "palworld-server");

        guard.Mode.Should().Be(WriteMode.PreviewOnly);
        guard.WritesPermitted.Should().BeFalse();

        resolver.Mode = WriteMode.Enabled;

        guard.Mode.Should().Be(WriteMode.Enabled);
        guard.WritesPermitted.Should().BeTrue();
    }

    [Fact]
    public void The_fixed_mode_constructor_still_holds_one_posture_for_its_lifetime()
    {
        // Provisioning hand-offs and tests mint a guard for a target whose posture genuinely cannot change
        // underneath them; that overload is unchanged.
        var guard = new WriteGuardedExecutionTarget(Substitute.For<IExecutionTarget>(), WriteMode.Enabled);

        guard.Mode.Should().Be(WriteMode.Enabled);
        guard.WritesPermitted.Should().BeTrue();
    }

    [Fact]
    public void A_null_resolver_or_descriptor_is_refused_at_construction()
    {
        var inner = Substitute.For<IExecutionTarget>();

        ((Action)(() => _ = new WriteGuardedExecutionTarget(inner, null!, Target())))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new WriteGuardedExecutionTarget(inner, new MutableResolver(WriteMode.ReadOnly), null!)))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task The_refusal_message_names_the_posture_the_decision_was_taken_against()
    {
        var resolver = new MutableResolver(WriteMode.PreviewOnly);
        var guard = new WriteGuardedExecutionTarget(
            Substitute.For<IExecutionTarget>(), resolver, Target(), "palworld-server");

        var act = async () => await guard.ExecuteAsync(new CommandSpec("rm", ["-rf", "/x"]));

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("PreviewOnly").And.Contain("palworld-server");
    }
}
