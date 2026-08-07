namespace Servyx.Web.Services;

/// <summary>
/// The white-label identity this deployment presents: what it calls itself, what mark it shows in the
/// sidebar, and what favicon the browser tab carries. Resolved once at startup from configuration and
/// injected wherever a page needs to say "Servyx" — or whatever an operator renamed it to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is presentation only.</strong> Nothing here changes what the process can do — no gate, no
/// grant, no capability. An operator who configures nothing gets a process that looks, byte-for-byte,
/// exactly like it always has: <see cref="Default"/> is what every page renders when
/// <c>Servyx:Branding</c> is absent.
/// </para>
/// <para>
/// <see cref="LogoAssetPath"/> and <see cref="FaviconAssetPath"/> are validated before use because they
/// become <c>src</c>/<c>href</c> attributes an operator's configuration file controls: a path containing
/// <c>..</c> or a backslash, or one that resolves outside <c>wwwroot</c>, is rejected in favour of the
/// default rather than trusted verbatim. An absolute <c>https://</c> URL — a CDN-hosted brand asset — is a
/// legitimate value and is let through unchanged; only the filesystem-path case is guarded. A rejected value
/// degrades to the default and logs a warning; it never throws at startup.
/// </para>
/// </remarks>
public sealed class BrandingOptions
{
    /// <summary>
    /// The configuration section this is read from — a sibling of <c>Servyx:DataSource</c>,
    /// <c>Servyx:Hosts</c> and <c>Servyx:Servers</c> inside the existing <c>Servyx</c> root.
    /// </summary>
    public const string SectionKey = "Servyx:Branding";

    /// <summary>The unconfigured state: Servyx's own name and mark. The safe default.</summary>
    public static readonly BrandingOptions Default = new();

    /// <summary>The product name shown in page titles, the sidebar brand word, and the login page.</summary>
    public string ProductName { get; init; } = "Servyx";

    /// <summary>A shorter form of the product name, for surfaces too narrow for the full name.</summary>
    public string ShortName { get; init; } = "Servyx";

    /// <summary>
    /// A <c>wwwroot</c>-relative path or absolute <c>https://</c> URL to a logo image, rendered in the
    /// sidebar brand mark in place of the single-letter glyph fallback when set. <see langword="null"/> keeps
    /// the glyph fallback — Servyx ships no bundled logo asset of its own.
    /// </summary>
    public string? LogoAssetPath { get; init; }

    /// <summary>
    /// A <c>wwwroot</c>-relative path or absolute <c>https://</c> URL to the favicon. Defaults to
    /// <c>favicon.png</c> — the only image Servyx bundles.
    /// </summary>
    public string FaviconAssetPath { get; init; } = "favicon.png";

    /// <summary>
    /// Reserved for a later phase: an override for the accent design token. Stored here but not consumed
    /// anywhere yet — wiring it into the CSS token system is out of scope for this change.
    /// </summary>
    public string? AccentTokenOverride { get; init; }

    /// <summary>An optional support URL a white-labelled deployment may want to surface. Not yet rendered anywhere.</summary>
    public string? SupportUrl { get; init; }

    /// <summary>
    /// Reads <see cref="SectionKey"/> from <paramref name="configuration"/>. Every field is independently
    /// optional: an absent key keeps that field's default, and a present-but-invalid asset path falls back to
    /// its default with a logged warning rather than failing startup.
    /// </summary>
    /// <param name="configuration">The application configuration to read from.</param>
    public static BrandingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionKey);

        // A short-lived bootstrap logger, exactly like the ones Program.cs stands up for the other
        // FromConfiguration calls that run before the DI container is built (see the game-definition catalog
        // bootstrap block and SshDockerWiringOptions.FromConfiguration's call sites) — there is no ILogger<T>
        // to resolve yet.
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var logger = loggerFactory.CreateLogger("Servyx.Web.Services.BrandingOptions");

        return new BrandingOptions
        {
            ProductName = FirstNonBlank(section["ProductName"], Default.ProductName) ?? Default.ProductName,
            ShortName = FirstNonBlank(section["ShortName"], Default.ShortName) ?? Default.ShortName,
            LogoAssetPath = ValidateAssetPath(section["LogoAssetPath"], Default.LogoAssetPath, nameof(LogoAssetPath), logger),
            FaviconAssetPath = ValidateAssetPath(section["FaviconAssetPath"], Default.FaviconAssetPath, nameof(FaviconAssetPath), logger)
                ?? Default.FaviconAssetPath,
            AccentTokenOverride = FirstNonBlank(section["AccentTokenOverride"], Default.AccentTokenOverride),
            SupportUrl = FirstNonBlank(section["SupportUrl"], Default.SupportUrl),
        };
    }

    private static string? FirstNonBlank(string? candidate, string? fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    /// <summary>
    /// Validates a configured asset path, falling back to <paramref name="fallback"/> — with a logged
    /// warning — for anything that is not either an absolute <c>https://</c> URL or a path that resolves
    /// to somewhere under this app's <c>wwwroot</c>.
    /// </summary>
    private static string? ValidateAssetPath(string? raw, string? fallback, string fieldName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var value = raw.Trim();

        // A CDN-hosted brand asset is a legitimate value and is not subject to the filesystem checks below.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            return value;
        }

        if (value.Contains("..", StringComparison.Ordinal) || value.Contains('\\'))
        {
            logger.LogWarning(
                "Servyx:Branding:{Field} value {Value} was rejected (contains '..' or a backslash); " +
                "falling back to the default.",
                fieldName, value);
            return fallback;
        }

        try
        {
            var wwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
            var candidate = Path.GetFullPath(Path.Combine(wwwroot, value));

            var staysUnderWwwroot =
                string.Equals(candidate, wwwroot, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(wwwroot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (!staysUnderWwwroot)
            {
                logger.LogWarning(
                    "Servyx:Branding:{Field} value {Value} resolves outside wwwroot; falling back to the default.",
                    fieldName, value);
                return fallback;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.LogWarning(
                ex,
                "Servyx:Branding:{Field} value {Value} is not a valid path; falling back to the default.",
                fieldName, value);
            return fallback;
        }

        return value;
    }
}
