# Servyx.Domain Abstractions

This document is the canonical C# interface specification for
`Servyx.Domain`. It targets `net10.0`, with nullable reference types enabled
and warnings treated as errors. `Servyx.Domain` has **zero I/O dependencies**
— no `Docker.DotNet`, no `SSH.NET`, no EF Core. Implementations of the
interfaces below live in `Servyx.Infrastructure.Docker`,
`Servyx.Infrastructure.Ssh`, `Servyx.Infrastructure.Process`,
`Servyx.Config`, and `Servyx.Protocols`.

Implementers should read this file directly rather than being re-briefed —
the signatures here are load-bearing and should not be altered without
updating this document.

## Cross-Cutting Rules

1. **No shell strings, ever.** Every command crosses a transport as an argv
   array. Definition-supplied values become array elements, never substrings
   of a command line. This is the injection boundary the entire transport
   layer is built around.
2. **Nothing mutates without a preview.** Any interface that changes state
   exposes a `Plan…`/`Preview…` method returning a reviewable object, and the
   corresponding apply method takes that object's id. M1 implements only the
   plan/preview side of every interface below — apply paths land in later
   milestones.

## §1 Transport

The transport seam sits at *reaching a host*, deliberately below
container-vs-process. Local execution, Docker, SSH, and SSH+Docker are four
implementations of one contract, which is what lets Kubernetes or Proxmox
become plugins later rather than forks of the core — this is the design
mistake Pterodactyl's Wings daemon made (see upstream issue #4225), where
container concerns are hard-wired throughout instead of being one
implementation of a general reach-a-host contract.

