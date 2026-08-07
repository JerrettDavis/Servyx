namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The parsed shape of a definition's <c>metadata</c> block: identifies the definition itself and supplies
/// presentation content for the catalogue UI.
/// </summary>
/// <remarks>
/// <see cref="Summary"/>, <see cref="Description"/>, <see cref="Vendor"/>, <see cref="DocumentationUrl"/>,
/// <see cref="Icon"/>, and <see cref="AccentColor"/> do not exist in <c>definitions/palworld-docker.yaml</c>
/// today. They are modeled ahead of the YAML gaining them in a later phase, per the accepted blueprint for
/// this refactor, so every field is nullable/optional and a definition that predates them still parses.
/// </remarks>
/// <param name="Id">
/// Stable identifier for the game, e.g. <c>palworld</c>. Servers pin definitions by content hash, not by
/// <see cref="Version"/> — see <see cref="Servyx.Domain.Definitions.GameDefinitionRef"/>.
/// </param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Version">Definition version string.</param>
/// <param name="License">License of the definition content, e.g. <c>MIT</c>.</param>
/// <param name="Tags">Free-form classification tags, e.g. <c>survival</c>, <c>steam</c>, <c>unreal</c>.</param>
/// <param name="Summary">A short, one-line description for catalogue listings. Not present in the YAML yet.</param>
/// <param name="Description">A longer, multi-paragraph description for the definition's detail page. Not present in the YAML yet.</param>
/// <param name="Vendor">The publisher of the underlying game or image, if known. Not present in the YAML yet.</param>
/// <param name="DocumentationUrl">A link to upstream documentation for the workload. Not present in the YAML yet.</param>
/// <param name="Icon">Where to source the definition's catalogue icon. Not present in the YAML yet.</param>
/// <param name="AccentColor">A CSS color used to accent this definition's catalogue card, e.g. <c>#2d6a4f</c>. Not present in the YAML yet.</param>
public sealed record GameMetadata(
    string Id,
    string Name,
    string Version,
    string? License,
    IReadOnlyList<string> Tags,
    string? Summary,
    string? Description,
    VendorRef? Vendor,
    Uri? DocumentationUrl,
    IconRef? Icon,
    string? AccentColor);

/// <summary>A reference to the publisher of a game or image, shown alongside a definition in the catalogue.</summary>
/// <param name="Name">The vendor's display name.</param>
/// <param name="Url">A link to the vendor's site, if any.</param>
public sealed record VendorRef(string Name, Uri? Url);

/// <summary>Where a definition's catalogue icon comes from.</summary>
public abstract record IconRef
{
    private IconRef()
    {
    }

    /// <summary>An icon shipped alongside the definition file itself.</summary>
    /// <param name="RelativePath">Path to the icon file, relative to the definition's own directory.</param>
    public sealed record BundleFile(string RelativePath) : IconRef;

    /// <summary>An icon fetched from an external URL.</summary>
    /// <param name="Url">The icon's absolute URL.</param>
    public sealed record Remote(Uri Url) : IconRef;
}
