using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;

namespace Servyx.Composition;

/// <summary>
/// Composes the ordered <see cref="RconReachabilityChain"/> the <c>chainFactory</c> delegate
/// <see cref="ServyxRconChannels"/> is given at composition time builds per channel.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The definition's declared order, exactly.</strong> <c>direct-tcp</c> first — it only succeeds
/// when the port is published, which the adopted <c>thijsvanloef/palworld-server-docker</c> container does
/// not do for RCON 25575 — then <c>docker-exec-tool</c>, which is what actually reaches it by running the
/// image's bundled <c>rcon-cli</c> via <c>docker exec</c> over the ssh+docker host's
/// <see cref="IExecutionTarget"/>, then <c>docker-exec-network</c>, still an
/// <see cref="UnavailableRconReachability"/> stand-in (see its remarks).
/// </para>
/// <para>
/// <strong>No exec strategy without an exec channel.</strong> When no ssh+docker host is configured — see
/// <see cref="Servyx.Infrastructure.Ssh.Docker.SshDockerWiringOptions.Any"/> — there is no
/// <see cref="IExecutionTarget"/> to run <c>docker exec</c> through. <see cref="Build"/> then omits
/// <c>docker-exec-tool</c> entirely rather than registering a strategy that could never succeed, so the
/// composed chain degrades to <c>[direct-tcp, docker-exec-network]</c> and startup still succeeds.
/// </para>
/// <para>
/// Pulled out of <c>Program.cs</c> as its own type so it can be exercised directly by a test — asserting the
/// declared order, the exec-strategy-absent degradation, and which strategy a chain actually acquires from —
/// without booting the whole host.
/// </para>
/// </remarks>
public static class RconReachabilityChainFactory
{
    /// <summary>
    /// The definition's <c>control.channels[rcon].reachability[docker-exec-tool].argv</c> — see
    /// <c>definitions/palworld-docker.yaml</c>. <c>GameDefinitionYamlParser</c> does parse this value into
    /// <c>ReachabilityStrategy.DockerExecTool.Argv</c> now, but nothing reads it from there yet; this
    /// constant is still hand-mirrored from the definition's literally declared argv rather than sourced from
    /// the parsed model, so a change to either side without the other is a drift this constant does not
    /// protect against.
    /// </summary>
    public static readonly IReadOnlyList<string> DockerExecArgv = ["rcon-cli", "{command}"];

    /// <summary>Builds the chain for one channel, in the definition's declared strategy order.</summary>
    /// <param name="channel">The channel a session is being acquired for.</param>
    /// <param name="client">The protocol client <c>direct-tcp</c>'s session factory uses.</param>
    /// <param name="catalog">The definition's command catalogue, shared by every strategy in the chain.</param>
    /// <param name="secrets">The secret store <c>direct-tcp</c>'s session factory resolves the credential through.</param>
    /// <param name="containerName">
    /// The ssh+docker host's container name (typically <c>SshDockerWiringOptions.Hosts[0].ContainerName</c>),
    /// or <see langword="null"/> when no remote host is configured — in which case <c>docker-exec-tool</c> is
    /// omitted entirely.
    /// </param>
    /// <param name="executionTarget">
    /// The exec channel <c>docker-exec-tool</c> runs over. Required (non-null) whenever
    /// <paramref name="containerName"/> is non-null; the two are supplied together or not at all.
    /// </param>
    /// <param name="players">
    /// Which command an acquired session's <c>GetPlayersAsync</c> invokes and how to read its reply, resolved
    /// from the definition's <c>control.players</c> block. Defaults to <see cref="PlayerListPlan.None"/> when
    /// omitted.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Exactly one of <paramref name="containerName"/> and <paramref name="executionTarget"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static RconReachabilityChain Build(
        RconChannel channel,
        IRconClient client,
        RconCommandCatalog catalog,
        ISecretStore secrets,
        string? containerName,
        IExecutionTarget? executionTarget,
        PlayerListPlan? players = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(secrets);

        if (containerName is null != executionTarget is null)
        {
            throw new ArgumentException(
                "A container name and an IExecutionTarget for docker-exec-tool must be supplied together or not "
                + "at all — a container name with nothing to run 'docker exec' through (or vice versa) is not a "
                + "usable strategy.",
                nameof(executionTarget));
        }

        var strategies = new List<IRconReachability>
        {
            new DirectTcpRconReachability(endpoint =>
                new RconSession(client, endpoint, catalog, secrets, channel.PasswordUrn, players: players)),
        };

        if (containerName is not null)
        {
            strategies.Add(new DockerExecToolRconReachability(
                executionTarget!, containerName, DockerExecArgv, catalog, audit: null, players: players));
        }

        strategies.Add(UnavailableRconReachability.DockerExecNetwork);

        return new RconReachabilityChain(strategies);
    }
}
