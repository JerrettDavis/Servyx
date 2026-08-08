using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// The <c>docker-exec-tool</c> reachability strategy: runs the image's bundled <c>rcon-cli</c> inside the
/// game container via <c>docker exec</c>, over whatever <see cref="IExecutionTarget"/> the composition root
/// wired up for this host (typically an SSH-backed transport to the Docker host). This is the strategy that
/// actually reaches RCON on the adopted <c>thijsvanloef/palworld-server-docker</c> container, where port
/// 25575 is declared <c>published: false</c> and <see cref="DirectTcpRconReachability"/> can never succeed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Intent is never inferred from argv text.</strong> <c>docker exec</c> is the same API call whether
/// the rendered command is <c>rcon-cli Info</c> or <c>rcon-cli Shutdown</c>, so every <see cref="CommandSpec"/>
/// this type builds carries a <see cref="CommandIntent"/> taken directly from the definition's
/// <see cref="RconCommand.ReadOnly"/> flag via the supplied <see cref="RconCommandCatalog"/> — the same
/// source <see cref="WriteGuardedRconSession"/> and <see cref="RconSession"/> use. This mirrors
/// <c>Servyx.Infrastructure.Ssh.Docker.DockerCli</c>'s split between <c>ExecReadOnly</c> and <c>Exec</c>,
/// deliberately re-implemented in this assembly rather than referenced: <c>Servyx.Infrastructure.Rcon</c>
/// does not depend on <c>Servyx.Infrastructure.Ssh</c>, because RCON reachability has no business coupling to
/// an SSH-specific transport package when the transport-agnostic <see cref="IExecutionTarget"/> abstraction
/// in <c>Servyx.Domain</c> — which this project already references — is all it needs.
/// </para>
/// <para>
/// <strong>Probing is side-effect free.</strong> <see cref="IsAvailableAsync"/> runs <c>which &lt;tool&gt;</c>
/// inside the container as a <see cref="CommandIntent.ReadOnly"/> exec — it never runs the RCON tool itself.
/// A missing binary, a non-zero exit, or the target throwing all answer <see langword="false"/> rather than
/// propagating, so <see cref="RconReachabilityChain"/> can fall through to the next strategy instead of
/// aborting the whole chain over one strategy's probe failure.
/// </para>
/// <para>
/// <strong>Scope at this milestone.</strong> The read-only command path (<c>info</c>, <c>players</c>) is
/// fully wired end to end. A mutating command (<c>save</c>, <c>shutdown</c>, ...) is still constructed and
/// classified correctly — it just has nowhere to go: the default <see cref="WriteGuardedRconSession"/>
/// refuses it, by design, until a later milestone grants writes.
/// </para>
/// </remarks>
public sealed class DockerExecToolRconReachability : IRconReachability
{
    /// <summary>The strategy id this type implements.</summary>
    public const string Id = "docker-exec-tool";

    private const string CommandPlaceholder = "{command}";

    private readonly IExecutionTarget _target;
    private readonly string _containerName;
    private readonly IReadOnlyList<string> _argvTemplate;
    private readonly RconCommandCatalog _catalog;
    private readonly IRconAuditSink? _audit;
    private readonly PlayerListPlan _players;
    private readonly string _probeTool;
    private string? _lastUnavailableReason;

