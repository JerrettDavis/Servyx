using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Lifecycle;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Lifecycle;

/// <summary>
/// Unit tests for <see cref="ServerLifecycleService"/>. Every collaborator (<see cref="IContainerLifecycle"/>,
/// <see cref="IContainerStateProbe"/>, <see cref="IRconChannelResolver"/>/<see cref="IRconSession"/>,
/// <see cref="ILogStream"/>) is an NSubstitute substitute, so these tests exercise the ladder-walking and
/// readiness-composition logic in isolation from any real transport or protocol.
/// </summary>
public class ServerLifecycleServiceTests
{
    private const string ServerId = "palworld-server";

    private static readonly TimeSpan ShortStageTimeout = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(10);

    private static ServerLifecycleService CreateService(
        LifecycleDefinition? definition = null,
        IContainerLifecycle? containerLifecycle = null,
        IContainerStateProbe? stateProbe = null,
        IRconChannelResolver? rconChannels = null,
        ILogStream? logStream = null,
        ILogger<ServerLifecycleService>? logger = null) => new(
        ServerId,
        definition ?? EmptyDefinition(),
        containerLifecycle ?? Substitute.For<IContainerLifecycle>(),
        stateProbe ?? NeverExitsProbe(),
        rconChannels ?? Substitute.For<IRconChannelResolver>(),
        logStream ?? Substitute.For<ILogStream>(),
        logger ?? NullLogger<ServerLifecycleService>.Instance,
        stopPollInterval: ShortPollInterval,
        logPollInterval: ShortPollInterval,
        killConfirmationTimeout: ShortStageTimeout);

    private static LifecycleDefinition EmptyDefinition() =>
        new(Ready: [], Stop: new StopPlan([new StopStage.Kill()]), CrashDetection: []);

