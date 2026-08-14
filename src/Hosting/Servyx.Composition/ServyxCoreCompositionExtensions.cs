using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servyx.Application;
using Servyx.Application.Auditing;
using Servyx.Application.Backups;
using Servyx.Application.Lifecycle;
using Servyx.Application.Provisioning;
using Servyx.Application.Servers;
using Servyx.Application.Users;
using Servyx.Config;
using Servyx.Definitions;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Infrastructure.Process;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Servers;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Infrastructure.Persistence;
using Servyx.Infrastructure.Persistence.Servers;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Backups;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Composition;

/// <summary>
/// Registers the composition root every Servyx host shares: the game-definition catalog, the
/// provisioning/authentication-adjacent gates, RCON, lifecycle, backups, and persistence. Extracted out of
/// the web host's own <c>Program.cs</c> so a second host (an MCP server, coming in a later phase) can compose
/// the identical set of safety gates without duplicating them — a gate silently registered in one host and
/// not the other would be a security hole, not a convenience.
/// </summary>
public static class ServyxCoreCompositionExtensions
{
    /// <summary>
    /// Registers the shared composition root into <paramref name="builder"/>'s container and returns a
    /// <see cref="ServyxCoreComposition"/> describing what was actually wired. Deliberately NOT called
    /// <c>AddServyxWeb</c> or similar — nothing web-specific (Razor components, operator authentication,
    /// branding, the dashboard data-service selection) lives here; those stay in each host's own
    /// <c>Program.cs</c>. The caller must still call <see cref="ServyxCoreComposition.ImportSecretsAsync"/>
    /// and <see cref="ServyxCoreComposition.MigrateDatabaseAsync"/> (or the
    /// <see cref="ServyxCoreComposition.RunStartupTasksAsync"/> convenience that runs both in order) once,
    /// after <c>Build()</c>.
    /// </summary>
    /// <param name="builder">The host's builder, before <c>Build()</c> is called.</param>
    /// <param name="bootstrapLoggerFactory">
    /// Optional. Several pieces of this composition need a logger before <c>builder.Build()</c> exists —
    /// definition-catalog loading, ssh+docker wiring, RCON wiring — so they cannot resolve one from the (not
    /// yet built) DI container. When <see langword="null"/> (the default), this method creates its own
    /// console-backed <see cref="ILoggerFactory"/> for that purpose and disposes it before returning — the
    /// same behaviour this method has always had, unchanged for the web host.
    /// <para>
    /// A host whose standard output is itself a protocol channel — a stdio-transport MCP server speaking
    /// JSON-RPC over stdout, for instance — MUST supply a factory that writes to stderr (or elsewhere) here.
    /// <c>builder.Logging.ClearProviders()</c> is NOT sufficient on its own: these bootstrap loggers are
    /// constructed and used before <c>builder.Build()</c> runs, so they never go through the container's
    /// logging configuration at all, and console output from them would corrupt the protocol stream
    /// regardless of what the DI-registered logging pipeline is later configured to do. When a factory is
    /// supplied here, this method uses it for every bootstrap-phase logger and does NOT dispose it — the
    /// caller owns its lifetime, exactly as it owns everything else it constructs before calling this method.
    /// </para>
    /// </param>
    public static ServyxCoreComposition AddServyxCore(
        this IHostApplicationBuilder builder,
        ILoggerFactory? bootstrapLoggerFactory = null)
    {
        var ownsBootstrapLoggerFactory = bootstrapLoggerFactory is null;
        var resolvedBootstrapLoggerFactory = bootstrapLoggerFactory ?? LoggerFactory.Create(logging => logging.AddConsole());
        try
        {
            return AddServyxCoreCore(builder, resolvedBootstrapLoggerFactory);
        }
        finally
        {
            if (ownsBootstrapLoggerFactory)
            {
                resolvedBootstrapLoggerFactory.Dispose();
            }
        }
    }

