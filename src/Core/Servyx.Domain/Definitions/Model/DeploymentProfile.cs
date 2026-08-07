namespace Servyx.Domain.Definitions.Model;

/// <summary>How a <see cref="DeploymentProfile"/> runs its workload.</summary>
public enum DeploymentKind
{
    /// <summary>Runs as a Docker container.</summary>
    Docker,

    /// <summary>Runs as a bare-metal/native OS process.</summary>
    Process,
}

/// <summary>
/// One entry of a definition's <c>deployments</c> list: an independently-configurable way to run the same
/// game. <c>definitions/palworld-docker.yaml</c> ships two — <c>docker-thijsvanloef</c> and
/// <c>native-steamcmd</c> — each with its own <see cref="Surfaces"/> list, since the same underlying setting
/// can live on a different surface, and even in a different role, depending on the profile.
/// </summary>
/// <param name="Id">Profile identifier, e.g. <c>docker-thijsvanloef</c>.</param>
/// <param name="Kind">Whether this profile runs as a Docker container or a native process.</param>
/// <param name="Detect">How adoption recognizes an existing deployment of this kind, if declared.</param>
/// <param name="Image">The Docker image to run, for <see cref="DeploymentKind.Docker"/> profiles. Null for <see cref="DeploymentKind.Process"/> profiles.</param>
/// <param name="DataDir">The workload's data root inside the deployment, e.g. <c>/palworld</c>.</param>
/// <param name="StopTimeout">Maximum time to wait for the deployment itself (e.g. the container) to stop — distinct from the per-stage timeouts in <c>lifecycle.stop</c>.</param>
/// <param name="StopGracePeriod">
/// How long the container runtime itself must wait, after asking the workload to shut down, before it
/// force-kills. Docker's own default is ten seconds, which truncates the save of any game whose graceful
/// shutdown takes longer — so a definition whose <c>lifecycle.stop</c> ladder budgets minutes must declare
/// a matching grace period here or the runtime will SIGKILL mid-save and the ladder is decorative. Null
/// when the definition declares none, in which case the runtime's own default applies. Declared as
/// <c>stopGracePeriodSeconds</c> (whole seconds) rather than a duration string, because it maps onto an
/// integer-seconds field in every runtime that has the concept — Docker's <c>StopTimeout</c>, Compose's
/// <c>stop_grace_period</c> — and a sub-second grace period is meaningless.
/// </param>
/// <param name="Surfaces">This profile's configuration surfaces.</param>
/// <param name="Ignored">Paths that exist but are deliberately excluded from binding and backup.</param>
/// <param name="Install">Allowlisted install steps, for <see cref="DeploymentKind.Process"/> profiles. Empty for <see cref="DeploymentKind.Docker"/> profiles.</param>
/// <param name="Executable">How to launch the workload, for <see cref="DeploymentKind.Process"/> profiles. Null for <see cref="DeploymentKind.Docker"/> profiles.</param>
/// <param name="Files">
/// Files this profile seeds into the deployment's own storage before its workload is started for the very
/// first time. Empty for the overwhelming majority of profiles — see the remarks on <see cref="DeployedFile"/>
/// for the narrow class of image this exists for.
/// </param>
public sealed record DeploymentProfile(
    string Id,
    DeploymentKind Kind,
    DetectRule? Detect,
    ImageSpec? Image,
    string? DataDir,
    TimeSpan? StopTimeout,
    TimeSpan? StopGracePeriod,
    IReadOnlyList<DeclaredConfigSurface> Surfaces,
    IReadOnlyList<IgnoredPath> Ignored,
    IReadOnlyList<InstallStep> Install,
    ExecutableSpec? Executable,
    IReadOnlyList<DeployedFile> Files);

