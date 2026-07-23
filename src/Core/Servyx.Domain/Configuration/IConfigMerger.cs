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

/// <summary>Merges a new value into an existing configuration document without disturbing content Servyx does not manage.</summary>
public interface IConfigMerger
{
    /// <summary>Produces a new <see cref="ConfigDocument"/> with <paramref name="target"/> set to <paramref name="newValue"/>.</summary>
    ConfigDocument Merge(ConfigDocument existing, ConfigPointer target, string newValue, MergePolicy policy);
}
