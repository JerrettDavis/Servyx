namespace Servyx.Domain.Control;

/// <summary>
/// A single capability Servyx may hold over a managed server. Each bit represents one concrete thing
/// Servyx can verifiably do, independent of how it does it.
/// </summary>
/// <remarks>
/// <para>
/// The bits are organized loosely by area (read/observe, configuration read, configuration write,
/// lifecycle, exec/control-channel, save data, and extras) but they are not an ordered scale: holding a
/// higher-numbered bit does not imply holding a lower-numbered one, and no bit is "more advanced" than
/// another purely by its position.
/// </para>
/// <para>
/// In particular, <see cref="WriteAuthoritativeConfig"/>, <see cref="WriteEnvFile"/>, and
/// <see cref="WriteComposeFile"/> are <b>alternative mechanisms for the same user intent</b> — "let
/// Servyx change this server's settings" — not a progression from weakest to strongest. A bare-metal
/// target reachable only via direct `.ini` writes and a containerized target reachable only via compose
/// edits both satisfy that intent through entirely different, mutually exclusive mechanisms. Consumers
/// that need "can write config, however it gets there" must check these as alternatives (see
/// <c>CapabilityRequirement.AnyOf</c>), never assume one implies or supersedes another.
/// </para>
/// </remarks>
[Flags]
public enum ControlCapability : ulong
{
    /// <summary>No capabilities are held.</summary>
    None = 0,

    /// <summary>Servyx can read the workload's current runtime state (running, stopped, health, etc.).</summary>
    ReadRuntimeState = 1UL << 0,

    /// <summary>Servyx can stream the workload's logs as they are produced.</summary>
    StreamLogs = 1UL << 1,

    /// <summary>Servyx can read resource metrics (CPU, memory, etc.) for the workload.</summary>
    ReadMetrics = 1UL << 2,

    /// <summary>Servyx can read configuration as derived/observed at runtime (not necessarily the authoritative source).</summary>
    ReadDerivedConfig = 1UL << 3,

    /// <summary>Servyx can read the authoritative configuration source (e.g. the server's `.ini` file).</summary>
    ReadAuthoritativeConfig = 1UL << 4,

    /// <summary>Servyx can read the deployment's `.env` file.</summary>
    ReadEnvFile = 1UL << 5,

    /// <summary>Servyx can read the deployment's compose file.</summary>
    ReadComposeFile = 1UL << 6,

    /// <summary>
    /// Servyx can write directly to the authoritative configuration source. One of three alternative
    /// mechanisms for changing settings; see the type-level remarks.
    /// </summary>
    WriteAuthoritativeConfig = 1UL << 7,

    /// <summary>
    /// Servyx can write the deployment's `.env` file. One of three alternative mechanisms for changing
    /// settings; see the type-level remarks.
    /// </summary>
    WriteEnvFile = 1UL << 8,

    /// <summary>
    /// Servyx can write the deployment's compose file. One of three alternative mechanisms for changing
    /// settings; see the type-level remarks.
    /// </summary>
    WriteComposeFile = 1UL << 9,

    /// <summary>Servyx can start the workload.</summary>
    StartWorkload = 1UL << 10,

    /// <summary>Servyx can stop the workload gracefully (e.g. requesting a clean shutdown).</summary>
    StopWorkloadGraceful = 1UL << 11,

    /// <summary>Servyx can send a signal directly to the workload's process.</summary>
    SignalProcess = 1UL << 12,

    /// <summary>Servyx can forcibly kill the workload.</summary>
    KillWorkload = 1UL << 13,

    /// <summary>Servyx can recreate the workload (e.g. `docker compose up --force-recreate`).</summary>
    RecreateWorkload = 1UL << 14,

    /// <summary>Servyx can create a new workload from scratch.</summary>
    CreateWorkload = 1UL << 15,

    /// <summary>Servyx can permanently destroy the workload and its associated resources.</summary>
    DestroyWorkload = 1UL << 16,

    /// <summary>Servyx can execute a command inside the running workload (e.g. `docker exec`).</summary>
    ExecInWorkload = 1UL << 17,

    /// <summary>Servyx can attach to the workload's stdin.</summary>
    AttachStdin = 1UL << 18,

    /// <summary>Servyx can read from a live control channel (e.g. RCON) exposed by the workload.</summary>
    ControlChannelRead = 1UL << 19,

    /// <summary>Servyx can write to a live control channel (e.g. RCON) exposed by the workload.</summary>
    ControlChannelWrite = 1UL << 20,

    /// <summary>Servyx can forward network ports between the panel and the workload.</summary>
    PortForward = 1UL << 21,

    /// <summary>Servyx can read the workload's save data.</summary>
    ReadSaveData = 1UL << 22,

    /// <summary>Servyx can write the workload's save data.</summary>
    WriteSaveData = 1UL << 23,

    /// <summary>Servyx can create a backup of the workload.</summary>
    CreateBackup = 1UL << 24,

    /// <summary>Servyx can restore the workload from a backup.</summary>
    RestoreBackup = 1UL << 25,

    /// <summary>Servyx can install mods for the workload.</summary>
    InstallMods = 1UL << 26,
}
