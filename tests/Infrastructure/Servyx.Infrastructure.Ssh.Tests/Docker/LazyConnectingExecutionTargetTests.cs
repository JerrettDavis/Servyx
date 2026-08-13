using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="LazyConnectingExecutionTarget"/>'s connect memoization — specifically the
/// negative half of it. A successful connect was always cached; a failed one was not, so every call against an
/// unreachable host paid a fresh SSH connect timeout. With a page render fanning out over its hosts, that is
/// what made one bad host read as the whole app hanging.
/// </summary>
public class LazyConnectingExecutionTargetTests
{
    private static readonly CommandSpec Spec = new("echo", ["hi"]);

    [Fact]
    public async Task A_successful_connect_happens_once_and_is_reused()
    {
        var attempts = 0;
        var inner = Substitute.For<IExecutionTarget>();
        await using var target = new LazyConnectingExecutionTarget(_ =>
        {
            attempts++;
            return Task.FromResult(inner);
        });

        await target.ExecuteAsync(Spec);
        await target.ExecuteAsync(Spec);

        attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_connect_is_not_retried_while_the_cooldown_holds()
    {
        var attempts = 0;
        var clock = new FakeTimeProvider();
        await using var target = new LazyConnectingExecutionTarget(
            _ =>
            {
                attempts++;
                return Task.FromException<IExecutionTarget>(
                    new InvalidOperationException("auth failed as user 'ssh paladmin'"));
            },
            failureCooldown: TimeSpan.FromSeconds(45),
            timeProvider: clock);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.ExecuteAsync(Spec));
        }

        // The point of the whole change: five calls, one connect. Before, each call paid its own 10s timeout.
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task The_remembered_failure_is_replayed_verbatim_so_the_caller_still_learns_why()
    {
        var clock = new FakeTimeProvider();
        await using var target = new LazyConnectingExecutionTarget(
            _ => Task.FromException<IExecutionTarget>(
                new InvalidOperationException("auth failed as user 'ssh paladmin'")),
            failureCooldown: TimeSpan.FromSeconds(45),
            timeProvider: clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ExecuteAsync(Spec));
        var replayed = await Assert.ThrowsAsync<InvalidOperationException>(() => target.ExecuteAsync(Spec));

        // A cached failure that degraded to a generic message would cost the operator the actual diagnosis.
        replayed.Message.Should().Be("auth failed as user 'ssh paladmin'");
    }

    [Fact]
    public async Task A_host_that_comes_back_is_reconnected_once_the_cooldown_expires()
    {
        var attempts = 0;
        var clock = new FakeTimeProvider();
        var inner = Substitute.For<IExecutionTarget>();
        await using var target = new LazyConnectingExecutionTarget(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<IExecutionTarget>(new InvalidOperationException("host down"))
                    : Task.FromResult(inner);
            },
            failureCooldown: TimeSpan.FromSeconds(45),
            timeProvider: clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ExecuteAsync(Spec));
        clock.Advance(TimeSpan.FromSeconds(46));
        await target.ExecuteAsync(Spec);

        // The cooldown is a delay, not a tombstone: a host repaired out of band recovers without a restart.
        attempts.Should().Be(2);
        await inner.Received(1).ExecuteAsync(Spec, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A caller abandoning its request says nothing about the host, so it must not start a cooldown that would
    /// then refuse the next, healthy caller.
    /// </summary>
    [Fact]
    public async Task A_cancelled_connect_does_not_start_a_cooldown()
    {
        var attempts = 0;
        var clock = new FakeTimeProvider();
        var inner = Substitute.For<IExecutionTarget>();
        using var abandoned = new CancellationTokenSource();
        await using var target = new LazyConnectingExecutionTarget(
            ct =>
            {
                attempts++;

                // Cancelled mid-connect, the way a caller navigating away from a page cancels one in flight.
                abandoned.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(inner);
            },
            failureCooldown: TimeSpan.FromSeconds(45),
            timeProvider: clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.ExecuteAsync(Spec, abandoned.Token));

        await target.ExecuteAsync(Spec);

        attempts.Should().Be(2);
    }

    [Fact]
    public void A_negative_cooldown_is_refused_at_construction()
    {
        var act = () => new LazyConnectingExecutionTarget(
            _ => Task.FromResult(Substitute.For<IExecutionTarget>()),
            failureCooldown: TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>A manual-advance clock, matching the hand-rolled fakes used elsewhere in this solution.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