    /// <summary>Creates the strategy.</summary>
    /// <param name="target">
    /// The exec channel commands run over — typically the same <see cref="IExecutionTarget"/> the
    /// composition root uses for Docker lifecycle operations against this host.
    /// </param>
    /// <param name="containerName">The running container's name, passed to <c>docker exec</c> verbatim.</param>
    /// <param name="argvTemplate">
    /// The definition's declared argv, e.g. <c>["rcon-cli", "{command}"]</c>. At least one element must
    /// contain the literal placeholder <c>{command}</c>; each occurrence is substituted with the rendered
    /// RCON command text in place, so the element remains exactly one argv array slot — never split on
    /// whitespace and never shell-joined — however many spaces or quotes the rendered command contains.
    /// </param>
    /// <param name="catalog">
    /// The definition's command catalogue. Every command this strategy's session issues is classified by
    /// this catalogue's <see cref="RconCommand.ReadOnly"/> flag, never by inspecting the rendered text.
    /// </param>
    /// <param name="audit">
    /// The audit sink acquired sessions' <c>SendRawAsync</c> writes to, mirroring
    /// <see cref="RconSession"/>'s constructor. When <see langword="null"/> (the default — nothing in this
    /// composition root wires one up yet), the raw escape hatch on every session this strategy acquires is
    /// unavailable and refuses outright, exactly as an unaudited <see cref="RconSession"/> does: see
    /// <see cref="DockerExecToolRconSession.SendRawAsync"/>.
    /// </param>
    /// <param name="players">
    /// Which command an acquired session's <c>GetPlayersAsync</c> invokes and how to read its reply, resolved
    /// from the definition's <c>control.players</c> block. Defaults to <see cref="PlayerListPlan.None"/> when
    /// omitted.
    /// </param>
    public DockerExecToolRconReachability(
        IExecutionTarget target,
        string containerName,
        IReadOnlyList<string> argvTemplate,
        RconCommandCatalog catalog,
        IRconAuditSink? audit = null,
        PlayerListPlan? players = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(argvTemplate);
        ArgumentNullException.ThrowIfNull(catalog);

        if (argvTemplate.Count == 0)
        {
            throw new ArgumentException("An argv template must declare at least one element.", nameof(argvTemplate));
        }

        if (!argvTemplate.Any(element => element.Contains(CommandPlaceholder, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The argv template [{string.Join(", ", argvTemplate)}] contains no '{CommandPlaceholder}' "
                + "placeholder, so there is nowhere to substitute the rendered RCON command.",
                nameof(argvTemplate));
        }

        _target = target;
        _containerName = containerName;
        _argvTemplate = argvTemplate;
        _catalog = catalog;
        _audit = audit;
        _players = players ?? PlayerListPlan.None;
        _probeTool = argvTemplate[0];
    }

    /// <inheritdoc />
    public string StrategyId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs <c>which &lt;tool&gt;</c> — the tool named by <c>argvTemplate[0]</c> — inside the container as a
    /// <see cref="CommandIntent.ReadOnly"/> exec. Exit code zero means available. Never throws: any failure,
    /// including the target itself throwing, is reported as <see langword="false"/> so the chain can move on
    /// to the next strategy. A cancellation requested by the caller's own token is the one exception that
    /// still propagates, since that is not a probe failure.
    /// </para>
    /// <para>
    /// On failure, records a short reason in <see cref="LastUnavailableReason"/>: the probe's exit code and a
    /// truncated excerpt of its stderr when the process ran, or the thrown exception's type name when it
    /// didn't. The probe's own argv is <c>["which", &lt;tool&gt;]</c> — it never carries the RCON password —
    /// so nothing this method can observe about it is secret; only its exit code and stderr are folded in,
    /// truncated by <see cref="RconDiagnosticText"/>, never the exception's own message text, which could in
    /// principle come from an unrelated part of the exec channel.
    /// </para>
    /// </remarks>
    public async Task<bool> IsAvailableAsync(RconEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        try
        {
            var probe = BuildExecSpec(["which", _probeTool], CommandIntent.ReadOnly);
            var result = await _target.ExecuteAsync(probe, ct).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                _lastUnavailableReason = null;
                return true;
            }

            _lastUnavailableReason = DescribeProbeFailure(result);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes OperationCanceledException raised for a reason other than the caller's own token (e.g.
            // an internal timeout), plus any transport failure. All of it means "unavailable", not "abort".
            // Only the exception's type is recorded, never its message: unlike the probe's own exit code and
            // stderr, an exception thrown by the exec channel could in principle carry arbitrary text this
            // strategy did not construct.
            _lastUnavailableReason = $"probe threw {ex.GetType().Name}";
            return false;
        }
    }

    /// <inheritdoc />
    public string? LastUnavailableReason => _lastUnavailableReason;

    /// <inheritdoc />
    public Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        IRconSession session = new DockerExecToolRconSession(_target, _containerName, endpoint, _argvTemplate, _catalog, _audit, _players);
        return Task.FromResult(session);
    }

    private CommandSpec BuildExecSpec(IReadOnlyList<string> argv, CommandIntent intent) =>
        new("docker", ["exec", _containerName, .. argv], Intent: intent);

    /// <summary>
    /// Describes why the availability probe reported the container unavailable: its exit code, plus a
    /// truncated excerpt of stderr when the probe wrote one. <paramref name="result"/> comes from running
    /// <c>which &lt;tool&gt;</c>, an argv that never carries the RCON password, so nothing captured here can
    /// contain the credential.
    /// </summary>
    private string DescribeProbeFailure(CommandResult result)
    {
        var reason = $"probe 'which {_probeTool}' exited {result.ExitCode}";

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            reason += $": {RconDiagnosticText.Truncate(result.StandardError)}";
        }

        return reason;
    }
}

/// <summary>
/// An <see cref="IRconSession"/> that runs each command by rendering it into the definition's declared
/// <c>docker-exec-tool</c> argv template and executing that via <c>docker exec</c> on an
/// <see cref="IExecutionTarget"/>.
/// </summary>
/// <remarks>
/// Public for the same reason <see cref="RconSession"/> is: acquired only through
/// <see cref="DockerExecToolRconReachability.AcquireAsync"/> in normal use, but visible enough that a test can
/// assert which concrete session a <see cref="RconReachabilityChain"/> actually acquired.
/// </remarks>
public sealed class DockerExecToolRconSession : IRconSession
{
    private readonly IExecutionTarget _target;
    private readonly string _containerName;
    private readonly RconEndpoint _endpoint;
    private readonly IReadOnlyList<string> _argvTemplate;
    private readonly RconCommandCatalog _catalog;
    private readonly IRconAuditSink? _audit;
    private readonly PlayerListPlan _players;

