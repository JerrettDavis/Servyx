using System.Reflection;
using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

/// <summary>
/// <see cref="IContainerLifecycle"/> is the second shape <see cref="WriteGuardedExecutionTarget"/> gates —
/// container start/stop/restart/kill on the local Docker path has no <see cref="CommandSpec"/> to classify,
/// so it needs its own door onto the exact same guard. Every assertion here mirrors
/// <c>WriteGuardedExecutionTargetTests</c>'s shape for the command path: refusal before I/O, identical
/// treatment of <see cref="WriteMode.PreviewOnly"/>, and no way for a caller to opt a verb out of the guard.
/// </summary>
public class ContainerLifecycleTests
{
    private static readonly ContainerLifecycleVerb[] AllVerbs =
        Enum.GetValues<ContainerLifecycleVerb>();

    private static (WriteGuardedExecutionTarget Guard, IExecutionTarget Inner) Guarded(WriteMode mode)
    {
        var inner = Substitute.For<IExecutionTarget>();
        return (new WriteGuardedExecutionTarget(inner, mode, "palworld-server"), inner);
    }

    private static (WriteGuardedExecutionTarget Guard, IExecutionTarget Inner) GuardedWithLifecycle(WriteMode mode)
    {
        var inner = Substitute.For<IExecutionTarget, IContainerLifecycle>();
        return (new WriteGuardedExecutionTarget(inner, mode, "palworld-server"), inner);
    }

    private static ContainerLifecycleRequest Request(ContainerLifecycleVerb verb) =>
        new(verb, "palworld-server");

    public static TheoryData<ContainerLifecycleVerb> Verbs()
    {
        var data = new TheoryData<ContainerLifecycleVerb>();
        foreach (var verb in AllVerbs)
        {
            data.Add(verb);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task Container_lifecycle_is_refused_when_writes_are_disabled(ContainerLifecycleVerb verb)
    {
        var (guard, _) = Guarded(WriteMode.ReadOnly);

        var act = async () => await guard.InvokeAsync(Request(verb));

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Container_lifecycle_is_refused_before_the_inner_target_is_touched()
    {
        var (guard, inner) = GuardedWithLifecycle(WriteMode.ReadOnly);
        var lifecycle = (IContainerLifecycle)inner;

        var act = async () => await guard.InvokeAsync(Request(ContainerLifecycleVerb.Stop));

        await act.Should().ThrowAsync<WritesDisabledException>();
        await lifecycle.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);
    }

    [Fact]
    public async Task Container_lifecycle_is_allowed_when_writes_are_enabled()
    {
        var (guard, inner) = GuardedWithLifecycle(WriteMode.Enabled);
        var lifecycle = (IContainerLifecycle)inner;
        var request = Request(ContainerLifecycleVerb.Start);
        var result = new ContainerLifecycleResult(true, "started");
        lifecycle.InvokeAsync(Arg.Any<ContainerLifecycleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

        var actual = await guard.InvokeAsync(request);

        actual.Should().BeSameAs(result);
        await lifecycle.Received(1).InvokeAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_only_refuses_container_lifecycle()
    {
        var (guard, inner) = GuardedWithLifecycle(WriteMode.PreviewOnly);
        var lifecycle = (IContainerLifecycle)inner;

        var act = async () => await guard.InvokeAsync(Request(ContainerLifecycleVerb.Restart));

        await act.Should().ThrowAsync<WritesDisabledException>();
        await lifecycle.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void Lifecycle_request_always_produces_a_mutating_spec(ContainerLifecycleVerb verb)
    {
        var request = Request(verb);

        request.AsGuardedSpec().Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Lifecycle_verb_enum_has_no_read_only_member()
    {
        // A guard against someone later adding a member that lets a caller claim a lifecycle verb doesn't
        // mutate. There is no such verb today, and this test is what makes adding one a deliberate,
        // reviewable change to this assertion rather than a silent hole.
        var names = Enum.GetNames<ContainerLifecycleVerb>();

        names.Should().NotBeEmpty();
        names.Should().NotContain(name => name.Contains("Read", StringComparison.OrdinalIgnoreCase));
        names.Should().Equal("Start", "Stop", "Restart", "Kill");
    }

    [Fact]
    public async Task Unsupported_inner_target_throws_not_supported()
    {
        // The inner target here does NOT implement IContainerLifecycle.
        var (guard, _) = Guarded(WriteMode.Enabled);

        var act = async () => await guard.InvokeAsync(Request(ContainerLifecycleVerb.Start));

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Guard_check_runs_before_the_not_supported_check()
    {
        // Ordering matters: with writes disabled, an inner target that doesn't implement IContainerLifecycle
        // still throws WritesDisabledException, not NotSupportedException. The guard is the first door, not
        // a fallback behind capability detection.
        var (guard, _) = Guarded(WriteMode.ReadOnly);

        var act = async () => await guard.InvokeAsync(Request(ContainerLifecycleVerb.Start));

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public void Null_request_throws()
    {
        var (guard, _) = Guarded(WriteMode.Enabled);

        var act = () => guard.InvokeAsync(null!);

        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Container_ref_guard_rejects_null_empty_or_whitespace(string? containerRef)
    {
        var act = () => new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, containerRef!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Container_ref_guard_also_applies_to_with_expressions()
    {
        var request = Request(ContainerLifecycleVerb.Start);

        var act = () => request with { ContainerRef = "" };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_matches_the_documented_shape()
    {
        // Guards against the record's parameter list silently changing shape underneath the design doc.
        var ctor = typeof(ContainerLifecycleRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 4);

        var parameters = ctor.GetParameters();

        parameters[0].ParameterType.Should().Be(typeof(ContainerLifecycleVerb));
        parameters[1].ParameterType.Should().Be(typeof(string));
        parameters[2].ParameterType.Should().Be(typeof(TimeSpan?));
        parameters[3].ParameterType.Should().Be(typeof(string));
        parameters[2].HasDefaultValue.Should().BeTrue();
        parameters[3].HasDefaultValue.Should().BeTrue();
    }
}