    private static IContainerStateProbe NeverExitsProbe()
    {
        var probe = Substitute.For<IContainerStateProbe>();
        probe.GetStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerStateSnapshot(Exited: false)));
        return probe;
    }

    private static IRconSession StubSession(string commandId, RconResponse response)
    {
        var session = Substitute.For<IRconSession>();
        session.InvokeAsync(commandId, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
        return session;
    }

    private static async IAsyncEnumerable<ConsoleLine> Lines(params string[] texts)
    {
        var offset = 0L;
        foreach (var text in texts)
        {
            yield return new ConsoleLine(offset++, text, DateTimeOffset.UtcNow, OutputStream.StdOut);
        }

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------
    // StopAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_walks_the_ladder_in_declared_order()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = StubSession("shutdown", new RconResponse("ok", true));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout),
            new StopStage.Signal("SIGINT", ShortStageTimeout),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: NeverExitsProbe());

        await sut.StopAsync(plan);

        Received.InOrder(() =>
        {
            rconSession.InvokeAsync("shutdown", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
            containerLifecycle.InvokeAsync(
                Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGINT"),
                Arg.Any<CancellationToken>());
            containerLifecycle.InvokeAsync(
                Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGKILL"),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Stop_halts_at_the_first_stage_that_ends_the_container()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = StubSession("shutdown", new RconResponse("ok", true));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        var stateProbe = Substitute.For<IContainerStateProbe>();
        stateProbe.GetStateAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerStateSnapshot(Exited: true)));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout),
            new StopStage.Signal("SIGINT", ShortStageTimeout),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: stateProbe);

        var outcome = await sut.StopAsync(plan);

        outcome.StageThatStopped.Should().Be(plan.Stages[0]);
        await containerLifecycle.DidNotReceive().InvokeAsync(Arg.Any<ContainerLifecycleRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_escalates_when_a_stage_times_out()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = StubSession("shutdown", new RconResponse("ok", true)); // the command itself succeeds...
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        // ...but the container never actually exits, so the stage must escalate once its own timeout elapses.
        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: NeverExitsProbe());

        await sut.StopAsync(plan);

        await rconSession.Received(1).InvokeAsync("shutdown", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        await containerLifecycle.Received(1).InvokeAsync(
            Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGKILL"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_escalates_when_an_rcon_stage_fails_for_an_ordinary_reason()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = Substitute.For<IRconSession>();
        rconSession.InvokeAsync("shutdown", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RconResponse>>(_ => throw new InvalidOperationException("rcon channel unreachable"));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: NeverExitsProbe());

        var outcome = await sut.StopAsync(plan);

        outcome.StageThatStopped.Should().Be(plan.Stages[^1]);
        await containerLifecycle.Received(1).InvokeAsync(
            Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGKILL"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The reason <see cref="StopStage.ContinueOnError"/> defaults to <see langword="true"/> on a control
    /// stage: an unreachable control channel is routine exactly when an operator most needs the stop to
    /// work, and must escalate rather than wedge the shutdown.
    /// </summary>
    [Fact]
    public async Task Stop_continues_past_a_failing_control_stage_that_declares_continueOnError()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = Substitute.For<IRconSession>();
        rconSession.InvokeAsync("shutdown", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RconResponse>>(_ => throw new InvalidOperationException("rcon channel unreachable"));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout) { ContinueOnError = true },
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: NeverExitsProbe());

        var outcome = await sut.StopAsync(plan);

        outcome.StageThatStopped.Should().Be(plan.Stages[^1]);
        await containerLifecycle.Received(1).InvokeAsync(
            Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGKILL"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An absent control channel is treated exactly like one that failed to answer, so a definition whose
    /// stop ladder leads with an RCON command still reaches its terminal kill on a server that has no RCON
    /// configured at all.
    /// </summary>
    [Fact]
    public async Task Stop_continues_past_a_control_stage_whose_channel_does_not_exist()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(null));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: NeverExitsProbe());

        var outcome = await sut.StopAsync(plan);

        outcome.StageThatStopped.Should().Be(plan.Stages[^1]);
        await containerLifecycle.Received(1).InvokeAsync(
            Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGKILL"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other half of the flag. A stage that declares <c>continueOnError: false</c> is saying its failure
    /// is not something to escalate past, so the ordinary failure propagates and no later stage runs.
    /// </summary>
    [Fact]
    public async Task Stop_aborts_at_a_failing_control_stage_that_declares_continueOnError_false()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = Substitute.For<IRconSession>();
        rconSession.InvokeAsync("shutdown", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RconResponse>>(_ => throw new InvalidOperationException("rcon channel unreachable"));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout) { ContinueOnError = false },
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: NeverExitsProbe());

        var act = async () => await sut.StopAsync(plan);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unreachable*");
        await containerLifecycle.DidNotReceive().InvokeAsync(Arg.Any<ContainerLifecycleRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A signal stage defaults to <em>not</em> continuing: unlike a control channel, the container runtime
    /// refusing to deliver a signal is a real fault worth surfacing rather than escalating past. Opting in
    /// restores escalation.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failing_signal_stage_escalates_only_when_it_declares_continueOnError(bool continueOnError)
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        containerLifecycle
            .InvokeAsync(
                Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGINT"),
                Arg.Any<CancellationToken>())
            .Returns<Task<ContainerLifecycleResult>>(_ => throw new InvalidOperationException("the daemon refused the signal"));

        var plan = new StopPlan([
            new StopStage.Signal("SIGINT", ShortStageTimeout) { ContinueOnError = continueOnError },
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, stateProbe: NeverExitsProbe());

        if (continueOnError)
        {
            var outcome = await sut.StopAsync(plan);

            outcome.StageThatStopped.Should().Be(plan.Stages[^1]);
            await containerLifecycle.Received(1).InvokeAsync(
                Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGKILL"),
                Arg.Any<CancellationToken>());
        }
        else
        {
            var act = async () => await sut.StopAsync(plan);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*refused the signal*");
            await containerLifecycle.DidNotReceive().InvokeAsync(
                Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGKILL"),
                Arg.Any<CancellationToken>());
        }
    }

    /// <summary>
    /// Every <see cref="StopStage"/> variant a <see cref="StopPlan"/> can declare, for
    /// <see cref="A_writes_disabled_refusal_aborts_the_ladder_without_escalating"/>. Each variant's action
    /// now runs inside the shared <c>ServerLifecycleService.AttemptAsync</c> helper that applies
    /// <see cref="StopStage.ContinueOnError"/>, so the refusal has a broad <c>catch</c> standing between it
    /// and the caller for every stage type -- which is precisely why this theory matters: the helper's
    /// exclusion for <see cref="WritesDisabledException"/> is the only thing keeping a guard refusal from
    /// being logged and escalated past like any other failure.
    /// </summary>
    public static TheoryData<StopStage> AllStopStageVariants() => new()
    {
        new StopStage.Rcon("shutdown", ShortStageTimeout),
        new StopStage.Signal("SIGINT", ShortStageTimeout),
        new StopStage.Kill(),
        new StopStage.ConsoleWrite("save-all", ShortStageTimeout),
    };

    /// <summary>
    /// THE safety test. A write-guard refusal from ANY stage type must abort the whole ladder immediately
    /// -- never escalate to a later stage, and especially never reach Kill. Escalating past a refusal would
    /// invert "you may not stop this politely" into "so kill it instead", which is exactly what the guard
    /// exists to prevent.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStopStageVariants))]
    public async Task A_writes_disabled_refusal_aborts_the_ladder_without_escalating(StopStage stage)
    {
        var refusal = new WritesDisabledException("writes are disabled for this server");

        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var logStream = Substitute.For<ILogStream>();
        var rconSession = Substitute.For<IRconSession>();
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        var stateProbe = Substitute.For<IContainerStateProbe>();
        stateProbe.GetStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerStateSnapshot(Exited: false)));

        switch (stage)
        {
            case StopStage.Rcon rcon:
                rconSession.InvokeAsync(rcon.CommandId, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<RconResponse>>(_ => throw refusal);
                break;

            case StopStage.Signal signal:
                containerLifecycle
                    .InvokeAsync(
                        Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == signal.SignalName),
                        Arg.Any<CancellationToken>())
                    .Returns<Task<ContainerLifecycleResult>>(_ => throw refusal);
                break;

            case StopStage.Kill:
                containerLifecycle
                    .InvokeAsync(
                        Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Kill && r.Signal == "SIGKILL"),
                        Arg.Any<CancellationToken>())
                    .Returns<Task<ContainerLifecycleResult>>(_ => throw refusal);
                break;

            case StopStage.ConsoleWrite consoleWrite:
                logStream
                    .When(x => x.WriteAsync(ServerId, consoleWrite.Text, Arg.Any<CancellationToken>()))
                    .Do(_ => throw refusal);
                break;

            default:
                throw new NotSupportedException($"Unhandled stop stage type '{stage.GetType().Name}' in test data.");
        }

        // Kill is the ladder's own final, unconditional stage -- when it IS the stage under test there is no
        // further stage to append; for every other stage it is appended as the sentinel that must never run.
        var plan = stage is StopStage.Kill
            ? new StopPlan([stage])
            : new StopPlan([stage, new StopStage.Kill()]);

        var sut = CreateService(
            containerLifecycle: containerLifecycle,
            rconChannels: rconChannels,
            stateProbe: stateProbe,
            logStream: logStream);

        var act = async () => await sut.StopAsync(plan);

        await act.Should().ThrowAsync<WritesDisabledException>();

        // The refusal is thrown before the throwing stage's own exit-polling loop starts, so no stage --
        // including the one under test -- ever reaches WaitForExitOrTimeoutAsync.
        await stateProbe.DidNotReceive().GetStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        switch (stage)
        {
            case StopStage.Rcon:
            case StopStage.ConsoleWrite:
                // Neither stage type touches containerLifecycle itself, so ANY call to it -- including the
                // appended Kill sentinel -- would prove an escalation happened.
                await containerLifecycle.DidNotReceive().InvokeAsync(Arg.Any<ContainerLifecycleRequest>(), Arg.Any<CancellationToken>());
                break;

            case StopStage.Signal:
                // The Signal stage's own (throwing) SIGINT call is expected; only the appended Kill
                // sentinel's SIGKILL call proves an escalation, and must never happen.
                await containerLifecycle.DidNotReceive().InvokeAsync(
                    Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGKILL" && r.Verb == ContainerLifecycleVerb.Kill),
                    Arg.Any<CancellationToken>());
                await containerLifecycle.Received(1).InvokeAsync(
                    Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGINT"),
                    Arg.Any<CancellationToken>());
                break;

            case StopStage.Kill:
                // Kill is the plan's only stage -- the propagated exception plus the state-probe assertion
                // above already prove nothing ran afterward; nothing further to check here.
                break;
        }
    }

    [Fact]
    public async Task Stop_reports_which_stage_ended_the_container()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = Substitute.For<IRconSession>();
        rconSession.InvokeAsync("shutdown", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RconResponse>>(_ => throw new InvalidOperationException("unreachable")); // stage 1: ordinary failure, escalates
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        // The container exits only once the Signal stage's kill call has been issued.
        var signalIssued = false;
        containerLifecycle
            .InvokeAsync(Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGINT"), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                signalIssued = true;
                return Task.FromResult(new ContainerLifecycleResult(true, "signalled"));
            });

        var stateProbe = Substitute.For<IContainerStateProbe>();
        stateProbe.GetStateAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ContainerStateSnapshot(Exited: signalIssued)));

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", ShortStageTimeout),
            new StopStage.Signal("SIGINT", ShortStageTimeout),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: stateProbe);

        var outcome = await sut.StopAsync(plan);

        outcome.StageThatStopped.Should().Be(plan.Stages[1]);
        await containerLifecycle.DidNotReceive().InvokeAsync(
            Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Signal == "SIGKILL"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_is_honored_mid_ladder()
    {
        using var cts = new CancellationTokenSource();

        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var rconSession = StubSession("shutdown", new RconResponse("ok", true));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(rconSession));

        // Cancellation arrives while StopAsync is polling for exit inside the first stage -- not before the
        // ladder even starts.
        var stateProbe = Substitute.For<IContainerStateProbe>();
        stateProbe.GetStateAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult(new ContainerStateSnapshot(Exited: false));
            });

        var plan = new StopPlan([
            new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(30)),
            new StopStage.Kill(),
        ]);

        var sut = CreateService(containerLifecycle: containerLifecycle, rconChannels: rconChannels, stateProbe: stateProbe);

        var act = async () => await sut.StopAsync(plan, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The Kill stage never ran.
        await containerLifecycle.DidNotReceive().InvokeAsync(Arg.Any<ContainerLifecycleRequest>(), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------
    // StartAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_waits_for_the_log_regex_readiness_signal()
    {
        var logStream = Substitute.For<ILogStream>();
        logStream.FollowAsync(ServerId, Arg.Any<ConsoleTailOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Lines("Starting up...", "Running Palworld dedicated server on 0.0.0.0:8211"));

        var definition = new LifecycleDefinition(
            Ready: [new ReadinessProbeDefinition.LogRegex("Running Palworld dedicated server on ", TimeSpan.FromSeconds(2))],
            Stop: new StopPlan([new StopStage.Kill()]),
            CrashDetection: []);

        var sut = CreateService(definition: definition, logStream: logStream);

        var outcome = await sut.StartAsync();

        outcome.Ready.Should().BeTrue();
        outcome.Signal.DetectorId.Should().Be("log-regex");
    }

    [Fact]
    public async Task Start_waits_for_the_control_probe_readiness_signal()
    {
        var session = StubSession("info", new RconResponse("Welcome to Pal Server", true));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(session));

        var definition = new LifecycleDefinition(
            Ready: [new ReadinessProbeDefinition.ControlProbe("rcon", "info", "Welcome to Pal Server", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2))],
            Stop: new StopPlan([new StopStage.Kill()]),
            CrashDetection: []);

        var sut = CreateService(definition: definition, rconChannels: rconChannels);

        var outcome = await sut.StartAsync();

        outcome.Ready.Should().BeTrue();
        outcome.Signal.DetectorId.Should().Be("control-probe");
    }

    [Fact]
    public async Task Start_reports_a_timeout_when_readiness_never_arrives()
    {
        var logStream = Substitute.For<ILogStream>();
        logStream.FollowAsync(ServerId, Arg.Any<ConsoleTailOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Lines("still starting...", "still not there yet..."));

        var definition = new LifecycleDefinition(
            Ready: [new ReadinessProbeDefinition.LogRegex("Running Palworld dedicated server on ", TimeSpan.FromMilliseconds(80))],
            Stop: new StopPlan([new StopStage.Kill()]),
            CrashDetection: []);

        var sut = CreateService(definition: definition, logStream: logStream);

        var outcome = await sut.StartAsync();

        outcome.Ready.Should().BeFalse();
    }

    /// <summary>
    /// Proves readiness checking is not itself gated as a write: the composition path from
    /// <see cref="ServerLifecycleService"/> down through <see cref="RconReadinessProbeChannel"/> to
    /// <see cref="IRconSession.InvokeAsync"/> never inspects or threads through any write-mode flag of its
    /// own -- whether a real session actually permits the call is entirely
    /// <c>WriteGuardedRconSession</c>'s job (an infrastructure concern, tested there), which the
    /// definition's <c>info</c> command is declared <c>readOnly: true</c> for. This substitute stands in
    /// for such a read-only-mode-but-info-is-allowed session.
    /// </summary>
    [Fact]
    public async Task The_control_probe_works_under_read_only_write_mode()
    {
        var readOnlyModeSession = StubSession("info", new RconResponse("Welcome to Pal Server", true));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(readOnlyModeSession));

        var definition = new LifecycleDefinition(
            Ready: [new ReadinessProbeDefinition.ControlProbe("rcon", "info", "Welcome to Pal Server", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2))],
            Stop: new StopPlan([new StopStage.Kill()]),
            CrashDetection: []);

        var sut = CreateService(definition: definition, rconChannels: rconChannels);

        var outcome = await sut.StartAsync();

        outcome.Ready.Should().BeTrue();
        await readOnlyModeSession.Received(1).InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restart_waits_for_readiness()
    {
        var containerLifecycle = Substitute.For<IContainerLifecycle>();
        var session = StubSession("info", new RconResponse("Welcome to Pal Server", true));
        var rconChannels = Substitute.For<IRconChannelResolver>();
        rconChannels.GetSessionAsync(ServerId, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IRconSession?>(session));

        var definition = new LifecycleDefinition(
            Ready: [new ReadinessProbeDefinition.ControlProbe("rcon", "info", "Welcome to Pal Server", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2))],
            Stop: new StopPlan([new StopStage.Kill()]),
            CrashDetection: []);

        var plan = new StopPlan([new StopStage.Kill()]);

        var sut = CreateService(definition: definition, containerLifecycle: containerLifecycle, rconChannels: rconChannels);

        var outcome = await sut.RestartAsync(plan);

        await containerLifecycle.Received(1).InvokeAsync(
            Arg.Is<ContainerLifecycleRequest>(r => r != null && r.Verb == ContainerLifecycleVerb.Restart),
            Arg.Any<CancellationToken>());
        await session.Received(1).InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        outcome.TotalDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
