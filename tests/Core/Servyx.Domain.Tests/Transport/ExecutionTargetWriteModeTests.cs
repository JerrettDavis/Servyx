using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

/// <summary>
/// One shared reading of a target's write posture, so the SSH backup provider, the local backup provider and
/// the local process provisioner cannot drift into three slightly different answers to the same question.
/// These tests pin the answer itself; each adapter's tests pin that it asks.
/// </summary>
public class ExecutionTargetWriteModeTests
{
    private static IExecutionTarget Bare() => Substitute.For<IExecutionTarget>();

    private static IExecutionTarget Guarded(WriteMode mode) => new WriteGuardedExecutionTarget(Bare(), mode);

    private static ICompositeExecutionTarget Composite(IExecutionTarget? exec, IExecutionTarget? file)
    {
        var composite = Substitute.For<ICompositeExecutionTarget>();
        composite.ExecTarget.Returns(exec);
        composite.FileTarget.Returns(file);
        return composite;
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    [InlineData(WriteMode.Enabled)]
    public void A_guarded_target_answers_the_mode_it_carries(WriteMode mode) =>
        ExecutionTargetWriteMode.Resolve(Guarded(mode)).Should().Be(mode);

    [Fact]
    public void An_unguarded_target_answers_null_rather_than_inventing_a_posture() =>
        // Absence of a guard is not a claim about the server. Refusing here would make this a second policy
        // instead of a reader of the one the composition root set.
        ExecutionTargetWriteMode.Resolve(Bare()).Should().BeNull();

    [Fact]
    public void A_composite_with_no_guard_in_either_half_answers_null() =>
        ExecutionTargetWriteMode.Resolve(Composite(Bare(), Bare())).Should().BeNull();

    [Fact]
    public void A_composite_answers_the_stricter_of_its_two_halves() =>
        ExecutionTargetWriteMode.Resolve(Composite(Guarded(WriteMode.Enabled), Guarded(WriteMode.ReadOnly)))
            .Should().Be(WriteMode.ReadOnly);

    [Fact]
    public void A_composite_answers_the_stricter_half_whichever_side_it_is_on() =>
        ExecutionTargetWriteMode.Resolve(Composite(Guarded(WriteMode.ReadOnly), Guarded(WriteMode.Enabled)))
            .Should().Be(WriteMode.ReadOnly);

    [Fact]
    public void A_composite_guarded_on_only_one_half_answers_that_half() =>
        // A caller that guarded one half still meant "this server does not mutate"; treating the unguarded
        // half as permission would let the weaker side decide.
        ExecutionTargetWriteMode.Resolve(Composite(Bare(), Guarded(WriteMode.PreviewOnly)))
            .Should().Be(WriteMode.PreviewOnly);

    [Fact]
    public void A_composite_with_a_missing_half_answers_the_half_it_has() =>
        ExecutionTargetWriteMode.Resolve(Composite(null, Guarded(WriteMode.ReadOnly)))
            .Should().Be(WriteMode.ReadOnly);

    [Fact]
    public void A_composite_nested_inside_a_guard_answers_the_outer_guard() =>
        ExecutionTargetWriteMode
            .Resolve(new WriteGuardedExecutionTarget(Composite(Bare(), Bare()), WriteMode.ReadOnly))
            .Should().Be(WriteMode.ReadOnly);

    [Fact]
    public void Resolving_nothing_is_rejected()
    {
        var act = () => ExecutionTargetWriteMode.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public void RequireWritesEnabled_refuses_and_names_the_operation_the_server_and_the_mode(WriteMode mode)
    {
        var act = () => ExecutionTargetWriteMode.RequireWritesEnabled(Guarded(mode), "create a backup", "palworld-1");

        act.Should().Throw<WritesDisabledException>()
            .Which.Message.Should().Contain("create a backup")
            .And.Contain("palworld-1")
            .And.Contain(mode.ToString());
    }

    [Fact]
    public void RequireWritesEnabled_appends_what_is_still_available_so_the_tier_is_legible()
    {
        var act = () => ExecutionTargetWriteMode.RequireWritesEnabled(
            Guarded(WriteMode.ReadOnly), "prune backups", "palworld-1", "A dry-run prune remains available.");

        act.Should().Throw<WritesDisabledException>()
            .Which.Message.Should().EndWith("A dry-run prune remains available.");
    }

    [Fact]
    public void RequireWritesEnabled_permits_an_enabled_target()
    {
        var act = () => ExecutionTargetWriteMode.RequireWritesEnabled(
            Guarded(WriteMode.Enabled), "create a backup", "palworld-1");

        act.Should().NotThrow();
    }

    [Fact]
    public void RequireWritesEnabled_permits_an_unguarded_target()
    {
        var act = () => ExecutionTargetWriteMode.RequireWritesEnabled(Bare(), "create a backup", "palworld-1");

        act.Should().NotThrow();
    }
}
