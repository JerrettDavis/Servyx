namespace Servyx.Web.Components.Layout;

/// <summary>One entry in the persistent sidebar.</summary>
/// <param name="Label">Visible label, and the page title shown in the top bar when this route is active.</param>
/// <param name="Href">Route, relative to the app base.</param>
/// <param name="Icon">Which glyph <see cref="Icon"/> should render for this entry.</param>
/// <param name="Locked">
/// Whether this entry should render as a disabled, non-navigating affordance rather than a live link.
/// Defaults to <see langword="false"/> — every entry is live unless a caller explicitly locks it. The
/// product invariant is "visible but locked", never "hidden", so a gated route stays in this list at all
/// times; only <see cref="Locked"/> changes.
/// </param>
public sealed record NavEntry(string Label, string Href, string Icon, bool Locked = false);

/// <summary>The single source of truth for the sidebar's nav entries and the top bar's page title lookup.</summary>
public static class NavCatalog
{
    public static readonly IReadOnlyList<NavEntry> Entries =
    [
        new("Dashboard", "", "dashboard"),
        new("Servers", "servers", "servers"),
        new("Hosts", "hosts", "hosts"),
        new("Games", "games", "games"),
        new("Backups", "backups", "backups"),
        new("Mods", "mods", "mods"),
        new("Plugins", "plugins", "plugins"),
        new("Settings", "settings", "settings"),
        new("Users", "users", "users"),
        new("Audit", "audit", "audit"),
    ];

    /// <summary>
    /// The provisioning entry, which is <em>not</em> part of <see cref="Entries"/> but is always appended by
    /// <see cref="EntriesFor"/>, live or locked depending on the gate.
    /// </summary>
    /// <remarks>
    /// Deploy is the only route in the app behind a configuration gate
    /// (<see cref="Servyx.Web.Services.ProvisioningGate.ConfigurationKey"/>). Earlier, a closed gate removed
    /// this entry from the sidebar entirely, which contradicted the product's own "visible but locked"
    /// invariant — a fresh install could not even discover that provisioning exists. It is now always
    /// present; <see cref="EntriesFor"/> only ever toggles <see cref="NavEntry.Locked"/>.
    /// </remarks>
    public static readonly NavEntry DeployEntry = new("Deploy", "deploy", "power");

    /// <summary>
    /// Explains why the Deploy entry is locked when the gate is closed. Named explicitly rather than reusing
    /// <c>GatedButton.DefaultReason</c>: this is a process-level configuration switch, not a per-server write
    /// grant, and the remedy is different — editing configuration and restarting the host, not a click
    /// anywhere in this UI.
    /// </summary>
    public const string DeployLockedReason =
        "Deploying and provisioning managed servers requires an operator to set Servyx:Provisioning:Enabled " +
        "to true in configuration and restart the host. This is a process-level switch, not a per-server " +
        "write grant, and nothing in this UI can change it.";

    /// <summary>
    /// The sidebar entries for a host whose provisioning gate is in the given state — the nine read-only
    /// entries, plus <see cref="DeployEntry"/> always, locked unless provisioning has been explicitly
    /// enabled.
    /// </summary>
    /// <param name="provisioningEnabled">Whether this host's provisioning gate is open.</param>
    public static IReadOnlyList<NavEntry> EntriesFor(bool provisioningEnabled) =>
        [.. Entries, provisioningEnabled ? DeployEntry : DeployEntry with { Locked = true }];

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

        // Titled unconditionally, gate open or closed: DeployPage renders safely either way, and a page that
        // renders with the wrong title in the top bar is a worse failure than a title for a route someone
        // reached by a direct URL rather than the (possibly locked) sidebar entry.
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
