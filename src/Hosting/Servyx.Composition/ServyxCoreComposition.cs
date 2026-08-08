using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Servyx.Application;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Persistence;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Composition;

/// <summary>
/// Which of the three definition-loading outcomes <see cref="ServyxCoreComposition.DefinitionCatalog"/>
/// landed in at startup — the same three-way split <c>Program.cs</c>'s <c>useSingleCriteriaMode</c> used to
/// encode as a bare <see langword="bool"/> plus a nullable definition. Exposed as its own enum here so a
/// second host can branch on it without re-deriving it from <see cref="GameDefinitionCatalog.DefinitionsById"/>.
/// </summary>
public enum DefinitionCatalogMode
{
    /// <summary>No game definition was loaded at all. Adoption honestly matches nothing.</summary>
    None,

    /// <summary>
    /// Exactly one game definition was loaded. Every single-definition-scoped feature (the RCON command
    /// catalogue, the backup quiesce step, the stop-escalation ladder, save inspection) is available.
    /// </summary>
    Single,

    /// <summary>
    /// Two or more game definitions were loaded. Per-server adoption/settings/lifecycle resolve
    /// independently through <see cref="IBoundDefinitionLookup"/>, but the single-definition-scoped
    /// features above are unconfigured for every server — see <see cref="ServyxCapabilityReport"/>.
    /// </summary>
    Multiple,
}

/// <summary>
/// A capability whose availability depends on how many game definitions loaded, or on which optional,
/// opt-in composition-root wiring is actually configured — see <see cref="ServyxCapabilityReport"/>.
/// </summary>
public enum ServyxCapability
{
    /// <summary>The RCON control-command catalogue sourced from a single definition's <c>control.channels[id=rcon].commands</c> block.</summary>
    ControlCommandCatalogue,

    /// <summary>The definition-declared stop-escalation ladder <c>ServyxServerLifecycles</c> drives.</summary>
    StopEscalationLadder,

    /// <summary>The definition-declared RCON quiesce step a backup context attaches before archiving.</summary>
    BackupQuiesce,

    /// <summary>Whether any <c>IBackupProvider</c> is registered at all (Docker and/or SSH-hosted backups).</summary>
    BackupProvider,

    /// <summary>Whether the provisioning gate is open, making mutating infrastructure operations reachable.</summary>
    Provisioning,

    /// <summary>Definition-driven, read-only inspection of a server's save world — see <c>ServerSavesReader</c>.</summary>
    SaveInspection,
}

/// <summary>
/// Stable reason codes a <see cref="CapabilityStatus"/> carries when its capability is unavailable — a small,
/// closed vocabulary rather than free-text, so a caller (a UI, an MCP tool description) can branch on the
/// reason without parsing <see cref="CapabilityStatus.Explanation"/>.
/// </summary>
public static class UnavailableReason
{
    /// <summary>No game definition loaded at all — see <see cref="DefinitionCatalogMode.None"/>.</summary>
    public const string NoDefinitionsLoaded = "no-definitions-loaded";

    /// <summary>
    /// Two or more game definitions loaded — see <see cref="DefinitionCatalogMode.Multiple"/>. The
    /// single-definition-scoped features this reason applies to are unconfigured for every server, not
    /// merely ambiguous for some.
    /// </summary>
    public const string MultipleDefinitionsLoaded = "multiple-definitions-loaded";

    /// <summary>The provisioning gate (<c>Servyx:Provisioning:Enabled</c>) is closed.</summary>
    public const string ProvisioningGateClosed = "provisioning-gate-closed";

    /// <summary>No server-specific configuration opted this capability in.</summary>
    public const string NotConfiguredForServer = "not-configured-for-server";

    /// <summary>No implementation is registered in the container for this capability.</summary>
    public const string NoProviderRegistered = "no-provider-registered";

    /// <summary>The wired transport does not support what this capability needs.</summary>
    public const string TransportUnsupported = "transport-unsupported";

    /// <summary>The governing game definition explicitly declares no support for this capability.</summary>
    public const string DefinitionDeclaresNone = "definition-declares-none";
}

