using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servyx.Application;
using Servyx.Application.Backups;
using Servyx.Application.Lifecycle;
using Servyx.Application.Provisioning;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Infrastructure.Process;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Infrastructure.Persistence;
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

        // Registered on both sides of the gate because it is a *label*, not a capability: with the gate closed it
        // is WritableServers.None and every page that asks gets "read-only", which is exactly true — no write
        // grant exists in the container at all. Pages inject it unconditionally, so it must always resolve.
        builder.Services.AddSingleton(WritableServers.FromConfiguration(builder.Configuration, provisioningGate));

        // ── Server-definition binding persistence ───────────────────────────────────────────────────────
        //
        // Needed only in multi-definition mode (see useSingleCriteriaMode above) — the single-definition case has
        // no ambiguity to resolve and nothing to pin, so it never touches this. Registered here, independent of
        // the provisioning gate below (which may register the same ServyxDbContext a second time over, for the
        // provisioning ledger, if it is also enabled — AddServyxPersistence's own DbContext/factory registrations
        // are idempotent-safe to call twice with the same connection string): resolving which definition governs
        // a server is core adoption/read-path functionality, not a provisioning capability, so a read-only host
        // with more than one definition loaded still needs its bindings to survive a restart or an image retag.
        // persistenceConnectionString is reused, not recomputed, by the provisioning gate's own persistence
        // wiring further below.
        var persistenceConnectionString = builder.Configuration["Servyx:Persistence:ConnectionString"]
            ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "servyx-data", "servyx.db")}";

        if (!useSingleCriteriaMode)
        {
            builder.Services.AddServyxPersistence(persistenceConnectionString);
            builder.Services.AddServyxServerDefinitionBindingStore();
        }

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
            // The only thing in this process that can make a transport session's WriteFileAsync/DeleteAsync do
            // anything other than throw WritesDisabledException. Each grant names ONE server, read from
            // Servyx:Servers:<container>:WriteMode; a server with no entry stays ReadOnly, and there is
            // deliberately no key that enables writes for everything the daemon can see — WriteModeGrant refuses
            // to construct such a thing. Registered inside this block, so with the provisioning flag off the
            // container holds no grants and the write guard refuses everywhere, exactly as it did before M4.
            //
            // Reuses sshDockerLogger — the bootstrap logger already stood up for the ssh+docker wiring above —
            // rather than creating another LoggerFactory for the same startup phase.
            foreach (var writeGrant in ServerWriteModes.ReadGrants(builder.Configuration, provisioningGate, sshDockerLogger))
            {
                builder.Services.AddSingleton(writeGrant);
            }

            // The ssh+docker half of Servyx:Servers:<container>:WriteMode. ServerWriteModes above emits grants keyed
            // on the docker-transport container-option spellings; a container observed over ssh+docker is reached
            // through a different transport id ("ssh+docker") and a different, single option spelling
            // ("containerName" only) — see SshDockerWriteModes' remarks for why the grant must still name
            // "ssh+docker" and not "ssh", even though SshDockerTransport rewrites the descriptor to "ssh" one layer
            // further in. With no ssh+docker host configured, this returns empty exactly like ServerWriteModes does
            // with no Servyx:Servers configured.
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

            // Durable storage for the provisioning ledger. persistenceConnectionString and (when multi-definition
            // mode already needed it) the AddServyxPersistence call itself come from the "Server-definition binding
            // persistence" block above — registered here only if that block did not already run, so this never
            // registers ServyxDbContext a second time over. A read-only, single-definition host still never gets a
            // ServyxDbContext, a SQLite file, or an IProvisioningLedger in its container. Servyx:Persistence:
            // ConnectionString lets an operator point at a different file (or a different provider-compatible
            // connection string) without touching code; the default keeps the database alongside the other on-disk
            // state under servyx-data/, matching the convention SecretsOptions already uses for secrets/host-keys
            // (see Servyx.Infrastructure.Secrets.SecretsOptions).
            if (useSingleCriteriaMode)
            {
                builder.Services.AddServyxPersistence(persistenceConnectionString);
            }

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

            if (rconWiring.Any)
            {
                var rconStartupLogger = bootstrapLoggerFactory.CreateLogger("Servyx.Web.Startup");

                // Sourced from the single loaded definition's control.channels[id=rcon].commands map — the same
                // source PalworldDefinitionLoader.TryLoadRconCommands used to parse directly, now read off the
                // already-typed ControlPlane instead. A missing definition, a missing rcon channel, or a channel
                // with no commands all degrade to RconCommandCatalog.Empty, exactly like TryLoadRconCommands'
                // null return: there is still no hardcoded fallback catalogue anywhere in this codebase.
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
                builder.Services.AddSingleton(sp => new ServyxRconChannels(
                    rconWiring,
                    rconCatalog,
                    sp.GetRequiredService<IRconClient>(),
                    sp.GetRequiredService<ISecretStore>(),
                    sp.GetRequiredService<WritableServers>(),
                    // RconReachabilityChainFactory.Build composes the definition's declared strategy order —
                    // direct-tcp, docker-exec-tool, docker-exec-network — omitting docker-exec-tool when no
                    // ssh+docker host is configured, since there is then no IExecutionTarget to run `docker exec`
                    // through. sshDockerWiring is the same bootstrap-phase value AddServyxSshDocker() was already
                    // given above; reused here rather than re-read from configuration.
                    chainFactory: channel => RconReachabilityChainFactory.Build(
                        channel,
                        sp.GetRequiredService<IRconClient>(),
                        rconCatalog,
                        sp.GetRequiredService<ISecretStore>(),
                        sshDockerWiring.Any ? sshDockerWiring.Hosts[0].ContainerName : null,
                        sshDockerWiring.Any ? sp.GetRequiredService<IExecutionTarget>() : null,
                        rconPlayers),
                    audit: null));
            }
            else
            {
                // Registered on both sides so ServyxBackupContextSource always resolves; None composes no client,
                // no secret lookup and no session, and every GetSessionAsync call returns null.
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
            // Opt-in per server on top of this gate, and empty by default: SshBackupWiringOptions returns None
            // unless a server names itself under Servyx:Servers:<name>:Ssh:Enabled AND supplies the two values
            // nothing can be inferred from — :Host and :Root. With nothing configured, not one line below runs, so
            // no SSH transport is constructed, no secret is resolved, no socket is opened, and the container is
            // byte-for-byte what it was before SSH backups existed.
            //
            // AddServyxSsh() is deliberately NOT called. It registers a second ITransport, and ITransport is
            // injected singly by ServyxBackupContextSource — the Docker context source — so a second registration
            // would resolve there and point Docker's backups at an SSH host. (It also registers an IConnectorPool
            // whose factory throws pending a connector registry; not calling it sidesteps that too, and nothing
            // here needs a pooled connector.) The SSH transport is therefore composed inline below, inside the
            // same WriteGuardedTransport wrapper AddServyxSsh() would have put it in and over the same grants.
            // Reuses sshDockerLogger — the same bootstrap logger already stood up for the ssh+docker wiring above —
            // so FromConfiguration can warn in-line about a declared-but-inert ForeignDirectory (see its remarks)
            // rather than that warning living as unreviewable, untestable Program.cs logic.
            var sshBackups = SshBackupWiringOptions.FromConfiguration(builder.Configuration, provisioningGate, sshDockerLogger);
            builder.Services.AddSingleton(sshBackups);

            if (sshBackups.Any)
            {
                // The SSH half of Servyx:Servers:<name>:WriteMode. ServerWriteModes above emits grants keyed on
                // container-name descriptor options, which no SSH target carries; these are scoped to the exact
                // endpoint the session connects to, and to nothing wider. Without one, an SSH server can still be
                // listed, inspected and dry-run pruned — only creating and restoring are refused.
                foreach (var sshGrant in sshBackups.WriteGrants)
                {
                    builder.Services.AddSingleton(sshGrant);
                }

                // The seam AddServyxSshBackups() deliberately does not default. Registered as the implementation
                // type as well so its cached SSH sessions are disposed with the container.
                builder.Services.AddSingleton(sp => new ServyxSshBackupContextSource(
                    sshBackups,
                    new WriteGuardedTransport(
                        new SshTransport(
                            sp.GetRequiredService<ISecretStore>(),
                            sp.GetRequiredService<IHostKeyVerifier>(),
                            sp.GetRequiredService<ILoggerFactory>()),
                        new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>())),
                    sp.GetRequiredService<ServyxRconChannels>()));
                builder.Services.AddSingleton<ISshBackupContextSource>(
                    sp => sp.GetRequiredService<ServyxSshBackupContextSource>());

                builder.Services.AddServyxSshBackups();

                // Two IBackupProviders are now registered, and BackupDashboardService takes one. Rather than let
                // registration order decide — which would make "Docker backups still work" a property of the order
                // of two lines in this file — the dashboard is composed over an explicit router that dispatches on
                // the server the call is about. See ServyxBackupProviderRouter for why routing beats keyed
                // resolution here: half of IBackupProvider's members take an opaque backup or restore-plan id that
                // names no server, and the router is the only place that can remember who issued one.
                builder.Services.AddSingleton<IBackupDashboard>(sp => new BackupDashboardService(
                    ServyxBackupProviderRouter.FromRegistered(sp.GetServices<IBackupProvider>(), sshBackups.ServerKeys)));
            }
            else
            {
                // The unchanged path: one provider, resolved singly, exactly as before.
                builder.Services.AddServyxBackupDashboard();
            }

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
            requiresDatabaseMigration: provisioningGate.Enabled || !useSingleCriteriaMode,
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