```csharp
namespace Servyx.Domain.Transport;

/// <summary>
/// Capabilities a transport may support. A transport advertises the subset
/// it actually implements; callers must check before invoking a capability
/// that may not be present.
/// </summary>
[Flags]
public enum TransportCapabilities
{
    None            = 0,
    ExecuteCommand  = 1 << 0,
    StreamOutput    = 1 << 1,
    StreamStdin     = 1 << 2,
    FileRead        = 1 << 3,
    FileWrite       = 1 << 4,
    DirectoryList   = 1 << 5,
    ContainerApi    = 1 << 6,
    ProcessApi      = 1 << 7,
    PortForward     = 1 << 8,
}

/// <summary>
/// Identifies a specific target reachable through a transport. Immutable;
/// two descriptors with equal values are considered the same target.
/// </summary>
/// <param name="TransportId">"local" | "docker" | "ssh" | "ssh+docker".</param>
/// <param name="Endpoint">
/// Transport-specific endpoint address, e.g.
/// "npipe://./pipe/dockerDesktopLinuxEngine" for local Docker Desktop,
/// or "ssh://host:22" for a remote host.
/// </param>
/// <param name="CredentialUrn">URN identifying credentials in the secret store, if any.</param>
/// <param name="DockerContext">Named Docker context to use, when applicable (e.g. "desktop-linux").</param>
/// <param name="Options">Additional transport-specific key/value options.</param>
public sealed record TargetDescriptor(
    string TransportId,
    string Endpoint,
    string? CredentialUrn,
    string? DockerContext,
    IReadOnlyDictionary<string, string> Options);

/// <summary>
/// A transport is a way of reaching a host or workload. It does not itself
/// represent a connection — call <see cref="ConnectAsync"/> to obtain an
/// <see cref="IExecutionTarget"/> session.
/// </summary>
public interface ITransport
{
    /// <summary>Stable identifier for this transport implementation.</summary>
    string TransportId { get; }

    /// <summary>Capabilities this transport implementation supports.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Checks whether the given target is reachable and reports its health.
    /// MUST be side-effect free: no state on the target may change as a
    /// result of calling this method.
    /// </summary>
    Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default);

    /// <summary>
    /// Establishes a session against the given target. The returned session
    /// is pooled and reference-counted by callers; this method itself does
    /// not pool.
    /// </summary>
    Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default);
}

/// <summary>Result of a reachability probe.</summary>
/// <param name="Reachable">Whether the target responded.</param>
/// <param name="Latency">Round-trip time of the probe, if reachable.</param>
/// <param name="Detail">Human-readable detail, especially on failure.</param>
public sealed record TargetHealth(bool Reachable, TimeSpan? Latency, string? Detail);

/// <summary>
/// An established session against a target, exposing the operations
/// available once connected. Implementations must be safe to hold open
/// across multiple calls and must release underlying resources on
/// <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
public interface IExecutionTarget : IAsyncDisposable
{
    /// <summary>Executes a command to completion and returns its result.</summary>
    Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Executes a command, streaming stdout/stderr chunks as they arrive.
    /// Used for live console attach and long-running operations.
    /// </summary>
    IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default);

    /// <summary>Returns whether a path exists on the target.</summary>
    Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>Returns file metadata for a path on the target.</summary>
    Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>
    /// Lists the immediate contents of a directory. Deliberately
    /// non-recursive, so traversal depth is always bounded by the caller
    /// rather than by the transport.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>Opens a read-only stream over a file on the target.</summary>
    Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>
    /// Writes a file atomically: content is written to a temporary sibling
    /// file and then renamed into place. Returns a receipt including the
    /// SHA-256 of the pre-image (or null if the file did not previously
    /// exist). If <paramref name="options"/> specifies an
    /// <c>ExpectedPreImageHash</c> that does not match the file's current
    /// content, the write is refused and <see cref="TargetDriftException"/>
    /// is thrown before any I/O occurs.
    /// </summary>
    Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default);

    /// <summary>Deletes a file on the target.</summary>
    Task DeleteAsync(TargetPath path, CancellationToken ct = default);
}

/// <summary>
/// A command to execute on a target. <see cref="Executable"/> never
/// contains arguments; <see cref="Arguments"/> are passed verbatim to the
/// target process with no shell expansion, globbing, or redirection —
/// remote transports (e.g. SSH) are responsible for quoting each argument
/// individually rather than joining them into a shell line.
/// </summary>
/// <param name="Executable">The program or entrypoint to invoke.</param>
/// <param name="Arguments">Argv array, passed through verbatim.</param>
/// <param name="WorkingDirectory">Optional working directory on the target.</param>
/// <param name="EnvironmentOverrides">Optional environment variable overrides.</param>
/// <param name="Timeout">Optional execution timeout.</param>
public sealed record CommandSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    TimeSpan? Timeout = null);

/// <summary>Result of a completed, non-streaming command execution.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

/// <summary>Identifies which stream an <see cref="OutputChunk"/> came from.</summary>
public enum OutputStream { StdOut, StdErr }

/// <summary>A single chunk of streamed command output.</summary>
public sealed record OutputChunk(OutputStream Stream, string Text, DateTimeOffset Timestamp);

/// <summary>
/// A path scoped to a target's server root. The constructor is internal so
/// that path traversal is rejected at construction time, at the type level,
/// rather than being re-validated ad hoc at every call site that accepts a
/// path.
/// </summary>
public readonly record struct TargetPath
{
    /// <summary>The normalized, root-relative path value.</summary>
    public string Value { get; }

    internal TargetPath(string value) => Value = value;
}

/// <summary>An entry returned by <see cref="IExecutionTarget.ListDirectoryAsync"/>.</summary>
public sealed record FileEntry(string Name, bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt);

/// <summary>Metadata about a single file or directory on a target.</summary>
public sealed record FileStat(bool Exists, bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt, string? Sha256);

/// <summary>Options controlling an atomic file write.</summary>
/// <param name="ExpectedPreImageHash">
/// SHA-256 of the content the caller last observed. If the file's current
/// content does not match, the write is refused with
/// <see cref="TargetDriftException"/>. Null means "no expectation" and
/// should only be used for files known not to previously exist.
/// </param>
public sealed record FileWriteOptions(string? ExpectedPreImageHash);

/// <summary>Receipt returned after a successful atomic file write.</summary>
/// <param name="PreImageSha256">Hash of the file's content before this write, or null if it did not exist.</param>
/// <param name="PostImageSha256">Hash of the file's content after this write.</param>
public sealed record FileWriteReceipt(string? PreImageSha256, string PostImageSha256, DateTimeOffset WrittenAt);

/// <summary>
/// Thrown when a write is refused because the target's current content no
/// longer matches the caller's expected pre-image hash — the file has
/// drifted since it was last observed.
/// </summary>
public sealed class TargetDriftException : Exception
{
    public TargetDriftException(string message) : base(message) { }
}

/// <summary>
/// A decorator over <see cref="IExecutionTarget"/> whose mutating members
/// throw <see cref="WritesDisabledException"/> before any I/O when the
/// owning server's <see cref="WriteMode"/> is <see cref="WriteMode.ReadOnly"/>.
/// Individual services are never trusted to check the write mode
/// themselves — an architecture test asserts that no transport can be
/// registered in DI without this decorator wrapping it.
/// </summary>
public sealed class WriteGuardedExecutionTarget : IExecutionTarget
{
    // Implementation wraps an inner IExecutionTarget and checks WriteMode
    // before delegating any mutating call: WriteFileAsync, DeleteAsync,
    // and any lifecycle/control operations reached through this target.
}

/// <summary>The write posture of a server, checked by <see cref="WriteGuardedExecutionTarget"/>.</summary>
public enum WriteMode
{
    /// <summary>No mutating operation is permitted; only reads and read-only control commands.</summary>
    ReadOnly,
    /// <summary>Plans may be previewed but never applied.</summary>
    PreviewOnly,
    /// <summary>Writes are permitted, subject to per-plan approval.</summary>
    Enabled,
}

/// <summary>Thrown by <see cref="WriteGuardedExecutionTarget"/> when a mutating call is attempted under a non-permitting <see cref="WriteMode"/>.</summary>
public sealed class WritesDisabledException : Exception
{
    public WritesDisabledException(string message) : base(message) { }
}
```