/// <summary>One capability's availability, why it is (or is not) available, and what facts drove that answer.</summary>
/// <param name="Capability">Which capability this reports on.</param>
/// <param name="Available">Whether it is usable in this process, right now.</param>
/// <param name="ReasonCode">One of <see cref="UnavailableReason"/>'s constants, present only when <paramref name="Available"/> is <see langword="false"/>.</param>
/// <param name="Explanation">A human-readable sentence expanding on <paramref name="ReasonCode"/> — game-neutral, safe to surface to an operator or an MCP client verbatim.</param>
/// <param name="Contributing">
/// Supporting facts — e.g. the ids of every game definition that made a multi-definition capability
/// unavailable. Empty when nothing beyond <paramref name="ReasonCode"/> itself is needed.
/// </param>
public sealed record CapabilityStatus(
    ServyxCapability Capability,
    bool Available,
    string? ReasonCode,
    string? Explanation,
    IReadOnlyList<string> Contributing);

/// <summary>
/// The full set of <see cref="CapabilityStatus"/> entries this process computed at startup — one per
/// <see cref="ServyxCapability"/> value. Built once by <see cref="ServyxCoreCompositionExtensions.AddServyxCore"/>
/// and exposed read-only through <see cref="ServyxCoreComposition.Capabilities"/> so every host (the web
/// dashboard today, an MCP server later) can answer "what can this process actually do right now" from the
/// same facts, rather than each re-deriving it from configuration on its own.
/// </summary>
public sealed class ServyxCapabilityReport
{
    private readonly IReadOnlyDictionary<ServyxCapability, CapabilityStatus> _byCapability;

    internal ServyxCapabilityReport(IReadOnlyList<CapabilityStatus> all)
    {
        All = all;
        _byCapability = all.ToDictionary(status => status.Capability);
    }

    /// <summary>Every capability's status, one entry per <see cref="ServyxCapability"/> value.</summary>
    public IReadOnlyList<CapabilityStatus> All { get; }

    /// <summary>Looks up one capability's status. Every <see cref="ServyxCapability"/> value is guaranteed to be present.</summary>
    public CapabilityStatus Get(ServyxCapability capability) => _byCapability[capability];
}

/// <summary>
/// The shared composition root's own handle on what it built: the loaded definition catalog, the
/// provisioning/ssh-docker wiring decisions, the persistence connection string, and a
/// <see cref="ServyxCapabilityReport"/> summarizing what is and is not usable. Returned by
/// <see cref="ServyxCoreCompositionExtensions.AddServyxCore"/> so the calling host can finish its own,
/// host-specific wiring (authentication, presentation, an MCP transport) with the facts this already
/// established, and so it can drive <see cref="RunStartupTasksAsync"/> after <c>Build()</c>.
/// </summary>
public sealed class ServyxCoreComposition
{
    internal ServyxCoreComposition(
        GameDefinitionCatalog definitionCatalog,
        DefinitionCatalogMode catalogMode,
        ProvisioningGate provisioning,
        SshDockerWiringOptions sshDocker,
        string persistenceConnectionString,
        bool requiresDatabaseMigration,
        ServyxCapabilityReport capabilities)
    {
        DefinitionCatalog = definitionCatalog;
        CatalogMode = catalogMode;
        Provisioning = provisioning;
        SshDocker = sshDocker;
        PersistenceConnectionString = persistenceConnectionString;
        RequiresDatabaseMigration = requiresDatabaseMigration;
        Capabilities = capabilities;
    }

    /// <summary>The data-driven game-definition catalog every consumer of adoption/settings/lifecycle/RCON data reads from.</summary>
    public GameDefinitionCatalog DefinitionCatalog { get; }

    /// <summary>Which of the three definition-loading outcomes <see cref="DefinitionCatalog"/> landed in — see <see cref="DefinitionCatalogMode"/>.</summary>
    public DefinitionCatalogMode CatalogMode { get; }

    /// <summary>Whether the provisioning gate is open — see <c>ProvisioningGate</c>'s own remarks for what that unlocks.</summary>
    public ProvisioningGate Provisioning { get; }

