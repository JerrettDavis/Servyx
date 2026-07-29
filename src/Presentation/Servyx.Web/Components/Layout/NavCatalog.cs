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
    /// The provisioning entry, which is <em>not</em> part of <see cref="Entries"/>.
    /// </summary>
    /// <remarks>
    /// Deploy is the only route in the app behind a configuration gate
    /// (<see cref="Servyx.Web.Services.ProvisioningGate.ConfigurationKey"/>), so it is kept out of the default catalog
    /// rather than filtered out of it: a host that has not opted in renders a sidebar byte-identical to the
    /// read-only one it renders today, and there is no code path in which the entry appears by default.
    /// </remarks>
    public static readonly NavEntry DeployEntry = new("Deploy", "deploy", "power");

    /// <summary>
    /// The sidebar entries for a host whose provisioning gate is in the given state — the nine read-only
    /// entries, plus <see cref="DeployEntry"/> only when provisioning has been explicitly enabled.
    /// </summary>
    /// <param name="provisioningEnabled">Whether this host's provisioning gate is open.</param>
    public static IReadOnlyList<NavEntry> EntriesFor(bool provisioningEnabled) =>
        provisioningEnabled ? [.. Entries, DeployEntry] : Entries;

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

        // Titled unconditionally: if the route rendered at all, the gate was open, and a page that renders
        // with the wrong title in the top bar is a worse failure than a title for a route nobody can reach.
        if (string.Equals(trimmed, DeployEntry.Href, StringComparison.OrdinalIgnoreCase))
        {
            return DeployEntry.Label;
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