## §2 Definitions

The seam here is at the *source* of definitions, not at parsing them —
`IGameDefinitionProvider` abstracts over builtin, directory, git, and
HTTP-catalog sources uniformly, and trust evaluation is funneled through
exactly one chokepoint (`IDefinitionTrustEvaluator`) regardless of source.
This is the direct answer to CVE-2023-32080, where trust and parsing were
not cleanly separated.

```csharp
namespace Servyx.Domain.Definitions;

/// <summary>
/// A reference to a specific definition by content, not by mutable version.
/// Servers pin <see cref="ContentHash"/>, the SHA-256 of the definition's
/// raw bytes, never the human-readable <c>version</c> string from its
/// metadata.
/// </summary>
public sealed record GameDefinitionRef(string Id, string ContentHash, string SourceId);

/// <summary>A fully parsed and validated definition, ready for use.</summary>
public sealed record LoadedDefinition(GameDefinitionRef Ref, TrustVerdict Trust, object Document);

/// <summary>
/// Supplies game definitions from a particular origin. Multiple providers
/// may be registered; the aggregate catalogue is their union.
/// </summary>
public interface IGameDefinitionProvider
{
    /// <summary>"builtin" | "directory" | "git" | "http-catalog".</summary>
    string SourceId { get; }

    /// <summary>Lists all definition references available from this provider.</summary>
    Task<IReadOnlyList<GameDefinitionRef>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads and validates a specific definition. Throws
    /// <see cref="DefinitionValidationException"/>, with YAML line/column
    /// information, if the definition fails schema or semantic validation.
    /// </summary>
    Task<LoadedDefinition> LoadAsync(GameDefinitionRef reference, CancellationToken ct = default);

    /// <summary>
    /// Watches this provider's source for changes, yielding updated
    /// references as they appear, to support hot reload during
    /// development.
    /// </summary>
    IAsyncEnumerable<GameDefinitionRef> WatchAsync(CancellationToken ct = default);
}

/// <summary>Validates a definition document against the schema and semantic rules.</summary>
public interface IGameDefinitionValidator
{
    ValidationReport Validate(object document);
}

/// <summary>Aggregate result of validating a definition.</summary>
public sealed record ValidationReport(bool IsValid, IReadOnlyList<ValidationIssue> Issues);

/// <summary>A single validation finding, including source position for editor integration.</summary>
public sealed record ValidationIssue(string Message, int Line, int Column, ValidationSeverity Severity);

public enum ValidationSeverity { Error, Warning }

/// <summary>
/// Thrown when <see cref="IGameDefinitionProvider.LoadAsync"/> encounters a
/// definition that fails validation. Carries the same line/column
/// information as <see cref="ValidationIssue"/>.
/// </summary>
public sealed class DefinitionValidationException : Exception
{
    public DefinitionValidationException(string message) : base(message) { }
}

/// <summary>
/// Assigns a trust tier to a loaded definition. This is the single
/// chokepoint through which all trust decisions pass, regardless of which
/// <see cref="IGameDefinitionProvider"/> supplied the definition.
/// </summary>
public interface IDefinitionTrustEvaluator
{
    TrustVerdict Evaluate(LoadedDefinition definition);
}

/// <summary>Trust tier assigned to a definition.</summary>
public enum TrustTier { Builtin, Verified, Unverified }

/// <summary>Result of trust evaluation, including which capabilities are permitted.</summary>
public sealed record TrustVerdict(TrustTier Tier, IReadOnlyList<string> DeniedCapabilities, string? Reason);
```

