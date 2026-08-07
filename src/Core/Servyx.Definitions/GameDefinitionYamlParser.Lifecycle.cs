using Servyx.Domain.Lifecycle;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> LifecycleKeys = new HashSet<string>(StringComparer.Ordinal) { "ready", "stop", "crashDetection", "healthSignal" };
    private static readonly IReadOnlySet<string> LogRegexProbeKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "pattern", "timeout" };
    private static readonly IReadOnlySet<string> ControlProbeKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "channel", "command", "expect", "interval", "timeout" };
    private static readonly IReadOnlySet<string> ControlStopStageKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "channel", "command", "args", "timeout", "continueOnError" };
    private static readonly IReadOnlySet<string> SignalStopStageKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "signal", "timeout", "continueOnError" };
    private static readonly IReadOnlySet<string> KillStopStageKeys = new HashSet<string>(StringComparer.Ordinal) { "kind" };
    private static readonly IReadOnlySet<string> CrashDetectionKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "pattern", "action" };
    private static readonly IReadOnlySet<string> HealthSignalKeys = new HashSet<string>(StringComparer.Ordinal) { "trust", "explanation" };

    private static LifecycleDefinition? ParseLifecycle(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        var map = RequireMapping(root, "lifecycle", issues, "The definition");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, LifecycleKeys, issues, "'lifecycle'");

        var ready = new List<ReadinessProbeDefinition>();
        foreach (var node in OptionalSequence(map, "ready", issues, "'lifecycle'"))
        {
            var probe = ParseReadinessProbe(node, issues, state);
            if (probe is not null)
            {
                ready.Add(probe);
            }
        }

        var stopSequence = RequireSequence(map, "stop", issues, "'lifecycle'");
        var stages = new List<StopStage>();
        if (stopSequence is not null)
        {
            foreach (var node in stopSequence.Children)
            {
                var stage = ParseStopStage(node, issues, state);
                if (stage is not null)
                {
                    stages.Add(stage);
                }
            }

            if (stopSequence.Children.Count > 0 && stages.Count == stopSequence.Children.Count && stages[^1] is not StopStage.Kill)
            {
                issues.Error(
                    "'lifecycle.stop' ladder's final stage must be 'kind: kill' — a ladder that can end without "
                    + "a forced kill would leave a server that can never be brought down.",
                    stopSequence.Children[^1]);
            }

            state.StopLadderTotal = stages.Aggregate(TimeSpan.Zero, (total, stage) => total + StageTimeout(stage));
        }

        var crashDetection = new List<CrashDetectionRule>();
        foreach (var node in OptionalSequence(map, "crashDetection", issues, "'lifecycle'"))
        {
            var rule = ParseCrashDetectionRule(node, issues);
            if (rule is not null)
            {
                crashDetection.Add(rule);
            }
        }

        var healthSignal = ParseHealthSignal(map, issues);

        return new LifecycleDefinition(ready, new StopPlan(stages), crashDetection, healthSignal);
    }

    /// <summary>
    /// How much wall-clock time a stage is allowed to consume before the ladder escalates past it.
    /// <see cref="StopStage.Kill"/> declares none — it is terminal, so nothing waits on it — and therefore
    /// contributes nothing to the ladder's budget.
    /// </summary>
    private static TimeSpan StageTimeout(StopStage stage) => stage switch
    {
        StopStage.Rcon rcon => rcon.Timeout,
        StopStage.ConsoleWrite consoleWrite => consoleWrite.Timeout,
        StopStage.Signal signal => signal.Timeout,
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// Parses the optional <c>lifecycle.healthSignal</c> block. Unlike <c>ready</c>/<c>stop</c>/
    /// <c>crashDetection</c>, a definition need not declare this at all — a workload whose own health
    /// signal is trustworthy (or one that simply has not documented otherwise) has nothing to say here, and
    /// <see langword="null"/> is a first-class, non-degraded outcome, not a parse failure.
    /// </summary>
    private static HealthSignalDefinition? ParseHealthSignal(YamlMappingNode lifecycleMap, ParseIssues issues)
    {
        if (!lifecycleMap.TryGet("healthSignal", out var node))
        {
            return null;
        }

        var map = AsMapping(node, issues, "'lifecycle's 'healthSignal'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, HealthSignalKeys, issues, "A 'lifecycle.healthSignal' entry");

        var trust = ParseHealthSignalTrust(map, issues);
        var explanation = OptionalString(map, "explanation", issues, "A 'lifecycle.healthSignal' entry");

        return trust is null ? null : new HealthSignalDefinition(trust.Value, explanation);
    }

    private static HealthSignalTrust? ParseHealthSignalTrust(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "trust", issues, "A 'lifecycle.healthSignal' entry");
        return raw switch
        {
            "trust" => HealthSignalTrust.Trust,
            "ignore" => HealthSignalTrust.Ignore,
            null => null,
            _ => Fail(map, issues, raw),
        };

        static HealthSignalTrust? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("trust", out var node);
            issues.Error($"A 'lifecycle.healthSignal' entry declares 'trust: {raw}'; only 'trust' and 'ignore' are recognized.", node);
            return null;
        }
    }

    private static ReadinessProbeDefinition? ParseReadinessProbe(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'lifecycle.ready'");
        if (map is null)
        {
            return null;
        }

        var kind = RequireString(map, "kind", issues, "A 'lifecycle.ready' entry");
        switch (kind)
        {
            case "log-regex":
                RejectUnknownKeys(map, LogRegexProbeKeys, issues, "A 'lifecycle.ready' log-regex entry");
                var pattern = RequireString(map, "pattern", issues, "A 'lifecycle.ready' log-regex entry");
                if (pattern is not null)
                {
                    map.TryGet("pattern", out var patternNode);
                    ValidateSafeRegex(pattern, patternNode, issues, "A 'lifecycle.ready' log-regex entry's 'pattern'");
                }

                var timeout = ParseDuration(map, "timeout", issues, "A 'lifecycle.ready' log-regex entry");
                return pattern is not null && timeout is not null
                    ? new ReadinessProbeDefinition.LogRegex(pattern, timeout.Value)
                    : null;

            case "control-probe":
                RejectUnknownKeys(map, ControlProbeKeys, issues, "A 'lifecycle.ready' control-probe entry");
                var channel = RequireString(map, "channel", issues, "A 'lifecycle.ready' control-probe entry");
                var command = RequireString(map, "command", issues, "A 'lifecycle.ready' control-probe entry");
                var expect = RequireString(map, "expect", issues, "A 'lifecycle.ready' control-probe entry");
                var interval = ParseDuration(map, "interval", issues, "A 'lifecycle.ready' control-probe entry");
                var probeTimeout = ParseDuration(map, "timeout", issues, "A 'lifecycle.ready' control-probe entry");

                if (channel is not null && command is not null)
                {
                    map.TryGet("channel", out var channelNode);
                    map.TryGet("command", out var commandNode);
                    state.PendingChannelCommandRefs.Add((channel, command, channelNode, commandNode, "A 'lifecycle.ready' control-probe entry"));
                }

                return channel is not null && command is not null && expect is not null && interval is not null && probeTimeout is not null
                    ? new ReadinessProbeDefinition.ControlProbe(channel, command, expect, interval.Value, probeTimeout.Value)
                    : null;

            case null:
                return null;

            default:
                map.TryGet("kind", out var kindNode);
                issues.Error($"A 'lifecycle.ready' entry declares 'kind: {kind}'; only 'log-regex' and 'control-probe' are recognized.", kindNode);
                return null;
        }
    }

    private static StopStage? ParseStopStage(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'lifecycle.stop'");
        if (map is null)
        {
            return null;
        }

        var kind = RequireString(map, "kind", issues, "A 'lifecycle.stop' entry");
        switch (kind)
        {
            case "control":
                RejectUnknownKeys(map, ControlStopStageKeys, issues, "A 'lifecycle.stop' control entry");
                var channel = RequireString(map, "channel", issues, "A 'lifecycle.stop' control entry");
                if (channel is not null && channel != "rcon")
                {
                    map.TryGet("channel", out var channelNode);
                    issues.Error(
                        $"A 'lifecycle.stop' control entry references channel '{channel}'; only 'rcon' is "
                        + "currently supported for control stop stages.",
                        channelNode);
                }

                var commandId = RequireString(map, "command", issues, "A 'lifecycle.stop' control entry");
                var stopTimeout = ParseDuration(map, "timeout", issues, "A 'lifecycle.stop' control entry");
                var args = ParseStopStageArgs(map, issues);

                if (commandId is not null && channel == "rcon")
                {
                    map.TryGet("command", out var commandNode);
                    state.PendingStopCommandRefs.Add((commandId, commandNode));
                }

                // Defaults to true: an unreachable control channel is the single most common reason a stop
                // stage fails, and it must escalate rather than wedge the stop — see StopStage.ContinueOnError.
                var controlContinues = OptionalBool(map, "continueOnError", issues, "A 'lifecycle.stop' control entry") ?? true;

                return commandId is not null && stopTimeout is not null
                    ? new StopStage.Rcon(commandId, stopTimeout.Value, args) { ContinueOnError = controlContinues }
                    : null;

            case "signal":
                RejectUnknownKeys(map, SignalStopStageKeys, issues, "A 'lifecycle.stop' signal entry");
                var signalName = RequireString(map, "signal", issues, "A 'lifecycle.stop' signal entry");
                var signalTimeout = ParseDuration(map, "timeout", issues, "A 'lifecycle.stop' signal entry");
                var signalContinues = OptionalBool(map, "continueOnError", issues, "A 'lifecycle.stop' signal entry") ?? false;
                return signalName is not null && signalTimeout is not null
                    ? new StopStage.Signal(signalName, signalTimeout.Value) { ContinueOnError = signalContinues }
                    : null;

            case "kill":
                RejectUnknownKeys(map, KillStopStageKeys, issues, "A 'lifecycle.stop' kill entry");
                return new StopStage.Kill();

            case null:
                return null;

            default:
                map.TryGet("kind", out var kindNode);
                issues.Error(
                    $"A 'lifecycle.stop' entry declares 'kind: {kind}'; only 'control', 'signal', and 'kill' are recognized.",
                    kindNode);
                return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseStopStageArgs(YamlMappingNode map, ParseIssues issues)
    {
        if (!map.TryGet("args", out var node))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var argsMap = AsMapping(node, issues, "A 'lifecycle.stop' control entry's 'args'");
        if (argsMap is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in argsMap.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } key })
            {
                issues.Error("A 'lifecycle.stop' control entry's 'args' declares a non-text key.", pair.Key);
                continue;
            }

            var value = pair.Value is YamlScalarNode { Value: { } v } ? v : pair.Value.ToString() ?? string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static CrashDetectionRule? ParseCrashDetectionRule(YamlNode node, ParseIssues issues)
    {
        var map = AsMapping(node, issues, "An entry of 'lifecycle.crashDetection'");
        if (map is null)
        {
            return null;
        }

        var kind = RequireString(map, "kind", issues, "A 'lifecycle.crashDetection' entry");
        if (kind is not null && kind != "log-regex")
        {
            map.TryGet("kind", out var kindNode);
            issues.Error($"A 'lifecycle.crashDetection' entry declares 'kind: {kind}'; only 'log-regex' is recognized.", kindNode);
            return null;
        }

        RejectUnknownKeys(map, CrashDetectionKeys, issues, "A 'lifecycle.crashDetection' entry");
        var pattern = RequireString(map, "pattern", issues, "A 'lifecycle.crashDetection' entry");
        if (pattern is not null)
        {
            map.TryGet("pattern", out var patternNode);
            ValidateSafeRegex(pattern, patternNode, issues, "A 'lifecycle.crashDetection' entry's 'pattern'");
        }

        var action = RequireString(map, "action", issues, "A 'lifecycle.crashDetection' entry");

        return pattern is not null && action is not null ? new CrashDetectionRule(pattern, action) : null;
    }
}
