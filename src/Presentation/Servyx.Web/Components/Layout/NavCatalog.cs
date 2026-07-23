namespace Servyx.Web.Components.Layout;

/// <summary>One entry in the persistent sidebar.</summary>
/// <param name="Label">Visible label, and the page title shown in the top bar when this route is active.</param>
/// <param name="Href">Route, relative to the app base.</param>
/// <param name="Icon">Which glyph <see cref="Icon"/> should render for this entry.</param>
public sealed record NavEntry(string Label, string Href, string Icon);

/// <summary>The single source of truth for the sidebar's nav entries and the top bar's page title lookup.</summary>
public static class NavCatalog
{
    public static readonly IReadOnlyList<NavEntry> Entries =
    [
        new("Dashboard", "", "dashboard"),
        new("Servers", "servers", "servers"),
        new("Games", "games", "games"),
        new("Backups", "backups", "backups"),
        new("Mods", "mods", "mods"),
        new("Plugins", "plugins", "plugins"),
        new("Settings", "settings", "settings"),
        new("Users", "users", "users"),
        new("Audit", "audit", "audit"),
    ];

    /// <summary>
    /// Finds the nav entry whose route best matches the given app-relative path (no leading slash),
    /// for use as the top bar's page title. Falls back to a generic title for sub-routes such as
    /// server detail pages.
    /// </summary>
    public static string TitleFor(string relativePath)
    {
        var trimmed = relativePath.Trim('/');

        if (trimmed.StartsWith("servers/", StringComparison.OrdinalIgnoreCase))
        {
            return "Server Detail";
        }

        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Href, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Label;
            }
        }

        return "Servyx";
    }
}