## §3 Configuration

```csharp
namespace Servyx.Domain.Configuration;

/// <summary>
/// Parses and renders a single configuration format. Round-trip fidelity is
/// a hard contract: <c>Render(Parse(x)) == x</c> byte-for-byte, including
/// comments, blank lines, key order, and quoting style. This is
/// property-tested against the ~150-key <c>.env</c> fixture used
/// throughout the Palworld deployment.
/// </summary>
public interface IConfigAdapter
{
    /// <summary>Format identifier, e.g. "dotenv", "yaml", "ini", "json".</summary>
    string FormatId { get; }

    /// <summary>Whether this adapter preserves comments through a round-trip.</summary>
    bool PreservesComments { get; }

    ConfigDocument Parse(string raw);

    string Render(ConfigDocument document);
}

/// <summary>
/// Decodes a structured payload embedded inside a single scalar value. The
/// motivating example is <c>unreal-option-settings</c>, which decodes the
/// Unreal Engine <c>OptionSettings=(...)</c> blob into named members.
/// </summary>
public interface IConfigValueCodec
{
    /// <summary>Codec identifier, e.g. "unreal-option-settings".</summary>
    string CodecId { get; }

    /// <summary>
    /// Decodes a scalar into its structured member values, preserving
    /// member order for re-encoding.
    /// </summary>
    IReadOnlyDictionary<string, string> Decode(string scalar);

    /// <summary>
    /// Re-encodes structured member values back into scalar form,
    /// preserving member order and numeric formatting — Unreal expects
    /// <c>1.000000</c>, not <c>1</c>.
    /// </summary>
    string Encode(IReadOnlyDictionary<string, string> members);
}

/// <summary>A parsed configuration document, as produced by an <see cref="IConfigAdapter"/>.</summary>
public sealed record ConfigDocument(object Root, IReadOnlyList<string> RawLines);

/// <summary>Addresses a specific value within a <see cref="ConfigDocument"/> (a key, a JSON-pointer-like path, or a codec member).</summary>
public sealed record ConfigPointer(string Path);

/// <summary>
/// The role a configuration surface plays. This is the central modeling
/// decision in Servyx: every configuration surface is one of these three,
/// and the role determines whether Servyx may write to it at all.
/// </summary>
public enum SurfaceRole
{
    /// <summary>Servyx may write to this surface; it is a genuine source of intent (e.g. <c>.env</c>).</summary>
    Authoritative,

    /// <summary>
    /// Generated by the workload itself at boot. Servyx reads this surface
    /// for drift detection only. Writing to a <c>Derived</c> surface is
    /// silently discarded the next time the workload regenerates it — this
    /// exact failure mode is why users lose trust in control panels that
    /// do not model it, and why it is enforced structurally here rather
    /// than left to adapter discipline.
    /// </summary>
    Derived,

    /// <summary>Live state observed over a control channel. Read-only by nature.</summary>
    Runtime,
}

/// <summary>A single configuration surface as declared by a game definition.</summary>
public sealed record ConfigSurface(string Id, SurfaceRole Role, SurfaceLocator Locator, string FormatId, string? CodecId)
{
    /// <summary>True only when <see cref="Role"/> is <see cref="SurfaceRole.Authoritative"/>.</summary>
    public bool ServyxMayWrite => Role == SurfaceRole.Authoritative;
}

/// <summary>How a <see cref="SurfaceRole.Derived"/> surface gets regenerated.</summary>
public enum RegenerationKind { ContainerRestart, ProcessRestart, Manual }

/// <summary>Describes when and how a derived surface is expected to regenerate.</summary>
public sealed record RegenerationTrigger(RegenerationKind Kind, string Description);

/// <summary>Where a configuration surface physically lives.</summary>
public abstract record SurfaceLocator
{
    /// <summary>A file on the target host or container filesystem.</summary>
    public sealed record HostFile(string Path) : SurfaceLocator;

    /// <summary>A query against a live control channel.</summary>
    public sealed record ControlChannel(string ChannelId, string Query) : SurfaceLocator;
}

/// <summary>
/// The four-column view of a single setting, mirroring the surface roles:
/// Servyx's intent, the current authoritative value, the current rendered
/// (derived) value, and the current live (runtime) value.
/// </summary>
public sealed record SettingState(
    string? Desired,
    string? Authoritative,
    string? Rendered,
    string? Runtime,
    DriftKind Drift,
    bool PendingRegeneration,
    bool IsWritable,
    string? NotWritableReason);

/// <summary>Which pairs of columns in a <see cref="SettingState"/> disagree.</summary>
[Flags]
public enum DriftKind
{
    None                        = 0,
    DesiredVsAuthoritative      = 1 << 0,
    AuthoritativeVsRendered     = 1 << 1,
    RenderedVsRuntime           = 1 << 2,
    Unreadable                  = 1 << 3,
}

/// <summary>Computes <see cref="SettingState"/> for a setting across its bound surfaces.</summary>
public interface ISettingStateResolver
{
    Task<SettingState> ResolveAsync(string settingKey, CancellationToken ct = default);
}

/// <summary>
/// Merges a new value into an existing configuration document without
/// disturbing content Servyx does not manage.
/// </summary>
public interface IConfigMerger
{
    ConfigDocument Merge(ConfigDocument existing, ConfigPointer target, string newValue, MergePolicy policy);
}

/// <summary>
/// Policy governing how unmanaged content is treated on write. There is
/// deliberately no "rewrite whole file" policy — every write must go
/// through one of these two, both of which preserve everything Servyx does
/// not explicitly own.
/// </summary>
public enum MergePolicy
{
    /// <summary>Default. Unmanaged keys are never touched, reordered, or reformatted.</summary>
    PreserveUnknown,

    /// <summary>
    /// Writes are confined to a delimited region
    /// (<c># >>> servyx:managed >>></c> … <c># &lt;&lt;&lt; servyx:managed &lt;&lt;&lt;</c>)
    /// within an otherwise unstructured file.
    /// </summary>
    ManagedBlock,
}

/// <summary>A previewed, not-yet-applied set of configuration changes.</summary>
public sealed record ConfigChangePlan(string Id, IReadOnlyList<PlannedAction> Actions, IReadOnlyList<Consequence> Consequences, IReadOnlyDictionary<string, string> SurfaceHashes);

public enum PlannedActionKind { WriteSurface, WriteControlChannel }

/// <summary>A single action within a <see cref="ConfigChangePlan"/>, including its unified diff.</summary>
public sealed record PlannedAction(PlannedActionKind Kind, string SurfaceId, string UnifiedDiff, bool Reversible, TransportCapabilities RequiredCapabilities);

public enum ConsequenceKind { RestartRequired, RecreateRequired, ServiceInterruption }

/// <summary>A downstream effect of applying a plan, surfaced to the operator before approval.</summary>
public sealed record Consequence(ConsequenceKind Kind, string Description);

/// <summary>
/// The single funnel through which every mutation in the product passes.
/// No other interface applies a configuration write directly.
/// </summary>
public interface IPlanExecutor
{
    /// <summary>
    /// Read-only. Produces a unified diff with secrets masked, a
    /// reversibility flag per action, the capabilities required, and any
    /// restart/recreate consequences.
    /// </summary>
    Task<ConfigChangePlan> PreviewAsync(string serverId, IReadOnlyDictionary<string, string> desiredValues, CancellationToken ct = default);

    /// <summary>
    /// Applies a previously previewed and approved plan by id. Throws
    /// <see cref="PlanStaleException"/> if any bound surface has drifted
    /// since preview, and <see cref="WritesDisabledException"/> if the
    /// server's write mode does not permit it.
    /// </summary>
    Task<ChangeReceipt> ApplyAsync(string planId, CancellationToken ct = default);

    /// <summary>Reverts a previously applied plan using its recorded pre-images.</summary>
    Task RevertAsync(string planId, CancellationToken ct = default);
}

/// <summary>Thrown when <see cref="IPlanExecutor.ApplyAsync"/> is called against a plan whose bound surfaces have drifted since preview.</summary>
public sealed class PlanStaleException : Exception
{
    public PlanStaleException(string message) : base(message) { }
}

/// <summary>Record of a successfully applied plan.</summary>
public sealed record ChangeReceipt(string PlanId, DateTimeOffset AppliedAt, IReadOnlyList<PlannedAction> Actions);
```

