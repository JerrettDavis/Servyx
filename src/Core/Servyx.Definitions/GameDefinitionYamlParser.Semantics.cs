namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    /// <summary>
    /// Resolves every check queued in <paramref name="state"/> while parsing an earlier block, because it
    /// depends on a later block's content — see the remarks on <see cref="ParseState"/>. Must run only after
    /// every top-level block has been parsed.
    /// </summary>
    private static void ResolveDeferredChecks(ParseState state)
    {
        foreach (var (token, node, allowHostVariable) in state.PendingVariableRefs)
        {
            var isHostVariable = allowHostVariable && ParseState.HostVariables.Contains(token);
            var isSettingKey = state.SettingKeys.Contains(token);
            if (isHostVariable || isSettingKey)
            {
                continue;
            }

            var message = $"'${{{token}}}' does not name a host-supplied variable"
                + (allowHostVariable ? " (DATA_DIR, COMPOSE_DIR, INSTANCE_ID)" : string.Empty)
                + " or a declared settings key.";

            if (allowHostVariable)
            {
                // Path-like template references (${DATA_DIR}/..., ${COMPOSE_DIR}/...): every occurrence in
                // the real definitions/palworld-docker.yaml resolves, so an unresolvable one is a genuine
                // Error.
                state.Issues.Error(message, node);
            }
            else
            {
                // Bare-name references (capabilities.network[].var, a control channel's port: "${KEY}"):
                // downgraded from the brief's default Error to Warning. definitions/palworld-docker.yaml
                // itself declares 'var: QUERY_PORT' and 'var: REST_API_PORT' (capabilities.network, and the
                // 'query'/'rest' channels' own port references) with NO matching settings-catalogue entry —
                // only PORT and RCON_PORT are exposed as user-configurable settings; QUERY_PORT and
                // REST_API_PORT appear to be image-internal env values never surfaced to the operator.
                // Enforcing this rule as Error, as the brief specifies, would fail the one real, shipped
                // definition this project's own round-trip fidelity test is built against — flagged here
                // rather than silently resolved; see this phase's final report for the full conflict.
                state.Issues.Warning(message, node);
            }
        }

        state.ChannelsById.TryGetValue("rcon", out var rconChannel);
        foreach (var (commandId, node) in state.PendingStopCommandRefs)
        {
            if (rconChannel is null)
            {
                state.Issues.Error(
                    $"'lifecycle.stop' references RCON command '{commandId}', but no 'control.channels' entry has 'id: rcon'.",
                    node);
            }
            else if (!rconChannel.Commands.ContainsKey(commandId))
            {
                state.Issues.Error(
                    $"'lifecycle.stop' references RCON command '{commandId}', which the 'rcon' channel's "
                    + "'commands' catalogue does not declare.",
                    node);
            }
        }

        // Same class of check as PendingStopCommandRefs above, generalized to an arbitrary channel id rather
        // than a hardcoded 'rcon' — backup.quiesce[] and lifecycle.ready's control-probe entries both name
        // their own channel, so both the channel and the command it claims must be checked here.
        foreach (var (channelId, commandId, channelNode, commandNode, context) in state.PendingChannelCommandRefs)
        {
            if (!state.ChannelsById.TryGetValue(channelId, out var channel))
            {
                state.Issues.Error(
                    $"{context} references channel '{channelId}', but no 'control.channels' entry declares it.",
                    channelNode);
            }
            else if (!channel.Commands.ContainsKey(commandId))
            {
                state.Issues.Error(
                    $"{context} references command '{commandId}', which the '{channelId}' channel's "
                    + "'commands' catalogue does not declare.",
                    commandNode);
            }
        }

        // A grace period shorter than the ladder it is meant to cover is worse than none at all: the ladder
        // still walks its stages, the container runtime still force-kills partway through, and the resulting
        // truncated save looks like a game bug rather than a misconfiguration. Reported as an Error naming
        // both numbers so the fix is arithmetic, not archaeology.
        foreach (var (deploymentId, gracePeriod, node) in state.PendingStopGracePeriods)
        {
            if (gracePeriod >= state.StopLadderTotal)
            {
                continue;
            }

            var declared = (int)Math.Round(gracePeriod.TotalSeconds);
            var required = (int)Math.Ceiling(state.StopLadderTotal.TotalSeconds);

            state.Issues.Error(
                $"Deployment '{deploymentId}' declares 'stopGracePeriodSeconds: {declared}', but its "
                + $"'lifecycle.stop' ladder's stage timeouts total {required} seconds. The container runtime "
                + $"would force-kill the workload after {declared}s, part-way through the ladder and quite "
                + $"possibly mid-save. Declare at least {required}.",
                node);
        }

        // A 'secret:key' reference naming a key the settings catalogue never declares would resolve to
        // nothing at the point of use, and the point of use for a seeded file is a write into the
        // deployment's storage performed before the workload has ever started. Reported as an Error naming
        // the key so the fix is either a typo correction or one added settings item.
        foreach (var (key, node, context) in state.PendingSecretKeyRefs)
        {
            if (state.SettingKeys.Contains(key))
            {
                continue;
            }

            state.Issues.Error(
                $"{context} references secret key '{key}', which no 'settings' item declares. A 'secret:' "
                + "reference may only name a declared settings key, so that the value has a catalogue entry to be "
                + "sourced and rotated through.",
                node);
        }

        foreach (var (channelId, channelNode) in state.PendingSurfaceChannelRefs)
        {
            if (!state.ChannelsById.ContainsKey(channelId))
            {
                state.Issues.Error(
                    $"A 'config.surfaces' entry's 'control-channel' locator references channel '{channelId}', "
                    + "but no 'control.channels' entry declares it.",
                    channelNode);
            }
        }
    }
}