    /// <summary>
    /// The actual composition body, factored out of <see cref="AddServyxCore(IHostApplicationBuilder, ILoggerFactory?)"/>
    /// so that method alone owns the decision of whether <paramref name="bootstrapLoggerFactory"/> was
    /// caller-supplied or created here, and therefore whether it gets disposed. Every bootstrap-phase logger
    /// this composition needs (definition-catalog loading, ssh+docker wiring, RCON wiring) is created from
    /// <paramref name="bootstrapLoggerFactory"/> rather than a locally-scoped <c>LoggerFactory.Create(...)</c>.
    /// </summary>
    private static ServyxCoreComposition AddServyxCoreCore(IHostApplicationBuilder builder, ILoggerFactory bootstrapLoggerFactory)
    {
        // ── Game definition catalog ─────────────────────────────────────────────────────────────────────
        //
        // Replaces the milestone-1 PalworldDefinitionLoader with the data-driven definition system:
        // FileSystemGameDefinitionProvider discovers *.yaml files under Servyx:Definitions:Path (defaulting to
        // {AppContext.BaseDirectory}/definitions, where the bundled definitions are copied at build time — see
        // each host's own .csproj), and GameDefinitionCatalog is the aggregate, queryable view over them.
        // AddServyxDefinitions registers the provider, the catalog, and DefinitionCatalogRefreshService — a
        // hosted service that performs one initial refresh at host startup, then optionally keeps watching for
        // hot reload in Development.
        //
        // AdoptionCriteria (needed by AddServyxApplication below) has to be known before builder.Build() runs —
        // the same constraint PalworldDefinitionLoader.TryLoad operated under — but
        // DefinitionCatalogRefreshService's own initial refresh only happens once the host actually starts, which
        // is too late for that. So this block performs one synchronous refresh itself, using a short-lived
        // bootstrap logger (the DI container isn't built yet), then replaces AddServyxDefinitions' own
        // (lazily-constructed, still-empty-until-first-resolved) provider/catalog registrations with these
        // already-populated instances — so every consumer of GameDefinitionCatalog, the hosted refresh service
        // included, shares the one catalog this synchronous refresh just populated, rather than a second instance
        // that starts empty and only catches up once hosted services run. AddServyxCore itself is synchronous —
        // the refresh is awaited via GetAwaiter().GetResult() rather than making this method (and therefore
        // every host's composition call site) async for the sake of one startup-only wait.
        builder.Services.AddServyxDefinitions(builder.Configuration);

        GameDefinitionCatalog definitionCatalog;
        GameDefinition? singleDefinition;
        IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet;
        {
            var definitionsBootstrapLogger = bootstrapLoggerFactory.CreateLogger("Servyx.Web.Startup");
            var definitionsProvider = new FileSystemGameDefinitionProvider(
                builder.Configuration[Servyx.Definitions.ServiceCollectionExtensions.PathConfigKey],
                // keep in sync with AddServyxDefinitions, which resolves IDefinitionTrustEvaluator from DI instead —
                // equivalent today, since nothing registers one, but this is a second place to update once trust
                // evaluation ships.
                trustEvaluator: null,
                bootstrapLoggerFactory.CreateLogger<FileSystemGameDefinitionProvider>());
            definitionCatalog = new GameDefinitionCatalog(
                [definitionsProvider], bootstrapLoggerFactory.CreateLogger<GameDefinitionCatalog>());
            definitionCatalog.RefreshAsync().GetAwaiter().GetResult();

            builder.Services.Replace(ServiceDescriptor.Singleton<IGameDefinitionProvider>(definitionsProvider));
            builder.Services.Replace(ServiceDescriptor.Singleton(definitionCatalog));

            // A definition that fails validation, or a directory holding more than one definition, is recorded as
            // DefinitionFault(s) (see GameDefinitionCatalog's remarks) rather than thrown — startup never crashes
            // over a malformed or ambiguous definitions/*.yaml. The /games catalogue itself (LiveDashboardDataService
            // .GetGamesAsync) renders every loaded definition as its own card, so a catalog holding more than one
            // definition is fully supported there.
            //
            // Per-server definition binding: every loaded definition with a derivable docker profile contributes its
            // own AdoptionCriteria (see AdoptionCriteriaFactory.DeriveAll below), each tagged with the exact
            // GameDefinitionRef it came from. singleDefinition below still exists so the "exactly one definition"
            // case can go on using ServerQueryService's original, single-criteria constructor — byte-identical
            // construction to before per-server binding existed, which is exactly what the characterization tests
            // pin. Two or more loaded definitions instead go through ServerQueryService's multi-definition
            // constructor, which independently resolves each discovered server to its own governing definition — see
            // ServerBindingResolver and AddServyxApplication's multi-definition overload, below. Zero definitions
            // falls into that same multi-definition path with an empty criteria set: adoption then honestly matches
            // nothing, rather than falling back to any hardcoded default game.
            singleDefinition = definitionCatalog.DefinitionsById.Count == 1
                ? definitionCatalog.DefinitionsById.Values.Single().Document as GameDefinition
                : null;

            criteriaSet = AdoptionCriteriaFactory.DeriveAll(
                definitionCatalog.DefinitionsById.Values
                    .Select(loaded => (loaded.Ref, Definition: loaded.Document as GameDefinition))
                    .Where(pair => pair.Definition is not null)
                    .Select(pair => (pair.Ref, Definition: pair.Definition!)));

            if (definitionCatalog.DefinitionsById.Count == 0)
            {
                definitionsBootstrapLogger.LogWarning(
                    "No game definitions were loaded from '{DefinitionsPath}'; there is no adoption criteria, "
                    + "lifecycle definition, settings catalogue, or RCON command catalogue available. This is an "
                    + "honest empty state, not a hardcoded fallback — nothing will be adopted until a definition "
                    + "is loaded.",
                    definitionsProvider.RootDirectory);
            }
            else if (definitionCatalog.DefinitionsById.Count > 1)
            {
                definitionsBootstrapLogger.LogWarning(
                    "{Count} game definitions were loaded ({DefinitionIds}); the /games catalogue renders all of "
                    + "them, and — unlike before per-server definition binding — adoption criteria, settings, and "
                    + "the health-signal explanation are now resolved independently for each discovered server "
                    + "against its own governing definition (see ServerBindingResolver), not against one hardcoded "
                    + "default. A server matched by more than one of these definitions with equal specificity is "
                    + "surfaced as ambiguous rather than silently assigned one. What remains single-definition-scoped: "
                    + "the RCON command catalogue and the backups quiesce integration, both still sourced from "
                    + "'singleDefinition' and therefore empty/unconfigured for every server while more than one "
                    + "definition is loaded, and ServyxServerLifecycles' stop-escalation ladder, which stays "
                    + "unconfigured the same way.",
                    definitionCatalog.DefinitionsById.Count,
                    string.Join(", ", definitionCatalog.DefinitionsById.Keys.OrderBy(id => id, StringComparer.Ordinal)));
            }
        }

        // AdoptionCriteria: derived from the catalog's single docker deployment's `detect` block — byte-identical
        // to the field values the removed AdoptionCriteria.PalworldDefault used to carry for today's bundled yaml
        // (see the characterization tests pinning this). useSingleCriteriaMode is true only when exactly one
        // definition loaded AND it has a derivable docker detect rule — the same condition that used to produce a
        // non-null result here. Every other case (zero definitions, more than one, or a malformed single one) is
        // null here, which now means "use the multi-definition binding path below" instead of falling back to any
        // hardcoded default game — see AddServyxApplication's two overloads.
        var adoptionCriteria = AdoptionCriteriaFactory.TryDerive(singleDefinition);
        var useSingleCriteriaMode = adoptionCriteria is not null;

        // Lifecycle: sourced from the same single definition's already-typed Lifecycle block — LifecycleDefinition
        // is reused verbatim by the new model (see its own remarks), so this is a source swap, not a shape change.
        // Null whenever the single-criteria path above is not taken: ServyxServerLifecycles.GetAsync then always
        // returns null and /servers/{id}'s Overview tab renders no lifecycle controls at all, rather than ones
        // wired to a ladder that does not exist. In multi-definition mode, ServerQueryService instead resolves
        // each server's own HealthSignalDefinition through IBoundDefinitionLookup — see below — but
        // ServyxServerLifecycles' stop-escalation ladder itself remains single-definition-scoped; that is
        // unchanged, larger scope this feature deliberately does not extend.
        var lifecycleDefinition = useSingleCriteriaMode ? singleDefinition?.Lifecycle : null;

        // Settings: sourced from the same single definition's already-typed Settings block — a list of
        // SettingGroup, each already in the YAML's own document order, exactly as authored under the bundled
        // definition's `settings:` key. Null by the same "single-criteria mode only" rule as
        // lifecycleDefinition above; in multi-definition mode each server's settings come from
        // IBoundDefinitionLookup instead of this ambient singleton.
        var settingGroups = useSingleCriteriaMode ? singleDefinition?.Settings : null;

        builder.Services.AddServyxDocker();

        // The default probe target a dashboard-style consumer (e.g. Servyx.Web's LiveDashboardDataService)
        // reports connection status against: the local Docker daemon AddServyxDocker() just wired up.
        // AddServyxSshDocker() below replaces this registration entirely — TargetDescriptor, ITransport,
        // IServerDiscovery, ILogStream and IMetricsSource together — when a remote host is declared, so this
        // stays the only registration in the process that reads it. Built here, not in Servyx.Web, because
        // registration order relative to AddServyxSshDocker() below is load-bearing: both calls register a
        // plain AddSingleton<TargetDescriptor>, and the later registration wins on resolution, so this must
        // run first for AddServyxSshDocker() to actually be able to override it.
        builder.Services.AddSingleton(sp => BuildDefaultDockerTarget(sp.GetRequiredService<ILoggerFactory>()));

        // ── ssh+docker: viewing a remote host's container instead of (or in addition to declaring) a local one ──
        //
        // Opt-in and empty by default: SshDockerWiringOptions.FromConfiguration returns None unless a host names
        // itself under Servyx:Hosts:<name>:Enabled with an Endpoint and a Container. Not gated behind the
        // provisioning flag — this is a read surface, registered write-guarded with zero WriteModeGrants, exactly
        // like the local Docker registration above. With nothing configured, AddServyxSshDocker is a no-op and the
        // container keeps whatever AddServyxDocker() already registered, byte-for-byte.
        //
        // A malformed host is never silently dropped: FromConfiguration logs a Warning naming the host and the
        // offending field when at least one other host is usable, and throws at startup when the section is present
        // but yields zero usable hosts — see its remarks. Both calls run before builder.Build(), so — like the
        // definition load above — they use a short-lived bootstrap logger rather than one resolved from the
        // (not yet built) DI container.
        //
        // Deliberately NOT scoped to a `{ }` block: sshDockerWiring and sshDockerLogger are still needed below, once
        // provisioningGate is known, to read SshDockerWriteModes.ReadGrants — and that reuses this same bootstrap
        // logger rather than standing up a second one for the same startup phase.
        var sshDockerLogger = bootstrapLoggerFactory.CreateLogger("Servyx.Infrastructure.Ssh.Docker.SshDockerWiring");
        var sshDockerWiring = SshDockerWiringOptions.FromConfiguration(builder.Configuration, sshDockerLogger);
        builder.Services.AddServyxSshDocker(sshDockerWiring, sshDockerLogger);

        // Single-criteria mode: AddServyxApplication's original overload, registering ServerQueryService for
        // plain DI activation exactly as before per-server binding existed — the construction path the
        // characterization tests pin for the single-definition case.
        //
        // Multi-definition mode: every other case (zero definitions, more than one, or a malformed single one).
        // IBoundDefinitionLookup is registered here so ServerQueryService's multi-definition constructor can
        // resolve a bound server's settings/lifecycle/name by content hash without Servyx.Application referencing
        // Servyx.Definitions directly — see that interface's own remarks. IServerDefinitionBindingStore is
        // resolved optionally by that same constructor; it is registered further below, once provisioningGate is
        // known, alongside the persistence wiring it needs.
        if (useSingleCriteriaMode)
        {
            builder.Services.AddServyxApplication(adoptionCriteria!);
        }
        else
        {
            builder.Services.AddSingleton<IBoundDefinitionLookup>(
                sp => new CatalogBoundDefinitionLookup(sp.GetRequiredService<GameDefinitionCatalog>()));
            builder.Services.AddServyxApplication(criteriaSet);
        }

        // Registered the same way as lifecycleDefinition below: only when a single definition actually loaded.
        // ServerQueryService (registered by AddServyxApplication above — plain DI activation, no factory lambda)
        // resolves this through its own optional settingGroups constructor parameter, so registration order
        // relative to AddServyxApplication does not matter; when unregistered, that parameter's null default
        // takes over and ServerDetail.Settings comes back empty rather than throwing.
        if (settingGroups is not null)
        {
            builder.Services.AddSingleton(settingGroups);
        }

        // ── Provisioning gate ────────────────────────────────────────────────────────────────────────────
        //
        // !! WARNING — READ BEFORE SETTING Servyx:Provisioning:Enabled TO true !!
        //
        // Setting this flag to true registers an IProvisioner in this container, which is a MUTATING,
        // MONEY-SPENDING capability: it can create real infrastructure, and at any provider other than a local
        // Docker daemon that infrastructure is billed to a real account for as long as it exists. With the
        // authentication gate open (the default), that capability belongs to whoever holds the operator
        // password. With the authentication gate CLOSED, it belongs to anyone who can reach the host —
        // which is why that exact combination is logged at Critical during startup (see StartupSafetyWarnings).
        //
        // Defaults to false whenever the key is absent, empty, or unparseable — see ProvisioningGate. When it is
        // false, nothing below the `if` runs: no IProvisioner, no dashboard service, and no /deploy nav entry
        // exist in this process, and AddServyxDocker()'s read-only registration above remains the only Docker
        // wiring, exactly as it was before this gate existed.
        var provisioningGate = ProvisioningGate.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(provisioningGate);

        // ── Per-server write grants: database-backed, live, revocable ────────────────────────────────────
        //
        // The per-server grant lives on the Server.WriteMode column and is flipped from the UI with
        // attribution (WriteModeChangedBy/At). Everything below reads from one place — WriteGrantCache — so
        // the write guard, the UI label, and the RCON control channel cannot disagree about one server.
        //
        // This replaces three separate frozen-at-startup snapshots that used to be built here:
        //   1. `foreach (grant in ServerWriteModes.ReadGrants(...)) AddSingleton(grant)` — a grant set nothing
        //      at runtime could add to or revoke from, which is why a fresh install was inert and why
        //      revoking a grant needed a process restart.
        //   2. `AddSingleton(WritableServers.FromConfiguration(...))` — the label every page reads, frozen the
        //      same way, so the READ-ONLY / WRITES ENABLED badge and every GatedButton reported the world as
        //      of process start.
        //   3. ServyxRconChannels' captured WritableServers (see its registration further below) — the same
        //      snapshot again, on an independent path.
        // Fixing only the first would have left the UI lying about the state of the second and third.
        //
        // The cache holds no context: ServyxDbContext is Scoped and this is a process-lifetime singleton
        // consumed by other singletons, so it takes IDbContextFactory<ServyxDbContext> and opens a
        // short-lived context per load — the same shape EfServerDefinitionBindingStore and EfServerRepository
        // already use. Do NOT reach for AddDbContext here; AddDbContext + AddDbContextFactory for the same
        // context type is a bug this project already hit (see AddServyxPersistence's own remarks).
        //
        // With the gate closed the cache is handed no factory at all, so a read-only host resolves every
        // target to ReadOnly without the database being opened even once. That is not an optimisation — it
        // is what keeps a read-only host's behaviour independent of whether its database is reachable.
        builder.Services.AddSingleton(sp => new WriteGrantCache(
            provisioningGate,
            provisioningGate.Enabled ? sp.GetRequiredService<IDbContextFactory<ServyxDbContext>>() : null,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WriteGrantCache>()));

        // Registered on both sides of the gate because it is a *label*, not a capability: with the gate closed
        // every server reports read-only, which is exactly true — no write grant exists in the container at
        // all. Pages resolve it unconditionally, so it must always resolve.
        builder.Services.AddSingleton(sp => WritableServers.Live(sp.GetRequiredService<WriteGrantCache>()));

        // The single IWriteModeResolver every transport in this process is guarded by. Registered with
        // Replace rather than Add: AddServyxDocker() above already TryAdd'ed a GrantedWriteModeResolver over
        // the registered WriteModeGrants, and AddServyxSshDocker()/AddServyxSsh() TryAdd the same thing, so
        // exactly one registration exists at this point and this swaps it for the database-backed one.
        // Grants for targets that are NOT on the local docker transport (ssh+docker containers, SSH backup
        // endpoints) still come from those WriteModeGrants — see DbBackedWriteModeResolver's remarks for why
        // the two sources are kept disjoint per target rather than merged.
        builder.Services.Replace(ServiceDescriptor.Singleton<IWriteModeResolver>(sp => new DbBackedWriteModeResolver(
            provisioningGate,
            sp.GetRequiredService<WriteGrantCache>(),
            new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()))));