## §4 Lifecycle

```csharp
namespace Servyx.Domain.Lifecycle;

public enum ServerState { Unknown, Stopped, Starting, Running, Stopping, Crashed }

/// <summary>Current observed status of a server.</summary>
public sealed record ServerStatus(ServerState State, DateTimeOffset? StartedAt, TimeSpan? Uptime);

/// <summary>
/// Controls the lifecycle of a single server. Mutating members are subject
/// to the write guard exactly as file writes are.
/// </summary>
public interface IServerLifecycle
{
    Task<ServerStatus> GetStatusAsync(CancellationToken ct = default);

    Task<StartOutcome> StartAsync(CancellationToken ct = default);

    Task<StopOutcome> StopAsync(StopPlan plan, CancellationToken ct = default);

    Task<StopOutcome> RestartAsync(StopPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Recreates the underlying container. Requires an already-approved
    /// <see cref="ConfigChangePlan"/> id whose consequences include
    /// <c>RecreateRequired</c> — this operation is never callable ad hoc,
    /// only as the applied consequence of a previewed plan.
    /// </summary>
    Task RecreateAsync(string approvedChangePlanId, CancellationToken ct = default);

    /// <summary>Streams status changes as they occur.</summary>
    IAsyncEnumerable<ServerStatus> WatchAsync(CancellationToken ct = default);
}

/// <summary>A single stage in a <see cref="StopPlan"/> escalation ladder.</summary>
public abstract record StopStage
{
    public sealed record Rcon(string CommandId, TimeSpan Timeout) : StopStage;
    public sealed record ConsoleWrite(string Text, TimeSpan Timeout) : StopStage;
    public sealed record Signal(string SignalName, TimeSpan Timeout) : StopStage;
    public sealed record Kill : StopStage;
}

/// <summary>An ordered escalation ladder: e.g. rcon → console → signal → kill.</summary>
public sealed record StopPlan(IReadOnlyList<StopStage> Stages);

/// <summary>Records which stage of a <see cref="StopPlan"/> actually stopped the server.</summary>
public sealed record StopOutcome(StopStage StageThatStopped, TimeSpan TotalDuration);

/// <summary>Result of a start attempt.</summary>
public sealed record StartOutcome(bool Ready, TimeSpan TimeToReady, ReadinessSignal Signal);

/// <summary>Detects when a starting server has become ready to serve.</summary>
public interface IReadinessDetector
{
    Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default);
}

/// <summary>
/// Readiness detector based on matching a regex against console output.
/// Definition-supplied regexes are untrusted input: they are compiled with
/// a non-backtracking regex engine and evaluated with a per-line match
/// timeout, so a malicious or accidental catastrophic-backtracking pattern
/// cannot become a ReDoS vector against the panel host.
/// </summary>
public sealed class LogRegexReadiness : IReadinessDetector
{
    public Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>
/// Readiness detector based on an authenticated control-channel probe.
/// Used as a fallback behind <see cref="LogRegexReadiness"/>, and must
/// never be weaker than the container's own health signal — see
/// "Readiness vs. Container Health" in <c>docs/architecture.md</c>.
/// </summary>
public sealed class ControlProbeReadiness : IReadinessDetector
{
    public Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>Context supplied to a readiness detector.</summary>
public sealed record ReadinessContext(string ServerId, TimeSpan Timeout);

/// <summary>Result of a readiness check.</summary>
public sealed record ReadinessSignal(bool Ready, string DetectorId, string? Detail);
```

