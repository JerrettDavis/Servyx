using System.Globalization;
using Servyx.Domain.Backups;

namespace Servyx.Web.Services;

/// <summary>
/// Where a server's backups are read from and written to, as the host understands it — the half of a
/// <c>DockerBackupContext</c> that is configuration rather than discovery.
/// </summary>
/// <remarks>
/// <para>
/// The defaults describe the <c>thijsvanloef/palworld-server-docker</c> layout this milestone adopts:
/// saves under <c>Pal/Saved</c> inside the container's data mount, the image's own cron archives in
/// <c>backups</c>, and Servyx's own archives kept beside them in a directory the image never writes to.
/// </para>
/// <para>
/// <strong><see cref="StoreDirectory"/> must be a directory nothing else writes backups into.</strong> It
/// is the one directory <c>DockerBackupProvider</c> is permitted to delete from, and pointing it at the
/// image's own backup directory would hand the cron archives to retention. It defaults to a name the
/// image does not use and is excluded from the capture set so archives are never archived into archives.
/// </para>
/// </remarks>
public sealed class BackupWiringOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionKey = "Servyx:Backups";

    /// <summary>The <see cref="Domain.Backups.RetentionPolicy"/> applied when a caller supplies none.</summary>
    public static readonly RetentionPolicy FallbackRetention = new(KeepHourly: 6, KeepDaily: 7, KeepWeekly: 4);

    /// <summary>The archive-entry prefix (and <c>BackupSource.Id</c>) for the container's data mount.</summary>
    public const string DataSourceId = "data";

    /// <summary>The container data root assumed when neither configuration nor discovery names one.</summary>
    public const string DefaultContainerDataRoot = "/palworld";

    /// <summary>The default directory Servyx writes its own archives into, relative to the data root.</summary>
    public const string DefaultStoreDirectory = "servyx-backups";

    /// <summary>The default directory the container image's own cron writes into, relative to the data root.</summary>
    public const string DefaultForeignDirectory = "backups";

    /// <summary>The default capture set: the Palworld save tree and its generated configuration.</summary>
    public static readonly string[] DefaultInclude = ["Pal/Saved/**"];

    /// <summary>Creates options.</summary>
    /// <param name="containerDataRoot">The absolute in-container data root, or null to use the adopted mount's container path.</param>
    /// <param name="storeDirectory">Root-relative directory Servyx writes its own archives into.</param>
    /// <param name="foreignDirectory">Root-relative directory the image's own cron writes into.</param>
    /// <param name="include">Root-relative globs selecting what a backup captures.</param>
    /// <param name="exclude">Root-relative globs removing paths from the capture set.</param>
    /// <param name="defaultRetention">Retention applied when a caller supplies none.</param>
    public BackupWiringOptions(
        string? containerDataRoot = null,
        string? storeDirectory = null,
        string? foreignDirectory = null,
        IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null,
        RetentionPolicy? defaultRetention = null)
    {
        ContainerDataRoot = string.IsNullOrWhiteSpace(containerDataRoot) ? null : containerDataRoot;
        StoreDirectory = string.IsNullOrWhiteSpace(storeDirectory) ? DefaultStoreDirectory : storeDirectory.Trim('/');
        ForeignDirectory = string.IsNullOrWhiteSpace(foreignDirectory) ? DefaultForeignDirectory : foreignDirectory.Trim('/');
        Include = include is { Count: > 0 } ? [.. include] : DefaultInclude;
        Exclude = exclude is null ? [] : [.. exclude];
        DefaultRetention = defaultRetention ?? FallbackRetention;

        if (string.Equals(StoreDirectory, ForeignDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // Loud at composition time. Servyx's artifact directory is the only place it may delete from;
            // aiming it at the cron's directory would make every foreign archive a prune candidate the
            // moment ownership was inferred from location rather than from discovery.
            throw new ArgumentException(
                $"'{SectionKey}:StoreDirectory' and '{SectionKey}:ForeignDirectory' must differ: Servyx deletes from "
                + "its own artifact directory, and must never be pointed at a directory another mechanism owns.",
                nameof(storeDirectory));
        }
    }

    /// <summary>The absolute in-container data root, or null to use the adopted mount's container path.</summary>
    public string? ContainerDataRoot { get; }

    /// <summary>Root-relative directory Servyx writes (and deletes) its own archives in.</summary>
    public string StoreDirectory { get; }

    /// <summary>Root-relative directory the container image's own cron writes into. Never written to by Servyx.</summary>
    public string ForeignDirectory { get; }

    /// <summary>Root-relative globs selecting what a backup captures.</summary>
    public IReadOnlyList<string> Include { get; }

    /// <summary>Root-relative globs removing paths from the capture set.</summary>
    public IReadOnlyList<string> Exclude { get; }

    /// <summary>Retention applied when a caller supplies none.</summary>
    public RetentionPolicy DefaultRetention { get; }

    /// <summary>Reads the options from <see cref="SectionKey"/>, falling back to the documented defaults.</summary>
    /// <param name="configuration">The application configuration.</param>
    public static BackupWiringOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionKey);

        return new BackupWiringOptions(
            section["ContainerDataRoot"],
            section["StoreDirectory"],
            section["ForeignDirectory"],
            ReadList(section.GetSection("Include")),
            ReadList(section.GetSection("Exclude")),
            new RetentionPolicy(
                ReadCount(section["KeepHourly"]) ?? FallbackRetention.KeepHourly,
                ReadCount(section["KeepDaily"]) ?? FallbackRetention.KeepDaily,
                ReadCount(section["KeepWeekly"]) ?? FallbackRetention.KeepWeekly));
    }

    private static IReadOnlyList<string>? ReadList(IConfigurationSection section)
    {
        var values = section.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        return values.Count == 0 ? null : values;
    }

    private static int? ReadCount(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count
            : null;
}
