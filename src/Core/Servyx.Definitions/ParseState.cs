using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

/// <summary>
/// Mutable state threaded through a single <see cref="GameDefinitionYamlParser.Parse(string,string?)"/>
/// call. Exists because several semantic rules reach across blocks that appear in a different order in the
/// document than the order validation needs them in — e.g. <c>capabilities.network[].var</c> (parsed
/// before <c>settings</c>) must name a settings key (parsed after) — so those checks cannot run inline and
/// are instead queued here and resolved once the whole document has been walked. See
/// <see cref="GameDefinitionYamlParser.ResolveDeferredChecks"/>.
/// </summary>
internal sealed class ParseState(ParseIssues issues)
{
    /// <summary>
    /// The names Servyx itself supplies to every deployment, independent of any declared setting.
    /// Case-insensitive on purpose — see the remarks on <c>ValidateContainedPath</c> in
    /// <c>GameDefinitionYamlParser.Support.cs</c> for why a differently-cased reference must not be silently
    /// misclassified between this check and the path-rooting check.
    /// </summary>
    public static readonly IReadOnlySet<string> HostVariables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DATA_DIR", "COMPOSE_DIR", "INSTANCE_ID" };

    public ParseIssues Issues { get; } = issues;

    /// <summary>Every surface id declared by any deployment profile, and the format(s) it was declared with.</summary>
    public Dictionary<string, List<(string DeploymentId, SurfaceFormat Format)>> SurfacesById { get; } = new(StringComparer.Ordinal);

    /// <summary>Populated once the <c>settings</c> block is parsed; empty (not null) beforehand.</summary>
    public HashSet<string> SettingKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Populated once the <c>control.channels</c> list is parsed.</summary>
    public Dictionary<string, ControlChannelDefinition> ChannelsById { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A <c>${TOKEN}</c> reference found in a path-like or port-like scalar, queued for validation once
    /// <see cref="SettingKeys"/> is complete. <paramref name="AllowHostVariable"/> is <see langword="true"/>
    /// for path-like fields (where <see cref="HostVariables"/> are also legal) and <see langword="false"/>
    /// for a <c>port:</c> value (which may only reference a settings key).
    /// </summary>
    public List<(string Token, YamlNode Node, bool AllowHostVariable)> PendingVariableRefs { get; } = [];

    /// <summary>A <c>lifecycle.stop</c> control-stage command id, queued to check against the <c>rcon</c> channel's declared commands once <c>control</c> is parsed.</summary>
    public List<(string CommandId, YamlNode Node)> PendingStopCommandRefs { get; } = [];

    /// <summary>
    /// A <c>channel</c>/<c>command</c> pair referenced from a block parsed before (or independently of)
    /// <c>control</c> — <c>backup.quiesce[]</c> and <c>lifecycle.ready</c>'s <c>control-probe</c> entries —
    /// queued to check both the channel and the command against <see cref="ChannelsById"/> once <c>control</c>
    /// is parsed. Unlike <see cref="PendingStopCommandRefs"/>, the channel itself is not hardcoded to
    /// <c>rcon</c>, so both parts of the reference need checking here.
    /// </summary>
    public List<(string ChannelId, string CommandId, YamlNode ChannelNode, YamlNode CommandNode, string Context)> PendingChannelCommandRefs { get; } = [];

    /// <summary>
    /// A bare <c>channel</c> id referenced from a config surface's <c>control-channel</c> locator, queued to
    /// check against <see cref="ChannelsById"/> once <c>control</c> is parsed.
    /// </summary>
    public List<(string ChannelId, YamlNode ChannelNode)> PendingSurfaceChannelRefs { get; } = [];

    /// <summary>
    /// A deployment profile's declared <c>stopGracePeriodSeconds</c>, queued to check against
    /// <see cref="StopLadderTotal"/> once <c>lifecycle</c> is parsed — <c>deployments</c> is walked first, so
    /// the ladder it must cover does not exist yet at the point the field is read.
    /// </summary>
    public List<(string DeploymentId, TimeSpan GracePeriod, YamlNode Node)> PendingStopGracePeriods { get; } = [];

    /// <summary>
    /// The key half of a <c>secret:key</c> reference that must name a declared <c>settings</c> item, queued
    /// because the block declaring it (<c>deployments[].files[].contentFrom</c>) is walked before
    /// <see cref="SettingKeys"/> is populated. Distinct from <see cref="PendingVariableRefs"/>: that list
    /// resolves <c>${TOKEN}</c> interpolation, where a host-supplied variable is also a legal answer, while a
    /// secret reference may only ever name a settings key.
    /// </summary>
    public List<(string Key, YamlNode Node, string Context)> PendingSecretKeyRefs { get; } = [];

    /// <summary>
    /// The sum of every <c>lifecycle.stop</c> stage's declared timeout, set once the ladder is parsed. The
    /// terminal <c>kill</c> stage declares none and so contributes nothing. <see cref="TimeSpan.Zero"/> when
    /// the ladder is absent or every stage failed to parse.
    /// </summary>
    public TimeSpan StopLadderTotal { get; set; }
}
