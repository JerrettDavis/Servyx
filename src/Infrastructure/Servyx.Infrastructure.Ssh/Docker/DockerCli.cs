using System.Globalization;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// Builds <see cref="CommandSpec"/> values for the <c>docker</c> CLI, for the ssh+docker transport that runs
/// docker commands over an existing SSH exec channel to manage a remote game server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every factory here declares its <see cref="CommandIntent"/>; none of them infer it.</strong>
/// <c>docker exec</c> is the same API call whether the argv it carries runs <c>rcon-cli Info</c> or
/// <c>rcon-cli Shutdown</c> — the text of the argv cannot tell a caller what the command does, only the
/// author of that argv can. So this class is split into two halves by construction, not by a runtime check:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// The read-only half (<see cref="Ps"/>, <see cref="Inspect"/>, <see cref="Logs"/>, <see cref="Stats"/>,
/// <see cref="Version"/>, <see cref="ExecReadOnly"/>) passes <see cref="CommandIntent.ReadOnly"/> explicitly
/// to the <see cref="CommandSpec"/> constructor. Explicit, so that deleting the argument is a visible diff,
/// not a silent behavior change.
/// </description>
/// </item>
/// <item>
/// <description>
/// The mutating half (<see cref="Start"/>, <see cref="Stop"/>, <see cref="Restart"/>, <see cref="Kill"/>,
/// <see cref="Exec"/>, <see cref="Pull"/>) never passes an intent argument at all, relying on
/// <see cref="CommandIntent.Mutating"/>
/// being the enum's zero value. A future factory added to this class without thinking about intent gets the
/// default that fails closed — refused on a read-only server — rather than one that fails open.
/// </description>
/// </item>
/// </list>
/// <para>
/// <see cref="ExecReadOnly"/> exists only because <c>docker exec</c> can carry an arbitrary remote command;
/// see its own remarks for the narrow contract that makes calling it safe.
/// </para>
/// </remarks>
public static class DockerCli
{
    private const string Executable = "docker";

    /// <summary>Lists every container, including stopped ones, as one JSON object per line.</summary>
    public static CommandSpec Ps() =>
        new(
            Executable,
            ["container", "ls", "--all", "--no-trunc", "--format", "{{json .}}"],
            Intent: CommandIntent.ReadOnly);

    /// <summary>Inspects a single container's full JSON configuration and state.</summary>
    public static CommandSpec Inspect(string containerIdOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerIdOrName);

        return new CommandSpec(
            Executable,
            ["container", "inspect", containerIdOrName],
            Intent: CommandIntent.ReadOnly);
    }

    /// <summary>Reads the trailing <paramref name="tailLines"/> lines of a container's logs, timestamped.</summary>
    public static CommandSpec Logs(string container, int tailLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentOutOfRangeException.ThrowIfNegative(tailLines);

        return new CommandSpec(
            Executable,
            ["logs", "--tail", tailLines.ToString(CultureInfo.InvariantCulture), "--timestamps", container],
            Intent: CommandIntent.ReadOnly);
    }

    /// <summary>Takes a single non-streaming snapshot of a container's resource usage, as JSON.</summary>
    public static CommandSpec Stats(string container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        return new CommandSpec(
            Executable,
            ["stats", "--no-stream", "--format", "{{json .}}", container],
            Intent: CommandIntent.ReadOnly);
    }

    /// <summary>Reads the local docker CLI/engine version, as JSON.</summary>
    public static CommandSpec Version() =>
        new(
            Executable,
            ["version", "--format", "{{json .}}"],
            Intent: CommandIntent.ReadOnly);

    /// <summary>
    /// Runs <paramref name="argv"/> inside <paramref name="container"/> via <c>docker exec</c>, declared
    /// <see cref="CommandIntent.ReadOnly"/>.
    /// </summary>
    /// <remarks>
    /// This is the one <c>docker exec</c> path a caller is allowed to mark read-only, and it is named
    /// distinctly from <see cref="Exec"/> on purpose: the different name is a review signal, flagging every
    /// call site as a place where a human decided the argv is provably side-effect-free before writing it —
    /// for example <c>which rcon-cli</c> or <c>rcon-cli Info</c>. Callers MUST NOT pass argv that could
    /// mutate the target (e.g. an RCON command that saves, kicks, bans, or shuts the server down). There is
    /// no way for this method to verify that itself; the guarantee lives entirely in the caller's choice of
    /// argv, which is exactly why misusing it is the failure mode this whole class exists to make visible.
    /// </remarks>
    public static CommandSpec ExecReadOnly(string container, IReadOnlyList<string> argv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(argv);

        return new CommandSpec(
            Executable,
            ["exec", container, .. argv],
            Intent: CommandIntent.ReadOnly);
    }

    /// <summary>Starts a stopped container. Mutating: relies on the <see cref="CommandIntent"/> default.</summary>
    public static CommandSpec Start(string container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        return new CommandSpec(Executable, ["start", container]);
    }

    /// <summary>
    /// Stops a running container, giving it up to <paramref name="timeoutSeconds"/> to exit gracefully before
    /// it is killed. Mutating: relies on the <see cref="CommandIntent"/> default.
    /// </summary>
    public static CommandSpec Stop(string container, int timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);

        return new CommandSpec(
            Executable,
            ["stop", "--time", timeoutSeconds.ToString(CultureInfo.InvariantCulture), container]);
    }

    /// <summary>Restarts a container. Mutating: relies on the <see cref="CommandIntent"/> default.</summary>
    public static CommandSpec Restart(string container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        return new CommandSpec(Executable, ["restart", container]);
    }

    /// <summary>
    /// Runs <paramref name="argv"/> inside <paramref name="container"/> via <c>docker exec</c>. Mutating:
    /// relies on the <see cref="CommandIntent"/> default, because in general an arbitrary exec'd command may
    /// change the target's state. Use <see cref="ExecReadOnly"/> only for argv proven side-effect-free.
    /// </summary>
    public static CommandSpec Exec(string container, IReadOnlyList<string> argv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(argv);

        return new CommandSpec(Executable, ["exec", container, .. argv]);
    }

    /// <summary>Pulls the latest image. Mutating: relies on the <see cref="CommandIntent"/> default.</summary>
    public static CommandSpec Pull(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        return new CommandSpec(Executable, ["pull", image]);
    }

    /// <summary>
    /// Terminates a container immediately, optionally with a specific OS <paramref name="signal"/> rather
    /// than docker's default (<c>SIGKILL</c>). Mutating: relies on the <see cref="CommandIntent"/> default.
    /// </summary>
    public static CommandSpec Kill(string container, string? signal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        if (signal is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        }

        List<string> argv = signal is null
            ? ["kill", container]
            : ["kill", "--signal", signal, container];

        return new CommandSpec(Executable, argv);
    }
}
