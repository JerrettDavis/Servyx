namespace Servyx.Domain.Configuration;

/// <summary>
/// Policy governing how unmanaged content is treated on write. There is deliberately no "rewrite whole
/// file" policy — every write must go through one of these two, both of which preserve everything Servyx
/// does not explicitly own.
/// </summary>
public enum MergePolicy
{
    /// <summary>Default, and effectively non-negotiable. Unmanaged keys are never touched, reordered, or reformatted.</summary>
    PreserveUnknown,

    /// <summary>
    /// Writes are confined to a delimited region (<c># >>> servyx:managed >>></c> …
    /// <c># &lt;&lt;&lt; servyx:managed &lt;&lt;&lt;</c>) within an otherwise unstructured file.
    /// </summary>
    ManagedBlock,
}

/// <summary>A single value replacement within a batch of edits applied via <see cref="IConfigMerger.MergeAll"/>.</summary>
/// <param name="Target">The value to replace.</param>
/// <param name="NewValue">The replacement text.</param>
public sealed record ConfigEdit(ConfigPointer Target, string NewValue);

/// <summary>Merges a new value into an existing configuration document without disturbing content Servyx does not manage.</summary>
public interface IConfigMerger
{
    /// <summary>Produces a new <see cref="ConfigDocument"/> with <paramref name="target"/> set to <paramref name="newValue"/>.</summary>
    ConfigDocument Merge(ConfigDocument existing, ConfigPointer target, string newValue, MergePolicy policy);

    /// <summary>
    /// Applies every edit in <paramref name="edits"/> to <paramref name="existing"/> in a single pass.
    /// Edits that target members packed inside the same codec-encoded scalar (see
    /// <see cref="IConfigValueCodec"/>) are grouped so that scalar is decoded and re-encoded exactly once,
    /// regardless of how many of its members are being changed — decoding/encoding once per edit would be
    /// needlessly quadratic for formats like Palworld's <c>OptionSettings</c>, which packs roughly 90
    /// settings into a single scalar.
    /// </summary>
    ConfigDocument MergeAll(ConfigDocument existing, IReadOnlyList<ConfigEdit> edits, MergePolicy policy);
}