## §5 Backups

```csharp
namespace Servyx.Domain.Backups;

/// <summary>
/// Ownership of a backup artifact. This distinction exists to guarantee
/// that Servyx never touches a backup it did not create: <c>Foreign</c>
/// artifacts are listed, inspectable, and restorable, but are never pruned,
/// moved, renamed, or counted against Servyx's own retention policy,
/// regardless of how retention is configured.
/// </summary>
public enum BackupOwnership
{
    /// <summary>Created by Servyx and subject to Servyx's retention policy.</summary>
    Servyx,

    /// <summary>Discovered via an <see cref="IBackupAdopter"/>. Read-only from Servyx's perspective, forever.</summary>
    Foreign,
}

/// <summary>A single backup artifact, Servyx-owned or adopted.</summary>
public sealed record BackupArtifact(string Id, BackupOwnership Ownership, DateTimeOffset CreatedAt, long SizeBytes, string Location);

/// <summary>Creates, lists, inspects, restores, and prunes backups for a server.</summary>
public interface IBackupProvider
{
    Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default);

    /// <summary>Reads an archive's index/manifest without extracting its content.</summary>
    Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default);

    Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default);

    Task RestoreAsync(string restorePlanId, CancellationToken ct = default);

    /// <summary>
    /// Applies retention. MUST skip every <see cref="BackupOwnership.Foreign"/>
    /// artifact regardless of the <paramref name="dryRun"/> flag — foreign
    /// artifacts are never candidates for pruning, not even hypothetically.
    /// </summary>
    Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default);
}

/// <summary>Retention configuration for Servyx-owned backups.</summary>
public sealed record RetentionPolicy(int KeepHourly, int KeepDaily, int KeepWeekly);

/// <summary>A previewed restore operation.</summary>
public sealed record RestorePlan(string Id, string BackupId, IReadOnlyList<string> AffectedPaths);

/// <summary>Result of a prune operation.</summary>
/// <param name="SkippedForeign">Count of foreign artifacts encountered and left untouched.</param>
public sealed record PruneResult(IReadOnlyList<string> Removed, int SkippedForeign);

/// <summary>
/// Discovers backups created outside Servyx by a workload's own mechanism
/// (e.g. a container's built-in cron job), so they can be surfaced as
/// <see cref="BackupOwnership.Foreign"/> without Servyx ever managing their
/// lifecycle.
/// </summary>
public interface IBackupAdopter
{
    /// <summary>e.g. "palworld-docker-cron".</summary>
    string AdapterId { get; }

    bool Supports(string deploymentKind);

    /// <summary>Read-only discovery; never creates, moves, or deletes anything.</summary>
    Task<IReadOnlyList<BackupArtifact>> DiscoverAsync(string serverId, CancellationToken ct = default);
}
```

