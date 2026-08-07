using Servyx.Domain.Lifecycle;

namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The typed shape of a <c>servyx.dev/v1</c> <c>GameDefinition</c> document — the full parse of a file like
/// <c>definitions/palworld-docker.yaml</c>, covering every top-level block described in
/// <c>docs/schema.md</c>.
/// </summary>
/// <remarks>
/// Pure data: no YAML/parsing concerns live here or anywhere under <c>Servyx.Domain</c>, so this type and
/// everything it references can be consumed by <c>Servyx.Application</c> and <c>Servyx.Web</c> with zero
/// YamlDotNet dependency. Producing a <see cref="GameDefinition"/> from a YAML file, and validating one, are
/// a separate project's job — see <see cref="Servyx.Domain.Definitions.IGameDefinitionProvider"/> and
/// <see cref="Servyx.Domain.Definitions.IGameDefinitionValidator"/>.
/// </remarks>
/// <param name="ApiVersion">
/// The schema version the document declares, e.g. <c>servyx.dev/v1</c>. The document's <c>kind:
/// GameDefinition</c> discriminator is not modeled as a field — it never varies for this shape, so there is
/// nothing for a field to carry beyond what the type itself already says.
/// </param>
/// <param name="Metadata">The <c>metadata</c> block: identity and presentation.</param>
/// <param name="Capabilities">The <c>capabilities</c> block: the declared blast radius, checked against trust tier restrictions before anything runs.</param>
/// <param name="Deployments">The <c>deployments</c> block: one or more independently-configurable ways to run this game.</param>
/// <param name="Lifecycle">
/// The <c>lifecycle</c> block. Reuses the existing <see cref="LifecycleDefinition"/> rather than a second
/// parsed-lifecycle shape — see the remarks there.
/// </param>
/// <param name="Control">The <c>control</c> block: control channels and the commands/endpoints exposed over each.</param>
/// <param name="Settings">The <c>settings</c> block: the user-facing settings catalogue, organized into groups.</param>
/// <param name="Backup">The <c>backup</c> block: what a Servyx-created backup archives, quiesce steps, adoption of foreign backups, and default retention.</param>
/// <param name="Saves">The <c>saves</c> block, if the definition declares one: the on-disk shape of world saves.</param>
/// <param name="Mods">The <c>mods</c> block: whether mod management is supported for this definition at all.</param>
public sealed record GameDefinition(
    string ApiVersion,
    GameMetadata Metadata,
    Capabilities Capabilities,
    IReadOnlyList<DeploymentProfile> Deployments,
    LifecycleDefinition Lifecycle,
    ControlPlane Control,
    IReadOnlyList<SettingGroup> Settings,
    BackupPolicy Backup,
    SavesLayout? Saves,
    ModsPolicy Mods);
