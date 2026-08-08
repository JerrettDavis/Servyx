using System.Globalization;
using Servyx.Domain.Backups;

namespace Servyx.Composition;

/// <summary>
/// Where a server's backups are read from and written to, as the host understands it — the half of a
/// <c>DockerBackupContext</c> that is configuration rather than discovery.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Precedence is config &gt; loaded game definition &gt; built-in default.</strong> An explicit
/// <c>Servyx:Backups:*</c> value here always wins. Absent one, <see cref="ServyxBackupContextSource.GetAsync"/>
/// falls back to the loaded <c>GameDefinition</c>'s own <c>backup</c> block — <c>include</c>/<c>exclude</c>,
/// <c>adopt</c>, and <c>quiesce</c> — which is data-driven per game rather than hand-copied into C#. Only
/// <see cref="StoreDirectory"/>/<see cref="ForeignDirectory"/> keep a built-in constant as the final
/// fallback: those are directory names Servyx itself chooses, not knowledge about any particular game's
/// layout. There is deliberately no built-in fallback for <see cref="Include"/> or the container data root
/// beyond the adopted container's own discovered mount path — a definition that fails to load must make
/// backups fail loudly, not silently archive some other game's paths under a name that happens to match
/// Palworld's.
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

    /// <summary>
    /// The archive-entry prefix (and <c>BackupSource.Id</c>) for the host directory holding the compose
    /// files — <c>${COMPOSE_DIR}</c>-rooted entries in a definition's <c>backup.include</c>.
    /// </summary>
    public const string ComposeSourceId = "compose";

    /// <summary>The default directory Servyx writes its own archives into, relative to the data root.</summary>
    public const string DefaultStoreDirectory = "servyx-backups";

    /// <summary>The default directory the container image's own cron writes into, relative to the data root.</summary>
    public const string DefaultForeignDirectory = "backups";

    /// <summary>Creates options.</summary>
    /// <param name="containerDataRoot">The absolute in-container data root, or null to use the adopted mount's container path.</param>
    /// <param name="storeDirectory">Root-relative directory Servyx writes its own archives into.</param>
    /// <param name="foreignDirectory">Root-relative directory the image's own cron writes into.</param>
    /// <param name="include">Root-relative globs selecting what a backup captures. Empty when unset — see remarks.</param>
    /// <param name="exclude">Root-relative globs removing paths from the capture set.</param>
    /// <param name="defaultRetention">Retention applied when a caller supplies none.</param>
    /// <param name="composeDirectory">
    /// The absolute host directory holding the server's compose files, or null when not configured — in
    /// which case no <see cref="ComposeSourceId"/> source is ever built, and <c>${COMPOSE_DIR}</c>-rooted
    /// backup paths stay uncaptured exactly as they were before this option existed. Servyx cannot discover
    /// this path on its own: it names a directory on the host, outside every container filesystem.
    /// </param>
    /// <param name="quiesceCommandId">
    /// The control-channel command id to run before archiving, or null to fall back to the loaded
    /// definition's <c>backup.quiesce</c> entry, and then to <see cref="RconWiringOptions.QuiesceCommandId"/>.
    /// </param>
    /// <param name="quiesceTimeout">
    /// How long to wait for <paramref name="quiesceCommandId"/>, or null to fall back the same way.
    /// </param>
    public BackupWiringOptions(
        string? containerDataRoot = null,
        string? storeDirectory = null,
        string? foreignDirectory = null,
        IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null,
        RetentionPolicy? defaultRetention = null,
        string? composeDirectory = null,
        string? quiesceCommandId = null,
        TimeSpan? quiesceTimeout = null)
    {
        ContainerDataRoot = string.IsNullOrWhiteSpace(containerDataRoot) ? null : containerDataRoot;
        StoreDirectory = string.IsNullOrWhiteSpace(storeDirectory) ? DefaultStoreDirectory : storeDirectory.Trim('/');
        ForeignDirectory = string.IsNullOrWhiteSpace(foreignDirectory) ? DefaultForeignDirectory : foreignDirectory.Trim('/');
        Include = include is { Count: > 0 } ? [.. include] : [];
        Exclude = exclude is null ? [] : [.. exclude];
        DefaultRetention = defaultRetention ?? FallbackRetention;
        ComposeDirectory = string.IsNullOrWhiteSpace(composeDirectory) ? null : composeDirectory.TrimEnd('/', '\\');
        QuiesceCommandId = string.IsNullOrWhiteSpace(quiesceCommandId) ? null : quiesceCommandId;
        QuiesceTimeout = quiesceTimeout;

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

    /// <summary>
    /// Root-relative globs selecting what a backup captures. Empty when neither this configuration nor a
    /// loaded game definition names any — <see cref="ServyxBackupContextSource.GetAsync"/> refuses to create
    /// a backup with no capture set rather than silently writing an empty archive.
    /// </summary>
    public IReadOnlyList<string> Include { get; }

    /// <summary>Root-relative globs removing paths from the capture set.</summary>
    public IReadOnlyList<string> Exclude { get; }

    /// <summary>Retention applied when a caller supplies none.</summary>
    public RetentionPolicy DefaultRetention { get; }

    /// <summary>
    /// The absolute host directory holding the compose files, or null when not configured. See the
    /// constructor parameter of the same name.
    /// </summary>
    public string? ComposeDirectory { get; }

    /// <summary>An explicit override for the quiesce command id, or null to fall back to the definition/built-in default.</summary>
    public string? QuiesceCommandId { get; }

    /// <summary>An explicit override for the quiesce timeout, or null to fall back to the definition/built-in default.</summary>
    public TimeSpan? QuiesceTimeout { get; }

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
                ReadCount(section["KeepWeekly"]) ?? FallbackRetention.KeepWeekly),
            section["ComposeDirectory"],
            section["QuiesceCommandId"],
            ReadCount(section["QuiesceTimeoutSeconds"]) is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
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