## §6 Observability

```csharp
namespace Servyx.Domain.Observability;

/// <summary>A single point-in-time resource usage sample.</summary>
public sealed record ResourceSample(DateTimeOffset Timestamp, double CpuPercent, long MemoryBytes, long NetworkRxBytes, long NetworkTxBytes);

/// <summary>
/// Supplies resource metrics. Backed by an in-memory ring buffer and
/// exported via OpenTelemetry — metrics are deliberately not persisted to
/// the relational store.
/// </summary>
public interface IMetricsSource
{
    IAsyncEnumerable<ResourceSample> StreamAsync(string serverId, CancellationToken ct = default);
}

/// <summary>
/// A single line of console output. <see cref="Offset"/> allows a client
/// to resume streaming after a socket drop without re-reading from the
/// start.
/// </summary>
public sealed record ConsoleLine(long Offset, string Text, DateTimeOffset Timestamp);

/// <summary>Options controlling how much backscroll to replay when following console output.</summary>
public sealed record ConsoleTailOptions(int MaxBacklogLines);

/// <summary>
/// Provides access to a server's console output, backed by append-only,
/// rotated files with an offset index — not the relational database.
/// </summary>
public interface ILogStream
{
    /// <summary>Replays tail backscroll per <paramref name="options"/>, then follows new output.</summary>
    IAsyncEnumerable<ConsoleLine> FollowAsync(string serverId, ConsoleTailOptions options, CancellationToken ct = default);

    /// <summary>Reads a range from the on-disk index directly, without touching the live workload.</summary>
    Task<IReadOnlyList<ConsoleLine>> ReadAsync(string serverId, long fromOffset, int count, CancellationToken ct = default);

    /// <summary>Writes a line to the server's stdin. Requires the <c>server.console.write</c> scope.</summary>
    Task WriteAsync(string serverId, string text, CancellationToken ct = default);

    /// <summary>Whether this server's transport supports interactive input.</summary>
    bool SupportsInput { get; }
}

// Every ConsoleLine yielded by ILogStream passes through the global secret
// redactor before being returned to any caller.
```

## §7 Mods