    internal DockerExecToolRconSession(
        IExecutionTarget target,
        string containerName,
        RconEndpoint endpoint,
        IReadOnlyList<string> argvTemplate,
        RconCommandCatalog catalog,
        IRconAuditSink? audit,
        PlayerListPlan players)
    {
        _target = target;
        _containerName = containerName;
        _endpoint = endpoint;
        _argvTemplate = argvTemplate;
        _catalog = catalog;
        _audit = audit;
        _players = players;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <see cref="CommandSpec"/>'s <see cref="CommandIntent"/> is taken directly from the catalogue's
    /// <see cref="RconCommand.ReadOnly"/> flag for <paramref name="commandId"/>, never inferred from the
    /// rendered text: <c>docker exec</c> looks identical on the wire whether it runs <c>rcon-cli Info</c> or
    /// <c>rcon-cli Shutdown</c>.
    /// </remarks>
    /// <exception cref="RconUnknownCommandException"><paramref name="commandId"/> is not in the catalogue.</exception>
    /// <exception cref="RconArgumentException">An argument is missing, unexpected, or hostile.</exception>
    public async Task<RconResponse> InvokeAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        var command = _catalog.Get(commandId);
        var rendered = _catalog.Render(commandId, args);
        var intent = command.ReadOnly ? CommandIntent.ReadOnly : CommandIntent.Mutating;

        return await ExecuteAsync(rendered, intent, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A raw, operator-authored command carries no declared classification, so — exactly as
    /// <see cref="WriteGuardedRconSession"/> treats its own <c>SendRawAsync</c> — it is classified
    /// <see cref="CommandIntent.Mutating"/> rather than guessed at from its text.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <see cref="IRconAuditSink"/> was supplied.</exception>
    public Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCommand);

        if (_audit is null)
        {
            // Mirrors RconSession.SendRawAsync's fail-closed refusal exactly (see its remarks and
            // docs/abstractions.md §8: raw operator-authored commands are "always logged to the audit
            // trail"). This session has no less duty to honour that than a direct-tcp one just because it
            // reaches RCON via 'docker exec' — a raw command run here is still catalogue-bypassing and
            // still unclassified, so it is refused rather than sent unrecorded.
            throw new InvalidOperationException(
                "The raw RCON escape hatch requires an audit sink, and this docker-exec-tool session has none. "
                + "A raw command bypasses the definition's command catalogue and therefore its readOnly "
                + "classification; the audit record is the only remaining account of what was run, so an "
                + "unrecorded raw command is refused outright. Use InvokeAsync with a catalogued command id "
                + "instead, or supply an IRconAuditSink to DockerExecToolRconReachability.");
        }

        // Rejected before it is recorded and before it is sent: a "command" carrying an embedded newline is
        // two commands, and an audit line naming only the first would be a false record.
        RconCommandText.EnsureSingleCommandLine(rawCommand);

        return SendRawAuditedAsync(rawCommand, ct);
    }

    private async Task<RconResponse> SendRawAuditedAsync(string rawCommand, CancellationToken ct)
    {
        await _audit!.RecordRawCommandAsync(_endpoint, rawCommand, ct).ConfigureAwait(false);
        return await ExecuteAsync(rawCommand, CommandIntent.Mutating, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both the command invoked and the shape its reply is parsed in come from the definition, exactly as
    /// <see cref="RconSession.GetPlayersAsync"/> does. When this session's <see cref="PlayerListPlan"/>
    /// resolved no command, nothing is sent and the answer is <see cref="PlayerListFidelity.Unknown"/>.
    /// </remarks>
    public async Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default)
    {
        if (_players.CommandId is not { } commandId)
        {
            return new PlayerSnapshot(DateTimeOffset.UtcNow, PlayerListSnapshot.Unresolved(_players.Diagnostic));
        }

        var response = await InvokeAsync(commandId, null, ct).ConfigureAwait(false);
        return new PlayerSnapshot(DateTimeOffset.UtcNow, RconPlayerListParser.Parse(response.Text, _players.Parser));
    }

    private async Task<RconResponse> ExecuteAsync(string renderedCommand, CommandIntent intent, CancellationToken ct)
    {
        var argv = RenderArgv(_argvTemplate, renderedCommand);
        var spec = new CommandSpec("docker", ["exec", _containerName, .. argv], Intent: intent);

        var result = await _target.ExecuteAsync(spec, ct).ConfigureAwait(false);
        return new RconResponse(result.StandardOutput, result.Succeeded);
    }

    private static IReadOnlyList<string> RenderArgv(IReadOnlyList<string> template, string renderedCommand)
    {
        var argv = new string[template.Count];
        for (var i = 0; i < template.Count; i++)
        {
            // One verbatim substitution per element, mirroring RconCommandText.Render's "substituted text is
            // never re-scanned" guarantee: a rendered command that itself contains the literal text
            // "{command}" is inert, and the element stays a single argv slot no matter how many spaces or
            // quotes the rendered command carries.
            argv[i] = template[i].Replace("{command}", renderedCommand, StringComparison.Ordinal);
        }

        return argv;
    }
}