        // The only sanctioned way a grant is created, changed, or revoked. Registered on both sides of the
        // gate so the UI can resolve it and render an honest refusal, rather than failing to resolve a
        // service; with the gate closed every call is refused before the database is touched.
        builder.Services.AddSingleton<IWriteGrantService>(sp => new WriteGrantService(
            provisioningGate,
            sp.GetRequiredService<IServerRepository>(),
            sp.GetRequiredService<WriteGrantCache>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(WriteGrantAudit.LogCategory),
            sp.GetService<TimeProvider>()));

        // ── Configuration surfaces ───────────────────────────────────────────────────────────────────
        //
        // Registered on both sides of the provisioning gate, because reading a configuration surface is a
        // read: it opens sessions and parses files, and issues no command, no write, and no RCON. What the
        // gate governs is applying a change, which is IPlanExecutor's job and does not exist yet.
        //
        // ORDER MATTERS. ServyxSurfaceResolutionContextSource is registered BEFORE AddServyxConfig() so the
        // latter's TryAdd'ed placeholders — which know about no server at all — yield to it, exactly as
        // AddServyxConfig's own remarks describe. Registered as the implementation type as well, so the
        // sessions it caches are disposed with the container.
        builder.Services.AddSingleton(sp =>
        {
            // ${COMPOSE_DIR}: the host directory where '.env' and 'compose.yaml' sit next to each other.
            // Servyx cannot discover it — it names a directory outside every container filesystem — so it is
            // opt-in under the same key the backup capture set already uses, rather than a second spelling
            // of one operator-configured fact. Read as raw keys rather than via
            // BackupWiringOptions.FromConfiguration so that composing this (always-on) block cannot start
            // throwing for a host whose backup options are misconfigured but whose backups are switched off.
            var composeDirectory = builder.Configuration[$"{BackupWiringOptions.SectionKey}:ComposeDirectory"];
            var containerDataRoot = builder.Configuration[$"{BackupWiringOptions.SectionKey}:ContainerDataRoot"];

            // Built and guarded in the same place, like every other transport in this process — see
            // ProvisionerCompositionWriteGuardTests, which fails a method that constructs a transport
            // without constructing its guard alongside. ComposeWriteModeResolver re-asks the SAME per-server
            // IWriteModeResolver the Docker transport consults, so this session is never a directory-scoped
            // grant that outlives the server's own posture.
            ITransport? composeTransport = string.IsNullOrWhiteSpace(composeDirectory)
                ? null
                : new WriteGuardedTransport(
                    new LocalProcessTransport(),
                    new ComposeWriteModeResolver(sp.GetRequiredService<IWriteModeResolver>()));

            return new ServyxSurfaceResolutionContextSource(
                // IServerDiscovery, NOT IServerQueryService. The query service optionally consumes
                // ISettingStateResolverFactory, which consumes this type, and all three are singletons —
                // so reaching back into it deadlocks the settings read against itself. Discovery is the
                // layer underneath both and answers the only question this type asks. See the type's own
                // remarks, and AddServyxCoreSettingStateReentrancyTests for the regression that proved it.
                sp.GetRequiredService<IServerDiscovery>(),
                adoptionCriteria,
                sp.GetRequiredService<ITransport>(),
                singleDefinition,
                containerDataRoot,
                composeDirectory,
                composeTransport);
        });
        builder.Services.AddSingleton<ISurfaceResolutionContextSource>(
            sp => sp.GetRequiredService<ServyxSurfaceResolutionContextSource>());
        builder.Services.AddSingleton<IServerConfigSessionSource>(
            sp => sp.GetRequiredService<ServyxSurfaceResolutionContextSource>());

        builder.Services.AddServyxConfig();

        // ── Configuration change plans (IPlanExecutor.PreviewAsync) ─────────────────────────────────────
        //
        // Registered here rather than in either Program.cs, like every other composed service in this
        // process — CompositionRootSingleSourceTests source-scans both hosts' Program.cs and fails a
        // directly-constructed composed type. Registered here rather than inside AddServyxConfig() for a
        // different reason: PlanExecutor needs an IChangePlanStore, which is a persistence concern, and
        // AddServyxConfig() is deliberately self-contained enough to be registered and container-validated
        // on its own with no database in sight.
        //
        // PreviewAsync is READ-ONLY against a game server. It opens the same read sessions the settings tab
        // already uses, computes and renders the change in memory, and writes only to Servyx's own
        // ChangePlans/ChangePlanActions tables.
        //
        // ApplyAsync DOES write to a game server — this is the one registration in this method that can — and
        // it is gated the same way everything else here is: every session it would write through is the
        // WriteGuardedExecutionTarget-wrapped session the settings tab already holds, so a server without an
        // Enabled write grant refuses the whole plan before the first byte. RevertAsync is implemented
        // (all-or-nothing preflight, read-back verified restores) but no operator surface calls it yet —
        // there is no revert button. IServerRepository is supplied because a stored plan records the tracked
        // ServerId while every session/catalogue lookup here is keyed by container id; it is the leaf
        // repository, NOT IServerQueryService, for the deadlock reason documented above.
        //
        // ChangePlanRetentionService is registered alongside, and is not optional: apply is only shippable
        // because something eventually discards the plaintext configuration images preview records. See its
        // own remarks.
        //
        // ServyxServerPlanCatalogSource is a leaf over the already-loaded singleDefinition, NOT a lookup
        // through IServerQueryService — the same rule (and the same deadlock) documented on
        // ServyxSurfaceResolutionContextSource above.
        var singleDefinitionVersion = definitionCatalog.DefinitionsById.Count == 1
            ? definitionCatalog.DefinitionsById.Values.Single().Ref.ContentHash
            : null;