```csharp
namespace Servyx.Domain.Mods;

/// <summary>A reference to a specific mod, at a specific version, from a specific source.</summary>
public sealed record ModRef(string SourceId, string ModId, string Version);

/// <summary>Descriptive metadata about a mod, as returned by search or list operations.</summary>
public sealed record ModDescriptor(ModRef Ref, string Name, string? Description, IReadOnlyList<string> Authors);

/// <summary>
/// A source of mods for a given game (e.g. a mod repository or workshop).
/// </summary>
public interface IModSource
{
    string SourceId { get; }

    bool Supports(string gameId);

    Task<IReadOnlyList<ModDescriptor>> SearchAsync(string gameId, string query, CancellationToken ct = default);

    Task<IReadOnlyList<ModDescriptor>> ListInstalledAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// Returns the exact file operations an install would perform —
    /// expected hashes and source URLs — before anything is downloaded.
    /// </summary>
    Task<ModInstallPlan> PlanInstallAsync(string serverId, ModRef mod, CancellationToken ct = default);

    Task InstallAsync(string installPlanId, CancellationToken ct = default);

    /// <summary>
    /// Uninstalls a mod. Reports, and leaves in place, any file that has
    /// changed since install rather than silently deleting it.
    /// </summary>
    Task UninstallAsync(string serverId, ModRef mod, CancellationToken ct = default);
}

/// <summary>A previewed mod install, with exact file operations and expected hashes.</summary>
public sealed record ModInstallPlan(string Id, ModRef Mod, IReadOnlyList<string> FileOperations, IReadOnlyDictionary<string, string> ExpectedHashes, IReadOnlyList<string> SourceUrls);
```

## §8 RCON

```csharp
namespace Servyx.Domain.Rcon;

/// <summary>Address of an RCON endpoint.</summary>
public sealed record RconEndpoint(string Host, int Port);

/// <summary>Raw response from an RCON invocation.</summary>
public sealed record RconResponse(string Text, bool Success);

/// <summary>A single connected player, as reported by the game.</summary>
public sealed record PlayerInfo(string Name, string PlayerUid, string? SteamId);

/// <summary>A point-in-time list of connected players.</summary>
/// <remarks>
/// Wraps a <c>PlayerListSnapshot</c> (§ the player-list parsing types) so fidelity survives the crossing into
/// the domain boundary: <c>Players</c> and <c>Fidelity</c> are projected off it, and there is deliberately no
/// convenience constructor that would let a caller claim a roster without also saying how trustworthy it is.
/// </remarks>
public sealed record PlayerSnapshot(DateTimeOffset Timestamp, PlayerListSnapshot List)
{
    public IReadOnlyList<PlayerInfo> Players => List.Players;
    public PlayerListFidelity Fidelity => List.Fidelity;
}

/// <summary>Low-level RCON protocol client.</summary>
public interface IRconClient
{
    Task<RconResponse> SendAsync(RconEndpoint endpoint, string password, string command, CancellationToken ct = default);
}

/// <summary>
/// A higher-level RCON session bound to a specific server and definition.
/// </summary>
public interface IRconSession
{
    /// <summary>
    /// Invokes a command by its definition-declared command id (e.g.
    /// "players"), never by raw string, so the write guard can enforce
    /// each command's declared <c>readOnly</c> flag.
    /// </summary>
    Task<RconResponse> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? args, CancellationToken ct = default);

    /// <summary>
    /// Sends a raw, operator-authored RCON command as an audited escape
    /// hatch, bypassing the command catalogue. Always logged to the audit
    /// trail.
    /// </summary>
    Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default);

    /// <summary>
    /// Returns the current list of connected players. Both the command invoked and the shape its reply is
    /// parsed in are resolved from the definition's <c>control.players</c> block (see <c>PlayerListPlan</c>),
    /// never hardcoded — a session with no resolved plan sends nothing and reports
    /// <c>PlayerListFidelity.Unknown</c> rather than inventing a command id.
    /// </summary>
    Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default);
}

/// <summary>
/// Determines and establishes how an RCON (or other control-channel)
/// endpoint is actually reached, since the port is frequently not
/// published to the host network.
/// </summary>
public interface IRconReachability
{
    /// <summary>"direct-tcp" | "docker-exec-tool" | "docker-exec-network" | "ssh-tunnel".</summary>
    string StrategyId { get; }

    /// <summary>
    /// Checks whether this strategy is currently usable. MUST be
    /// side-effect free: it may not publish a port or edit compose as a
    /// side effect of checking.
    /// </summary>
    Task<bool> IsAvailableAsync(RconEndpoint endpoint, CancellationToken ct = default);

    Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default);
}
```

## Implementer Note

> `docker exec` is technically a mutating Docker API call, so the write
> guard cannot classify it by HTTP verb. It classifies by **declared
> intent** — the `readOnly` flag on each command in the game definition.
> `Info` and `ShowPlayers` pass the gate; `Save`, `Broadcast`, and `Shutdown`
> do not, even though all four travel the identical code path. On the target
> container RCON 25575 and REST 8212 are not published, so the winning
> reachability strategy is `docker-exec-tool` invoking the image's bundled
> `rcon-cli` via argv array — no port publishing, no compose edit, no
> restart.