    /// <summary>The resolved ssh+docker wiring, so a caller can read which (if any) remote host is configured.</summary>
    public SshDockerWiringOptions SshDocker { get; }

    /// <summary>
    /// The connection string persistence was configured with — <c>Servyx:Persistence:ConnectionString</c>,
    /// or the default <c>servyx-data/servyx.db</c> SQLite file — regardless of whether persistence was
    /// actually registered in this process (see <see cref="RequiresDatabaseMigration"/> for that).
    /// </summary>
    public string PersistenceConnectionString { get; }

    /// <summary>
    /// Whether this process registered <c>ServyxDbContext</c> at all — true when the provisioning gate is
    /// open, or when more than one game definition loaded (server-definition bindings need somewhere durable
    /// to live). <see cref="RunStartupTasksAsync"/> only migrates the schema when this is true.
    /// </summary>
    public bool RequiresDatabaseMigration { get; }

    /// <summary>The capability report this composition computed at startup — see <see cref="ServyxCapabilityReport"/>.</summary>
    public ServyxCapabilityReport Capabilities { get; }

    /// <summary>
    /// Config-driven, startup-only write path into <see cref="ISecretStore"/> (e.g. importing an SSH private
    /// key) — see <see cref="SecretImport"/>'s remarks. <see cref="ISecretStore"/> is resolved optionally:
    /// registered unconditionally today, but this stays safe if that ever changes.
    /// </summary>
    public async Task ImportSecretsAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var startupLog = services.GetRequiredService<ILoggerFactory>().CreateLogger("Servyx.Web.Startup");

        var configuration = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (services.GetService<ISecretStore>() is { } secretStore)
        {
            await SecretImport.RunAsync(configuration, secretStore, startupLog).ConfigureAwait(false);
        }
        else
        {
            startupLog.LogWarning("Servyx:Secrets:Import was not processed: no ISecretStore is registered.");
        }
    }

    /// <summary>
    /// Applies the EF migration when (and only when) this process actually registered persistence — see
    /// <see cref="RequiresDatabaseMigration"/>.
    /// </summary>
    /// <remarks>
    /// Migrations are applied here — called only after <c>Build()</c> — rather than inside
    /// <c>AddServyxPersistence()</c>. Registration stays side-effect-free and testable in isolation (a test
    /// fixture can compose the container without anything touching disk); migrating the schema is an
    /// explicit, startup-time action that should only ever happen once persistence was actually registered —
    /// either because provisioning is enabled, or because multi-definition mode registered it for the
    /// server-definition binding store. With <see cref="RequiresDatabaseMigration"/> false, this method
    /// returns without touching the database file.
    /// </remarks>
    public Task MigrateDatabaseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        if (RequiresDatabaseMigration)
        {
            using var migrationScope = services.CreateScope();
            migrationScope.ServiceProvider.GetRequiredService<ServyxDbContext>().Database.Migrate();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The post-<c>Build()</c> half of composition: <see cref="ImportSecretsAsync"/> followed by
    /// <see cref="MigrateDatabaseAsync"/>, for a host that does not need to run anything between the two.
    /// </summary>
    /// <remarks>
    /// The web host does NOT use this convenience method — see its <c>Program.cs</c> call site. It needs
    /// <c>StartupSafetyWarnings.LogDangerousCombinations</c> to run between secret import and
    /// migration, so that an operator running with a dangerous configuration (e.g. provisioning enabled while
    /// authentication is disabled) sees that Critical-level warning even if <see cref="MigrateDatabaseAsync"/>
    /// subsequently throws — a locked database file, a permissions error, or a drifted schema must never be
    /// able to swallow a safety warning that would otherwise already be in the operator's log. Hosts with no
    /// such interleaving requirement (the MCP stdio host, for instance) can call this method instead.
    /// </remarks>
    public async Task RunStartupTasksAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await ImportSecretsAsync(services, ct).ConfigureAwait(false);
        await MigrateDatabaseAsync(services, ct).ConfigureAwait(false);
    }
}