/// <summary>
/// One entry of a deployment profile's optional <c>files</c> list: content Servyx materializes into the
/// deployment's storage <em>before</em> the workload starts for the first time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all, given <c>secret:</c> refs already work.</strong> A
/// <see cref="SecretRef"/> resolves to a <em>value</em>, which the existing machinery can hand to an
/// environment variable or a config surface. Some images accept no such value: they generate a credential
/// themselves on first start, write it into a file under their data directory, and only do so when that
/// file is absent. Servyx cannot learn a credential invented inside a container it does not yet control, so
/// the only way to make one knowable is to put a known value in that file first — which is a
/// <em>file</em>-shaped operation, not a value-shaped one, and therefore cannot be expressed by a
/// <c>secret:</c> ref anywhere in the settings surface.
/// </para>
/// <para>
/// <strong>Why <see cref="CreateOnly"/> defaults to true.</strong> The same file the image would have
/// generated is, after the first start, live state the operator (or the workload) may legitimately have
/// changed. Re-seeding it on every provision would silently revert that, and would do so with a value taken
/// from a secret store the operator may have rotated in the meantime. Seeding is therefore a first-run act
/// by default, and overwriting is something a definition has to ask for explicitly.
/// </para>
/// <para>
/// <strong>Exactly one of <see cref="ContentFrom"/>/<see cref="Content"/> is set</strong>, enforced at parse
/// time. A literal <see cref="Content"/> is checked-in definition text and must never carry a credential;
/// <see cref="ContentFrom"/> is the only route for one, and it accepts only the <c>secret:</c> scheme
/// naming a declared settings key, so the value is resolved from the secret store at the moment of use and
/// never appears in the repository.
/// </para>
/// </remarks>
/// <param name="Path">
/// Where the file lands, as a template rooted at <c>${DATA_DIR}</c> or <c>${COMPOSE_DIR}</c>. Neither an
/// OS-absolute path nor any form of <c>..</c> traversal is accepted — see the parser's own containment
/// rule.
/// </param>
/// <param name="Mode">The POSIX permission bits to create the file with, as an octal string (<c>0600</c>). Defaults to <c>0600</c>.</param>
/// <param name="CreateOnly">Whether an already-present file at <see cref="Path"/> is left untouched. Defaults to <see langword="true"/>.</param>
/// <param name="ContentFrom">A <c>secret:key</c> reference whose resolved value becomes the file's content, or null when <see cref="Content"/> is declared instead.</param>
/// <param name="Content">Literal file content declared inline, or null when <see cref="ContentFrom"/> is declared instead.</param>
public sealed record DeployedFile(
    string Path,
    string Mode,
    bool CreateOnly,
    string? ContentFrom,
    string? Content);

/// <summary>A single required bind mount for <see cref="DetectRule"/>-based adoption.</summary>
/// <param name="ContainerPath">The path the mount must appear at inside the container.</param>
public sealed record RequiredMount(string ContainerPath);

/// <summary>How adoption recognizes an existing deployment of a <see cref="DeploymentProfile"/>.</summary>
/// <param name="ImageRepo">The Docker image repository an adoptable container must be running, if declared.</param>
/// <param name="RequiredMounts">Bind mounts an adoptable container must have.</param>
public sealed record DetectRule(string? ImageRepo, IReadOnlyList<RequiredMount> RequiredMounts);

/// <summary>The Docker image a <see cref="DeploymentKind.Docker"/> profile runs.</summary>
/// <param name="Default">The default image reference, e.g. <c>thijsvanloef/palworld-server-docker:latest</c>.</param>
public sealed record ImageSpec(string Default);

/// <summary>How to launch a <see cref="DeploymentKind.Process"/> profile's workload, per platform.</summary>
/// <param name="Linux">The launch command on Linux, if supported.</param>
/// <param name="Windows">The launch command on Windows, if supported.</param>
public sealed record ExecutableSpec(string? Linux, string? Windows);

/// <summary>
/// One entry of a definition's <c>ignored</c> list: a path that exists but is deliberately excluded from
/// binding and backup, with a human-readable reason shown in the UI.
/// </summary>
/// <param name="Path">The excluded path.</param>
/// <param name="Reason">Why it is excluded.</param>
public sealed record IgnoredPath(string Path, string Reason);

/// <summary>
/// One allowlisted step of a <see cref="DeploymentKind.Process"/> profile's <c>install</c> list. Closed by
/// design, not an arbitrary shell script: an <c>Unverified</c> definition has no shell surface to exploit
/// because there is no shell step to declare — see "Departures from Pterodactyl's Egg Format" in
/// <c>docs/schema.md</c>.
/// </summary>
public abstract record InstallStep
{
    private InstallStep()
    {
    }

    /// <summary>Installs or updates the workload via SteamCMD.</summary>
    /// <param name="AppId">The Steam application id to install.</param>
    /// <param name="Validate">Whether to pass SteamCMD's <c>validate</c> flag.</param>
    public sealed record SteamCmd(int AppId, bool Validate) : InstallStep;

    /// <summary>Ensures a directory exists, creating it if necessary.</summary>
    /// <param name="Path">The directory to ensure.</param>
    public sealed record EnsureDir(string Path) : InstallStep;
}