        builder.Services.AddSingleton<IServerPlanCatalogSource>(
            new ServyxServerPlanCatalogSource(singleDefinition, singleDefinitionVersion));
        builder.Services.AddServyxChangePlanStore();
        builder.Services.AddSingleton<IPlanExecutor>(sp => new PlanExecutor(
            sp.GetRequiredService<IServerConfigSessionSource>(),
            sp.GetRequiredService<IServerPlanCatalogSource>(),
            sp.GetRequiredService<ISurfaceResolver>(),
            sp.GetRequiredService<IServerSettingsService>(),
            sp.GetRequiredService<IConfigMerger>(),
            sp.GetRequiredService<IChangePlanStore>(),
            sp.GetServices<IConfigAdapter>(),
            sp.GetServices<IConfigValueCodec>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<PlanExecutor>>(),
            actor: null,
            sp.GetRequiredService<IServerRepository>()));

        var changePlanRetention = ChangePlanRetentionOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(changePlanRetention);
        builder.Services.AddSingleton<ChangePlanRetentionService>(sp => new ChangePlanRetentionService(
            changePlanRetention,
            sp.GetRequiredService<IChangePlanStore>(),
            sp.GetRequiredService<ILogger<ChangePlanRetentionService>>(),
            sp.GetService<TimeProvider>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangePlanRetentionService>());

        // The old Servyx:Servers:<key>:WriteMode key is now IGNORED for adopted (local docker) servers —
        // neither honoured as an override nor imported as a seed. It is keyed by container NAME while the
        // grant is keyed by container ID, so importing it would attach write access to whatever container
        // currently answers to that name; and a config file can be stale, copied from another host, or
        // committed to a repository. Failing closed and making the operator re-grant once, in the UI, with
        // attribution, is the correct trade. Naming every ignored key is what turns that from a silent
        // behaviour change into a diagnosable one. Logged here rather than from either Program.cs so BOTH
        // hosts report it — see ServerWriteModes' own remarks for the precise scope of "ignored".
        var ignoredLegacyWriteModeKeys = ServerWriteModes.FindIgnoredLegacyKeys(builder.Configuration);
        if (ignoredLegacyWriteModeKeys.Count > 0)
        {
            sshDockerLogger.LogWarning(
                "{Count} '{SectionKey}:<server>:{WriteModeKey}' configuration key(s) are present and are NO "
                + "LONGER honoured as a write grant for adopted servers: {Keys}. The per-server grant now "
                + "lives in Servyx's database and is set from the server's page in the UI, with attribution. "
                + "These keys were deliberately not imported: they name a container by NAME, while a grant is "
                + "bound to a container's ID, so importing one could grant write access to a different "
                + "workload than the operator intended. Every server named here is READ-ONLY until it is "
                + "re-granted in the UI. (Explicitly-configured ssh+docker hosts and SSH backup endpoints "
                + "still read this key — no adoption path mints a database row for those yet.)",
                ignoredLegacyWriteModeKeys.Count,
                ServerWriteModes.SectionKey,
                ServerWriteModes.WriteModeKey,
                string.Join(", ", ignoredLegacyWriteModeKeys));
        }

        // ── Persistence, server-definition binding, and server adoption/forget ─────────────────────────────
        //
        // Registered UNCONDITIONALLY — on both sides of useSingleCriteriaMode and, further below, on both
        // sides of the provisioning gate. This used to be conditional (multi-definition mode only, or
        // single-definition mode only once the provisioning gate opened it a second way), which meant a
        // fresh, single-bundled-definition, gate-closed install — Servyx's own default configuration — never
        // registered ServyxDbContext, IServerDefinitionBindingStore, or anywhere for an adopted server to
        // live at all. That made the whole point of this phase (adopt an existing container, view it, forget
        // it) unreachable on exactly the install shape it exists for. Adoption writes ONLY to Servyx's own
        // database — it never issues a mutating command to a container — so it needs no write grant and is
        // registered here, outside the `if (provisioningGate.Enabled)` block below, on purpose.
        //
        // AddServyxPersistence's own DbContext/factory registrations are safe to call more than once with
        // the same connection string (see its own remarks); this is nonetheless now the ONLY call site for
        // it in this method — the two call sites that used to exist further below (one keyed on
        // !useSingleCriteriaMode, one nested inside the provisioning-gate block) were removed rather than
        // left as redundant duplicate calls, so there is exactly one place in this file that decides
        // "persistence is registered" and nothing to keep in sync between them.
        var persistenceConnectionString = builder.Configuration["Servyx:Persistence:ConnectionString"]
            ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "servyx-data", "servyx.db")}";

        builder.Services.AddServyxPersistence(persistenceConnectionString);
        builder.Services.AddServyxServerDefinitionBindingStore();
        builder.Services.AddServyxServerRepository();

        // ── Server status cache + background refresh ────────────────────────────────────────────────────
        //
        // Blazor Server's Home/ServersList pages used to call live discovery/metrics on every page load,
        // blocking the HTTP response for however long the transport probe took (see ServerStatusCache's own
        // remarks). ServerStatusCache is the in-memory read side LiveDashboardDataService now serves from
        // instead; ServerStatusRefreshService is the one writer, ticking on
        // ServerStatusRefreshOptions.RefreshInterval and refreshing every adopted server's status+metrics
        // entirely off the request path. Registered unconditionally, like the persistence block immediately
        // above — this is a read surface, exactly like AddServyxDocker()'s registrations near the top of this
        // method, and needs no write grant.
        //
        // Closes over adoptionCriteria/criteriaSet — the same locals AddServyxApplication was called with
        // above, in the useSingleCriteriaMode branch — rather than resolving either back out of DI, since
        // exactly one of the two is ever non-null/non-empty and both are already in scope here.
        //
        // Cache priming (loading the last-known snapshot from the database) happens in
        // ServyxCoreComposition.MigrateDatabaseAsync, immediately after WriteGrantCache.Prime() — see that
        // method's remarks — so the very first page load after a restart shows the last real read rather than
        // an empty cache.
        builder.Services.AddSingleton(sp => new ServerStatusCache(
            sp.GetRequiredService<IDbContextFactory<ServyxDbContext>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServerStatusCache>()));

        var serverStatusRefresh = ServerStatusRefreshOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(serverStatusRefresh);
        builder.Services.AddSingleton<ServerStatusRefreshService>(sp => new ServerStatusRefreshService(
            serverStatusRefresh,
            sp.GetRequiredService<IServerDiscovery>(),
            sp.GetRequiredService<IMetricsSource>(),
            sp.GetRequiredService<ServerStatusCache>(),
            sp.GetRequiredService<IDbContextFactory<ServyxDbContext>>(),
            sp.GetRequiredService<ILogger<ServerStatusRefreshService>>(),
            useSingleCriteriaMode ? adoptionCriteria : null,
            useSingleCriteriaMode ? null : criteriaSet,
            sp.GetService<TargetDescriptor>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ServyxRconChannels>(),
            settingGroups));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerStatusRefreshService>());

        // IHostRepository — durable storage behind Servyx's own host-registration bookkeeping (Increment 1).
        // Registered here, unconditionally, for the same reason as IServerRepository immediately above: this
        // is Servyx's own database, not a mutating call to any managed host, so it needs no write grant and
        // must resolve with the provisioning gate closed. AddServyxSshDocker() above resolves IHostRepository
        // lazily (inside a singleton factory, only actually invoked on first discovery call) via
        // HostConnectionRegistry — registration order relative to that earlier call does not matter, only
        // that IHostRepository is registered by the time the container is built, which this guarantees.
        builder.Services.AddServyxHostRepository();

        // IAuditEntryRepository / IAuditLogger — durable storage and the pluggable writer behind Servyx's
        // cross-cutting accountability trail. Registered here, before every write-action service that consumes
        // it (UserService, HostRegistrationService, ServerAdoptionService all take an IAuditLogger), for the
        // same reason and with the same unconditional posture as IHostRepository immediately above: this is
        // Servyx's own database, not a mutating call to any managed host or workload, so it needs no write
        // grant and must resolve with the provisioning gate closed. AuditLogger is singleton, matching
        // EfAuditEntryRepository's own lifetime — it holds no state beyond the repository reference.
        builder.Services.AddServyxAuditEntryRepository();
        builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

        // IUserRepository / IUserService — durable storage and the application-layer surface behind Servyx's
        // own account bookkeeping (Increment 1 added the entity and the store; this increment is what starts
        // consuming it). Registered here, unconditionally, for the same reason as IHostRepository immediately
        // above: this is Servyx's own database, not a mutating call to any managed host, so it needs no write
        // grant and must resolve with the provisioning gate closed — and, now, so that the login pipeline
        // itself (which has nothing to do with provisioning) can always resolve it. UserService is singleton,
        // matching EfUserRepository's own lifetime (see AddServyxUserRepository's remarks): it holds no state
        // beyond the repository reference, so one instance for the process lifetime is exactly right.
        builder.Services.AddServyxUserRepository();
        builder.Services.AddSingleton<IUserService, UserService>();

        // Desired-value persistence for the settings tab (Phase 4a). Registered unconditionally, alongside
        // the rest of this block and for the same reason: IServerSettingsService writes ONLY to Servyx's own
        // database — it never issues a command to a container (see its own remarks) — so it needs no write
        // grant and must work with the provisioning gate closed, exactly like adoption above.
        builder.Services.AddServyxServerSettingsService();

        // ── Game definition import (Phase 5) ────────────────────────────────────────────────────────────
        //
        // Import writes ONLY into Servyx's own definitions directory (Servyx:Definitions:Path) — it never
        // issues a command to a managed game server, adopted or otherwise — so, exactly like adoption and
        // the settings service immediately above, it needs no write grant and is registered here,
        // unconditionally, rather than inside the `if (provisioningGate.Enabled)` block below. Gating this
        // behind the provisioning master switch would mean a fresh install cannot teach Servyx a new game
        // without first opening the ability to mutate a server, which inverts rather than strengthens the
        // safety story — see docs/plans/ui-management-surface.md, Phase 5, "Is definition import gated?".
        // Operator authentication, enforced above this service at the host level, is the only gate.
        //
        // Resolves the same GameDefinitionCatalog singleton every other consumer in this file shares (see
        // the "Game definition catalog" block above, which replaces AddServyxDefinitions' lazily-constructed
        // registration with an already-populated instance) — a successful import calls RefreshAsync on that
        // exact instance, so every reader (this process's /games page, ServerQueryService's adoption
        // criteria, and so on) observes the new definition on its next read, not a second, disconnected
        // catalog. Uses the same Servyx:Definitions:Path configuration key FileSystemGameDefinitionProvider
        // itself reads, so an import lands exactly where the provider that will serve it back out already
        // looks.
        builder.Services.AddSingleton<IDefinitionImportService>(sp => new DefinitionImportService(
            builder.Configuration[Servyx.Definitions.ServiceCollectionExtensions.PathConfigKey],
            sp.GetRequiredService<GameDefinitionCatalog>(),
            sp.GetService<ILogger<DefinitionImportService>>()));

        // Every Server-row write drops the grant cache, structurally rather than by convention.
        //
        // AddServyxServerRepository() above registers IServerRepository -> EfServerRepository; this Replace
        // swaps that for the durable one wrapped in GrantInvalidatingServerRepository, so the ONLY
        // IServerRepository anything in this process can resolve is the invalidating one. That closes a real
        // gap rather than tidying one: ServerAdoptionService.ForgetAsync calls RemoveAsync directly, so
        // forgetting a server that held WriteMode: Enabled deleted the row while the cache went on answering
        // Enabled for that container id — and re-adopting the same container then produced a freshly-adopted,
        // never-granted server that was writable. Invalidation living in WriteGrantService alone was a
        // convention, and it had already been broken by the second caller. See
        // GrantInvalidatingServerRepository's remarks for why this is a decorator rather than a call moved
        // behind IWriteGrantService.
        //
        // EfServerRepository is named explicitly here (rather than resolved) because the interface
        // registration is the thing being replaced; it takes the same singleton-safe context factory
        // AddServyxServerRepository would have handed it.
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRepository>(sp => new GrantInvalidatingServerRepository(
            new EfServerRepository(sp.GetRequiredService<IDbContextFactory<ServyxDbContext>>()),
            sp.GetRequiredService<WriteGrantCache>())));

        // IAdoptionDefinitionCatalog is the adoption-path sibling of IBoundDefinitionLookup just below: it
        // lets ServerAdoptionService (Servyx.Application) consume the game-definition catalog without
        // Servyx.Application referencing Servyx.Definitions directly. Registered unconditionally, same as
        // the persistence block above and for the same reason.
        builder.Services.AddSingleton<IAdoptionDefinitionCatalog>(
            sp => new CatalogAdoptionDefinitionLookup(sp.GetRequiredService<GameDefinitionCatalog>()));

        // The adoption/forget surface itself. Singleton, matching IServerDefinitionBindingStore's own
        // lifetime — both of its store dependencies (EfServerRepository, EfServerDefinitionBindingStore) are
        // themselves singleton-safe (a short-lived DbContext per call via the factory, never a held scoped
        // dependency — see their own remarks).
        builder.Services.AddSingleton<IServerAdoptionService, ServerAdoptionService>();

        // ── Server lifecycle (Start/Restart/Stop/Kill) ──────────────────────────────────────────────────
        //
        // Registered unconditionally, on both sides of the provisioning gate, for the same "label vs capability"
        // reason as WritableServers immediately above: the write guard is what actually authorizes a mutating
        // call — WriteGuardedExecutionTarget for the container lifecycle verb, WriteGuardedRconSession for the
        // stop ladder's rcon stages — not whether ServyxServerLifecycles exists in this container. With the gate
        // closed (or a server carrying no WriteMode: Enabled grant), every mutating call still throws
        // WritesDisabledException at the transport exactly as it does for every other write path in this file;
        // this registration only makes the read-only half (GetStatusAsync, and rendering the definition's
        // stop-escalation ladder under WriteMode.PreviewOnly) reachable from /servers/{id}'s Overview tab on a
        // read-only host, the same way WritableServers is always registered so a page can honestly answer "is
        // this writable?" even when the answer is always no.
        //
        // ServyxRconChannels.None is registered here — also unconditionally — for the same reason: with the gate
        // closed, or no RCON channel configured, it composes no client, no secret lookup and no session, so
        // resolving it is always safe, and ServerConsoleTab degrades to "no control channel configured" rather
        // than failing to resolve a service at all. The RCON block further below overrides this registration with
        // a real, chain-composed instance whenever a channel is actually configured.
        builder.Services.AddSingleton(ServyxRconChannels.None);
        builder.Services.AddSingleton<IRconChannelResolver, ServyxRconChannelResolver>();
        builder.Services.AddSingleton<IContainerStateProbe, ServerQueryContainerStateProbe>();
        if (lifecycleDefinition is not null)
        {
            builder.Services.AddSingleton(lifecycleDefinition);
        }
        builder.Services.AddSingleton<ServyxServerLifecycles>();

        if (provisioningGate.Enabled)
        {
            // ── Per-server write mode ────────────────────────────────────────────────────────────────────
            //
            // The local-docker half of this used to live here as a loop registering one WriteModeGrant
            // singleton per Servyx:Servers:<container>:WriteMode entry. It is gone: an adopted server's grant
            // is now a database row, resolved live by DbBackedWriteModeResolver (registered above, outside
            // this block, because the resolver must exist even on a read-only host so it can answer
            // "ReadOnly" without touching the database). Nothing about enforcement moved — the guard still
            // refuses every mutating call whose target does not resolve to WriteMode.Enabled.
            //
            // The ssh+docker half of Servyx:Servers:<container>:WriteMode. It is still configuration-driven:
            // it names a container on a host the operator declared explicitly under Servyx:Hosts, and no
            // adoption path mints a Server row for one, so there is no database grant to replace it with yet.
            // A container observed over ssh+docker is reached
            // through a different transport id ("ssh+docker") and a different, single option spelling
            // ("containerName" only) — see SshDockerWriteModes' remarks for why the grant must still name
            // "ssh+docker" and not "ssh", even though SshDockerTransport rewrites the descriptor to "ssh" one layer
            // further in. With no ssh+docker host configured, this returns empty.
            foreach (var writeGrant in SshDockerWriteModes.ReadGrants(
                builder.Configuration, provisioningGate, sshDockerWiring, sshDockerLogger))
            {
                builder.Services.AddSingleton(writeGrant);
            }

            // The one call in this file that makes container creation/destruction reachable at all. It is
            // deliberately NOT part of AddServyxDocker() — see AddServyxDockerProvisioning's own remarks.
            builder.Services.AddServyxDockerProvisioning();

            // ── The remaining provisioners ───────────────────────────────────────────────────────────────
            //
            // Individually opt-in on top of this gate, and absent by default: ProvisionerWiringOptions returns None
            // unless a provisioner names itself under Servyx:Provisioners:<name>:Enabled. With nothing configured
            // the two lines below register nothing whatsoever, so an operator who sets only the gate gets exactly
            // the Docker-only composition the line above has always produced — no second provisioner, no second
            // transport, no HTTP client, no secret resolved.
            //
            // A provisioner enabled without a value it cannot be constructed without — an endpoint, a region, a
            // credential URN — fails this process at startup with the missing key named. Registering it anyway
            // would put a target on /deploy whose first click is guaranteed to fail after the operator has already
            // approved a plan; dropping it quietly would answer an explicit `Enabled = true` with silence. See
            // ProvisionerWiringOptions.FromConfiguration.
            //
            // Credentials are locators only. Every one of these keys takes a secret:// URN resolved through
            // ISecretStore at the point of use; there is no key anywhere in this block that accepts a token, a
            // password, a client secret or an AWS key.
            //
            // AddServyxConfiguredProvisioners registers no ITransport — see its remarks. That is the same hazard
            // the SSH backup block below documents: ITransport is injected singly by ServyxBackupContextSource, so
            // a second registration would silently point Docker's backups at another machine.
            var configuredProvisioners = ProvisionerWiringOptions.FromConfiguration(builder.Configuration, provisioningGate);
            builder.Services.AddSingleton(configuredProvisioners);
            builder.Services.AddServyxConfiguredProvisioners(configuredProvisioners);

            // Durable storage (ServyxDbContext) is already registered unconditionally above — see the
            // "Persistence, server-definition binding, and server adoption/forget" block. Nothing here needs
            // to register it a second time; IProvisioningLedger only needs binding below, over the context
            // that already exists. Servyx:Persistence:ConnectionString lets an operator point at a different
            // file (or a different provider-compatible connection string) without touching code; the default
            // keeps the database alongside the other on-disk state under servyx-data/, matching the
            // convention SecretsOptions already uses for secrets/host-keys (see
            // Servyx.Infrastructure.Secrets.SecretsOptions).

            // Binds IProvisioningLedger to the durable EfProvisioningLedger. A separate call from
            // AddServyxPersistence() on purpose — see that method's own remarks in
            // Servyx.Infrastructure.Persistence.ServiceCollectionExtensions.
            builder.Services.AddServyxProvisioningLedger();

            // Registers ProvisioningExecutor over the durable IProvisioningLedger registered above. This is the
            // only sanctioned route by which an IProvisioningOperation gets driven, and it is deliberately its own
            // opt-in call — see AddServyxProvisioningExecution's remarks — so the mutating path is visible here
            // rather than folded into AddServyxApplication().
            builder.Services.AddServyxProvisioningExecution();

            // The dashboard can now plan AND apply. Applying still runs through ProvisioningExecutor over the
            // durable ledger, and still refuses a plan whose hash has drifted since the user previewed it. When
            // the executor is absent the dashboard reports ExecutionConfigured == false and /deploy renders a
            // gated, non-functional Apply control rather than a live one.
            //
            // Scoped, not singleton, and it must be: both IProvisioningLedger (EfProvisioningLedger) and
            // ProvisioningExecutor are scoped because they ride on ServyxDbContext. A singleton resolving them
            // from the root provider throws "Cannot resolve scoped service from root provider" the first time
            // /deploy is opened — a failure that only appears once someone actually turns this flag on, which is
            // the worst possible moment to discover it. In Blazor Server the scope is the user's circuit, so the
            // dashboard, its ledger and its DbContext all live and die with the page the operator is looking at.
            builder.Services.AddScoped<IProvisioningDashboard>(sp => new ProvisioningDashboardService(
                sp.GetServices<IProvisioner>(),
                sp.GetService<IProvisioningLedger>(),
                sp.GetService<ProvisioningExecutor>()));

            // ── RCON control channel ─────────────────────────────────────────────────────────────────────
            //
            // Opt-in per server on top of this gate: RconWiringOptions.FromConfiguration returns Disabled unless a
            // server names itself under Servyx:Servers:<container>:Rcon:Enabled, and returns Disabled outright when
            // the gate is closed. With nothing configured, none of the lines below reads the definition file,
            // resolves a secret, or opens a socket.
            //
            // The catalogue comes from the bundled definition's control.channels[rcon].commands block and from
            // nowhere else — there is no hardcoded fallback. Every id that reaches the wire has to carry the
            // definition's own readOnly flag, because that flag is what WriteGuardedRconSession gates on; a
            // fallback written in C# would be a second, unreviewed source of truth for exactly that decision. A
            // definition that will not parse therefore yields an empty catalogue, and ServyxRconChannels refuses at
            // startup if a channel is configured against one.
            //
            // What this buys, concretely: ServyxBackupContextSource can now attach the definition's quiesce step to
            // a backup context, so DockerBackupProvider flushes the world with RCON `Save` before archiving — and
            // refuses to write an archive at all if that flush does not succeed.
            var rconWiring = RconWiringOptions.FromConfiguration(builder.Configuration, provisioningGate);
            builder.Services.AddSingleton(rconWiring);

            // Sourced from the single loaded definition's control.channels[id=rcon].commands map — the same
            // source PalworldDefinitionLoader.TryLoadRconCommands used to parse directly, now read off the
            // already-typed ControlPlane instead. A missing definition, a missing rcon channel, or a channel
            // with no commands all degrade to RconCommandCatalog.Empty, exactly like TryLoadRconCommands'
            // null return: there is still no hardcoded fallback catalogue anywhere in this codebase.
            //
            // Computed here regardless of rconWiring.Any — unlike before, a real ServyxRconChannels is worth
            // registering even when no server names itself under Servyx:Servers:<container>:Rcon:Enabled,
            // because a server adopted purely through the UI (a database-registered host, no static config at
            // all) can still get a channel ServyxRconChannels derives for itself at first use — see its own
            // remarks. Gating this whole block on rconWiring.Any, as it used to, meant a zero-static-config
            // install (this repo's own appsettings.Development.json is exactly one) registered
            // ServyxRconChannels.None and could never derive anything, no matter how many hosts were
            // registered later through the UI.
            var rconStartupLogger = bootstrapLoggerFactory.CreateLogger("Servyx.Web.Startup");

            var rconChannel = singleDefinition?.Control.Channels
                .FirstOrDefault(c => string.Equals(c.Id, PlayerListPlan.RconChannelId, StringComparison.Ordinal));
            List<RconCommand>? rconCommands = rconChannel is not null && rconChannel.Commands.Count > 0
                ? rconChannel.Commands
                    .Select(kv => new RconCommand(kv.Key, kv.Value.Template, kv.Value.ReadOnly))
                    .ToList()
                : null;

            if (rconCommands is null)
            {
                rconStartupLogger.LogWarning(
                    "No usable 'control.channels[id=rcon].commands' block was found in the loaded game "
                    + "definition(s); no RCON control-command catalogue is available.");
            }

            // A real instance is worth registering whenever there is a usable catalogue (so a channel can be
            // derived for an adopted server) OR a static channel is configured at all (rconWiring.Any) — the
            // latter kept even with an empty catalogue so ServyxRconChannels' own constructor guard still
            // fires its ArgumentException for that misconfiguration, exactly as before this change: falling
            // straight to ServyxRconChannels.None here instead would silently swallow an operator's explicit
            // Rcon:Enabled = true against a game definition with no rcon command catalogue.
            if (rconCommands is not null || rconWiring.Any)
            {
                var rconCatalog = rconCommands is null ? RconCommandCatalog.Empty : new RconCommandCatalog(rconCommands);

                // Resolves which command GetPlayersAsync invokes on the rcon channel, and how to read its reply, from
                // the same single loaded definition's control.players block. An unresolved plan is not a startup
                // refusal — it degrades GetPlayersAsync to reporting an unknown roster rather than inventing a
                // command id (e.g. "players") a particular game's dialect may not even declare.
                var rconPlayers = PlayerListPlan.Resolve(singleDefinition?.Control.Players, PlayerListPlan.RconChannelId);

                if (!rconPlayers.IsResolved)
                {
                    rconStartupLogger.LogWarning(
                        "No player-list source resolved for the '{ChannelId}' control channel: {Reason} Player listing "
                        + "over this channel will report an unknown roster rather than an empty one.",
                        PlayerListPlan.RconChannelId,
                        rconPlayers.Diagnostic);
                }

                builder.Services.AddServyxRcon();
                builder.Services.AddSingleton(sp =>
                {
                    // ISecretStore is NOT something AddServyxCore itself registers — it is wired by whichever
                    // host composes it (Servyx.Web's Program.cs, via AddServyxOperatorAuthentication, ahead of
                    // AddServyxCore) and Servyx.Mcp.Stdio deliberately never does (see
                    // SshDockerServiceCollectionExtensions' remarks on LazyBuiltTransport for the same fact
                    // documented at the transport layer). Resolved optionally rather than required: with the
                    // gate open and a usable rcon catalogue but genuinely no secret store anywhere in this
                    // process, there is no way to resolve a credential for even a statically configured
                    // channel, so this degrades to ServyxRconChannels.None exactly as a host with no channel
                    // configured at all does, rather than crashing every future resolution of
                    // ServyxBackupContextSource/ServyxRconChannels the first time anything touches it.
                    var secrets = sp.GetService<ISecretStore>();
                    if (secrets is null)
                    {
                        return ServyxRconChannels.None;
                    }

                    return new ServyxRconChannels(
                        rconWiring,
                        rconCatalog,
                        sp.GetRequiredService<IRconClient>(),
                        secrets,
                        sp.GetRequiredService<WritableServers>(),
                        // RconReachabilityChainFactory.Build composes the definition's declared strategy order —
                        // direct-tcp, docker-exec-tool, docker-exec-network — omitting docker-exec-tool when no
                        // ssh+docker host is configured, since there is then no IExecutionTarget to run `docker exec`
                        // through. sshDockerWiring is the same bootstrap-phase value AddServyxSshDocker() was already
                        // given above; reused here rather than re-read from configuration. This closure only ever
                        // runs for a STATICALLY configured channel (RconChannel.HostKey null) — ServyxRconChannels
                        // itself builds the chain for a channel it derived for an adopted server, over that
                        // server's own host, regardless of sshDockerWiring.Any; see ServyxRconChannels.BuildAsync.
                        chainFactory: channel => RconReachabilityChainFactory.Build(
                            channel,
                            sp.GetRequiredService<IRconClient>(),
                            rconCatalog,
                            secrets,
                            sshDockerWiring.Any ? sshDockerWiring.Hosts[0].ContainerName : null,
                            sshDockerWiring.Any ? sp.GetRequiredService<IExecutionTarget>() : null,
                            rconPlayers),
                        audit: null,
                        // Unconditional, same reasoning as HostAwareLogStream/HostAwareMetricsSource just above:
                        // IHostConnectionSource and IServerExecutionTargetResolver both exist regardless of
                        // sshDockerWiring.Any, because a host can be registered through the UI at any point after
                        // this DI composition already ran.
                        hostConnections: sp.GetRequiredService<IHostConnectionSource>(),
                        executionTargetResolver: sp.GetRequiredService<IServerExecutionTargetResolver>(),
                        players: rconPlayers);
                });
            }
            else
            {
                // Registered on both sides so ServyxBackupContextSource always resolves; None composes no client,
                // no secret lookup and no session, and every GetSessionAsync call returns null. With no usable
                // catalogue there is nothing a derived channel could ever invoke either, so this is not a
                // narrower fallback than before — it is the same "nothing configured" outcome, just reached for
                // a different reason (no catalogue rather than no static channel).
                builder.Services.AddSingleton(ServyxRconChannels.None);
            }

            // ── Backups ──────────────────────────────────────────────────────────────────────────────────
            //
            // The three calls below are the whole of it, and they are here — inside the gate — because every one
            // of them is mutating: creating a backup writes an archive, restoring one overwrites live save data,
            // and applying retention deletes archives. With the flag off none of these lines runs, so no
            // IBackupProvider, no IBackupDashboard and no scheduler exist in this process, and /backups renders
            // the same read-only listing it always has.
            //
            // Writing still requires a per-server grant on top of this: the sessions ServyxBackupContextSource
            // opens go through the same WriteGuardedTransport as everything else, so a server without
            // Servyx:Servers:<name>:WriteMode = Enabled refuses every write at the transport regardless of what
            // this block registered. The Backups page reads WritableServers so it can say so rather than
            // offering a control that is guaranteed to throw.
            var backupWiring = BackupWiringOptions.FromConfiguration(builder.Configuration);
            builder.Services.AddSingleton(backupWiring);

            // The seam AddServyxDockerBackups() deliberately does not default — turning a server id into a
            // container, a data root and a capture set is host knowledge. Registered as an explicit factory, not
            // AddSingleton<ServyxBackupContextSource>(), because that constructor now takes two ITransport
            // parameters (the shared Docker transport and the compose one below) and implicit constructor-injection
            // cannot tell them apart. Registered as the implementation type as well so its cached sessions are
            // disposed with the container.
            builder.Services.AddSingleton(sp =>
            {
                // The host-rooted half of the capture set: ${COMPOSE_DIR}-relative paths ('.env', 'compose.yaml')
                // that live next to the compose file, not inside the container. Servyx cannot discover that host
                // directory on its own, so it is opt-in via Servyx:Backups:ComposeDirectory; with it unset this
                // stays null and ServyxBackupContextSource never builds a second BackupSource, leaving those paths
                // uncaptured exactly as before this option existed.
                //
                // Built and guarded here, in the same place, like every other transport in this process —
                // TransportWriteGuardArchitectureTests' sibling for this file (ProvisionerCompositionWriteGuardTests)
                // asserts that construction and guarding are never split apart. The guard itself is deliberately
                // NOT a static, directory-scoped grant: ComposeWriteModeResolver re-asks the SAME per-server
                // IWriteModeResolver the shared Docker ITransport above already consults, so a ReadOnly server's
                // compose directory stays refused even though ComposeDirectory is one process-wide setting — see
                // that resolver's remarks, and ServyxBackupContextSourceWriteGuardTests for the bypass this closes
                // (a prior version granted WriteMode.Enabled for the compose directory unconditionally).
                ITransport? composeTransport = backupWiring.ComposeDirectory is not null
                    ? new WriteGuardedTransport(
                        new LocalProcessTransport(),
                        new ComposeWriteModeResolver(sp.GetRequiredService<IWriteModeResolver>()))
                    : null;

                return new ServyxBackupContextSource(
                    sp.GetRequiredService<IServerQueryService>(),
                    sp.GetRequiredService<ITransport>(),
                    backupWiring,
                    sp.GetRequiredService<ServyxRconChannels>(),
                    singleDefinition,
                    composeTransport);
            });
            builder.Services.AddSingleton<IDockerBackupContextSource>(sp => sp.GetRequiredService<ServyxBackupContextSource>());

            builder.Services.AddServyxDockerBackups();

            // ── SSH-hosted backups ───────────────────────────────────────────────────────────────────────
            //
            // Statically opt-in per server on top of this gate, and empty by default: SshBackupWiringOptions
            // returns None unless a server names itself under Servyx:Servers:<name>:Ssh:Enabled AND supplies
            // the two values nothing can be inferred from — :Host and :Root.
            //
            // Everything below is now registered UNCONDITIONALLY, regardless of sshBackups.Any — the same
            // move ServyxRconChannels' own registration made (see its remarks): a server adopted purely
            // through the UI, on a registered ssh+docker host with zero Servyx:Servers:<name>:Ssh:* ever
            // declared, still needs a route to its backups, and ServyxSshBackupContextSource now derives one
            // for exactly that server — see its own remarks on FromAdoptedAsync. Gating this block on
            // sshBackups.Any, as it used to, meant a zero-static-config install (this repo's own
            // appsettings.Development.json is exactly one) never even registered an SshBackupProvider, so an
            // adopted remote server's Backups panel had no route to reach at all, static or derived.
            //
            // AddServyxSsh() is deliberately NOT called. It registers a second ITransport, and ITransport is
            // injected singly by ServyxBackupContextSource — the Docker context source — so a second registration
            // would resolve there and point Docker's backups at an SSH host. (It also registers an IConnectorPool
            // whose factory throws pending a connector registry; not calling it sidesteps that too, and nothing
            // here needs a pooled connector.) The SSH transport for a STATICALLY configured server is therefore
            // composed inline below, inside the same WriteGuardedTransport wrapper AddServyxSsh() would have put
            // it in and over the same grants — but only when sshBackups.Any, so that ISecretStore/IHostKeyVerifier
            // are never required on a host that declared no static SSH-hosted server at all (a process with no
            // secret store — see ServyxRconChannels' own remarks — must still start). An adopted server never
            // reaches this transport: its execution target comes from IServerExecutionTargetResolver instead,
            // which is registered regardless (see ServyxSshBackupContextSource's own remarks).
            // Reuses sshDockerLogger — the same bootstrap logger already stood up for the ssh+docker wiring above —
            // so FromConfiguration can warn in-line about a declared-but-inert ForeignDirectory (see its remarks)
            // rather than that warning living as unreviewable, untestable Program.cs logic.
            var sshBackups = SshBackupWiringOptions.FromConfiguration(builder.Configuration, provisioningGate, sshDockerLogger);
            builder.Services.AddSingleton(sshBackups);

            // The SSH half of Servyx:Servers:<name>:WriteMode. The local docker half of that key emits no
            // grant at all any more (it is a database row); these are scoped to the exact endpoint the
            // session connects to, and to nothing wider. Without one, a statically-configured SSH server can
            // still be listed, inspected and dry-run pruned — only creating and restoring are refused. Empty
            // when sshBackups.Any is false, which registers nothing here — an adopted server's writes are
            // gated by WritableServers instead (see ServyxSshBackupContextSource.ContainerGrantWriteModeResolver).
            foreach (var sshGrant in sshBackups.WriteGrants)
            {
                builder.Services.AddSingleton(sshGrant);
            }

            // Registered as the implementation type as well so its cached (static-path) SSH sessions are
            // disposed with the container.
            builder.Services.AddSingleton(sp => new ServyxSshBackupContextSource(
                sshBackups,
                sshBackups.Any
                    ? new WriteGuardedTransport(
                        new SshTransport(
                            sp.GetRequiredService<ISecretStore>(),
                            sp.GetRequiredService<IHostKeyVerifier>(),
                            sp.GetRequiredService<ILoggerFactory>()),
                        new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()))
                    : null,
                sp.GetRequiredService<ServyxRconChannels>(),
                sp.GetRequiredService<IServerQueryService>(),
                sp.GetRequiredService<IServerExecutionTargetResolver>(),
                sp.GetRequiredService<WritableServers>(),
                singleDefinition));
            builder.Services.AddSingleton<ISshBackupContextSource>(
                sp => sp.GetRequiredService<ServyxSshBackupContextSource>());

            builder.Services.AddServyxSshBackups();

            // Two IBackupProviders are now always registered, and BackupDashboardService takes one. Rather
            // than let registration order decide — which would make "Docker backups still work" a property
            // of the order of two lines in this file — the dashboard is composed over an explicit router that
            // dispatches on the server the call is about. See ServyxBackupProviderRouter for why routing beats
            // keyed resolution here: half of IBackupProvider's members take an opaque backup or restore-plan
            // id that names no server, and the router is the only place that can remember who issued one. The
            // router itself falls back to a live host probe for a server not in sshBackups.ServerKeys — see
            // its own remarks — so this composes correctly whether sshBackups is empty, statically populated,
            // or both.
            builder.Services.AddSingleton<IBackupDashboard>(sp => new BackupDashboardService(
                ServyxBackupProviderRouter.FromRegistered(
                    sp.GetServices<IBackupProvider>(),
                    sshBackups.ServerKeys,
                    sp.GetRequiredService<IHostConnectionSource>())));

            // Opt-in on top of the gate: BackupScheduleOptions.FromConfiguration returns Disabled unless a
            // server names itself under Servyx:Servers:<name>:Backup:Enabled, so registering the service here
            // schedules nothing by default. It also returns Disabled whenever this gate is closed, which makes
            // the "not on a read-only host" guarantee hold even if a later edit moved this line outside the if.
            var backupSchedule = BackupScheduleOptions.FromConfiguration(builder.Configuration, provisioningGate);
            builder.Services.AddSingleton(backupSchedule);
            builder.Services.AddHostedService(sp => new ScheduledBackupService(
                backupSchedule,
                sp.GetRequiredService<ILogger<ScheduledBackupService>>(),
                sp.GetService<IBackupDashboard>(),
                sp.GetService<TimeProvider>()));
        }

        var catalogMode = definitionCatalog.DefinitionsById.Count switch
        {
            0 => DefinitionCatalogMode.None,
            1 => DefinitionCatalogMode.Single,
            _ => DefinitionCatalogMode.Multiple,
        };

        var capabilities = BuildCapabilityReport(catalogMode, definitionCatalog, provisioningGate, singleDefinition);

        return new ServyxCoreComposition(
            definitionCatalog,
            catalogMode,
            provisioningGate,
            sshDockerWiring,
            persistenceConnectionString,
            // Always true now that persistence is registered unconditionally (see the block above) — every
            // process needs its schema migrated so server adoption/forget has somewhere durable to live, not
            // only the provisioning-enabled or multi-definition cases this used to be limited to. See
            // ServyxCoreComposition.MigrateDatabaseAsync for why a failure here still does not stop startup.
            requiresDatabaseMigration: true,
            capabilities);
    }

    /// <summary>
    /// Builds the default local-Docker probe target, used by the composition root when no ssh+docker host
    /// is configured. Registered as the <see cref="TargetDescriptor"/> singleton a dashboard-style consumer
    /// is constructed with; <c>AddServyxSshDocker</c> replaces that registration entirely when a remote host
    /// is declared, so this method is never called in that case.
    /// </summary>
    /// <param name="loggerFactory">Used to report a resolution failure without crashing startup.</param>
    private static TargetDescriptor BuildDefaultDockerTarget(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Servyx.Composition.DefaultDockerTarget");

        try
        {
            var endpoint = DockerEndpointResolver.Resolve((string?)null).ToString();
            return new TargetDescriptor("docker", endpoint, null, null, new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            // Resolution itself can fail (e.g. a malformed DOCKER_HOST value); still report a named,
            // if unresolved, endpoint rather than letting startup crash over it.
            logger.LogWarning(ex, "Could not resolve a Docker endpoint to probe; connection status will report disconnected.");
            return new TargetDescriptor("docker", "(unresolved Docker endpoint)", null, null, new Dictionary<string, string>());
        }
    }

    /// <summary>
    /// Builds the <see cref="ServyxCapabilityReport"/> <see cref="AddServyxCore"/> returns, from the same
    /// facts the definition-catalog warning above already logs (see the "{Count} game definitions were
    /// loaded" message): with two or more definitions loaded, the RCON command catalogue, the backup quiesce
    /// wiring, and the stop-escalation ladder are all unconfigured for every server, because each is sourced
    /// from a single governing definition that per-server binding no longer picks for them.
    /// </summary>
    private static ServyxCapabilityReport BuildCapabilityReport(
        DefinitionCatalogMode catalogMode,
        GameDefinitionCatalog definitionCatalog,
        ProvisioningGate provisioningGate,
        GameDefinition? singleDefinition)
    {
        var loadedDefinitionIds = definitionCatalog.DefinitionsById.Keys
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        CapabilityStatus SingleDefinitionScoped(ServyxCapability capability, string featureName) => catalogMode switch
        {
            DefinitionCatalogMode.None => new CapabilityStatus(
                capability,
                Available: false,
                ReasonCode: UnavailableReason.NoDefinitionsLoaded,
                Explanation: $"{featureName} has no loaded game definition to read from.",
                Contributing: []),

            DefinitionCatalogMode.Multiple => new CapabilityStatus(
                capability,
                Available: false,
                ReasonCode: UnavailableReason.MultipleDefinitionsLoaded,
                Explanation: $"{featureName} is unconfigured for every server while {definitionCatalog.DefinitionsById.Count} "
                    + "game definitions are loaded; it remains scoped to a single loaded definition.",
                Contributing: loadedDefinitionIds),

            _ => new CapabilityStatus(capability, Available: true, ReasonCode: null, Explanation: null, Contributing: []),
        };

        var provisioningStatus = provisioningGate.Enabled
            ? new CapabilityStatus(ServyxCapability.Provisioning, true, null, null, [])
            : new CapabilityStatus(
                ServyxCapability.Provisioning,
                false,
                UnavailableReason.ProvisioningGateClosed,
                "Provisioning is closed; set Servyx:Provisioning:Enabled to unlock it.",
                []);

        var backupProviderStatus = provisioningGate.Enabled
            ? new CapabilityStatus(ServyxCapability.BackupProvider, true, null, null, [])
            : new CapabilityStatus(
                ServyxCapability.BackupProvider,
                false,
                UnavailableReason.ProvisioningGateClosed,
                "No backup provider is registered while the provisioning gate is closed.",
                []);

        CapabilityStatus saveInspectionStatus;
        if (catalogMode is DefinitionCatalogMode.None or DefinitionCatalogMode.Multiple)
        {
            saveInspectionStatus = SingleDefinitionScoped(ServyxCapability.SaveInspection, "Save inspection");
        }
        else if (singleDefinition?.Saves is null)
        {
            saveInspectionStatus = new CapabilityStatus(
                ServyxCapability.SaveInspection,
                false,
                UnavailableReason.DefinitionDeclaresNone,
                "The loaded game definition declares no 'saves' block.",
                []);
        }
        else
        {
            saveInspectionStatus = new CapabilityStatus(ServyxCapability.SaveInspection, true, null, null, []);
        }

        List<CapabilityStatus> all =
        [
            SingleDefinitionScoped(ServyxCapability.ControlCommandCatalogue, "The RCON control-command catalogue"),
            SingleDefinitionScoped(ServyxCapability.StopEscalationLadder, "The stop-escalation ladder"),
            SingleDefinitionScoped(ServyxCapability.BackupQuiesce, "The backup quiesce step"),
            backupProviderStatus,
            provisioningStatus,
            saveInspectionStatus,
        ];

        return new ServyxCapabilityReport(all);
    }
}
