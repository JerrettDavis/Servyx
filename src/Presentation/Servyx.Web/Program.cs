using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using Servyx.Web.Authentication;
using Servyx.Web.Components;
using Servyx.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Game definition catalog ─────────────────────────────────────────────────────────────────────
//
// Replaces the milestone-1 PalworldDefinitionLoader with the data-driven definition system:
// FileSystemGameDefinitionProvider discovers *.yaml files under Servyx:Definitions:Path (defaulting to
// {AppContext.BaseDirectory}/definitions, where the bundled definitions/palworld-docker.yaml is copied
// at build time — see this project's own .csproj), and GameDefinitionCatalog is the aggregate, queryable
// view over them. AddServyxDefinitions registers the provider, the catalog, and
// DefinitionCatalogRefreshService — a hosted service that performs one initial refresh at host startup,
// then optionally keeps watching for hot reload in Development.
//
// AdoptionCriteria (needed by AddServyxApplication below) has to be known before builder.Build() runs —
// the same constraint PalworldDefinitionLoader.TryLoad operated under — but
// DefinitionCatalogRefreshService's own initial refresh only happens once the host actually starts, which
// is too late for that. So this block performs one synchronous refresh itself, using a short-lived
// bootstrap logger (the DI container isn't built yet), then replaces AddServyxDefinitions' own
// (lazily-constructed, still-empty-until-first-resolved) provider/catalog registrations with these
// already-populated instances — so every consumer of GameDefinitionCatalog, the hosted refresh service
// included, shares the one catalog this synchronous refresh just populated, rather than a second instance
// that starts empty and only catches up once hosted services run.
builder.Services.AddServyxDefinitions(builder.Configuration);

GameDefinitionCatalog definitionCatalog;
GameDefinition? singleDefinition;
IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet;
{
    using var definitionsLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var definitionsBootstrapLogger = definitionsLoggerFactory.CreateLogger("Servyx.Web.Startup");
    var definitionsProvider = new FileSystemGameDefinitionProvider(
        builder.Configuration[Servyx.Definitions.ServiceCollectionExtensions.PathConfigKey],
        // keep in sync with AddServyxDefinitions, which resolves IDefinitionTrustEvaluator from DI instead —
        // equivalent today, since nothing registers one, but this is a second place to update once trust
        // evaluation ships.
        trustEvaluator: null,
        definitionsLoggerFactory.CreateLogger<FileSystemGameDefinitionProvider>());
    definitionCatalog = new GameDefinitionCatalog(
        [definitionsProvider], definitionsLoggerFactory.CreateLogger<GameDefinitionCatalog>());
    await definitionCatalog.RefreshAsync();

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
// SettingGroup, each already in the YAML's own document order, exactly as authored under
// definitions/palworld-docker.yaml's `settings:` key. Null by the same "single-criteria mode only" rule as
// lifecycleDefinition above; in multi-definition mode each server's settings come from
// IBoundDefinitionLookup instead of this ambient singleton.
var settingGroups = useSingleCriteriaMode ? singleDefinition?.Settings : null;

builder.Services.AddServyxDocker();

// The default probe target LiveDashboardDataService reports connection status against: the local Docker
// daemon AddServyxDocker() just wired up. AddServyxSshDocker() below replaces this registration entirely
// — TargetDescriptor, ITransport, IServerDiscovery, ILogStream and IMetricsSource together — when a remote
// host is declared, so this stays the only registration in the process that reads it.
builder.Services.AddSingleton(sp =>
    LiveDashboardDataService.BuildDockerTarget(sp.GetRequiredService<ILogger<LiveDashboardDataService>>()));

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
// Palworld definition load above — they use a short-lived bootstrap logger rather than one resolved from the
// (not yet built) DI container.
//
// Deliberately NOT scoped to a `{ }` block: sshDockerWiring and sshDockerLogger are still needed below, once
// provisioningGate is known, to read SshDockerWriteModes.ReadGrants — and that reuses this same bootstrap
// logger rather than standing up a second one for the same startup phase.
using var sshDockerLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var sshDockerLogger = sshDockerLoggerFactory.CreateLogger("Servyx.Infrastructure.Ssh.Docker.SshDockerWiring");
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

// Servyx:DataSource selects between the real Docker-backed data service and the in-memory mock, so the
// UI stays developable/testable without a Docker daemon. Defaults to Live; the mock remains available
// (and is what all 13 bUnit tests bind directly, independent of this registration) for local UI work
// without Docker running.
var dataSource = builder.Configuration["Servyx:DataSource"];
if (string.Equals(dataSource, "Mock", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDashboardDataService, MockDashboardDataService>();
}
else
{
    builder.Services.AddSingleton<IDashboardDataService, LiveDashboardDataService>();
}

// ── Authentication gate ──────────────────────────────────────────────────────────────────────────
//
// Servyx is a single-operator, self-hosted application: one person, one password, one session. There is no
// user table, no roles, no API keys — see AddServyxOperatorAuthentication's remarks for the full list of
// what this deliberately does not provide.
//
// Servyx:Authentication:Enabled defaults to TRUE. That is the opposite default from the provisioning gate
// immediately below, and both defaults follow the same rule: a misconfiguration must never widen what an
// anonymous caller can do. Provisioning is a capability, so an unreadable flag leaves it off; authentication
// is a protection, so an unreadable flag leaves it on. Only an explicit, parseable `false` turns it off.
//
// What "on" means in practice is one line inside AddServyxOperatorAuthentication: an authorization
// FallbackPolicy requiring an authenticated user. It applies to every endpoint that does not carry
// authorization metadata of its own — every page in this app, including pages nobody has written yet, and
// the Blazor SignalR endpoint itself. The only things that opt out are /login and the static assets the
// login page needs to render.
var authenticationGate = AuthenticationGate.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(authenticationGate);
builder.Services.AddServyxOperatorAuthentication(
    authenticationGate,
    builder.Environment.IsDevelopment(),
    // Lets an operator (or a test host) point the encrypted secret files somewhere other than the default
    // servyx-data/secrets beside the binaries, in the same spirit as Servyx:Persistence:ConnectionString.
    builder.Configuration["Servyx:Secrets:RootDirectory"]);

// ── Provisioning gate ────────────────────────────────────────────────────────────────────────────
//
// !! WARNING — READ BEFORE SETTING Servyx:Provisioning:Enabled TO true !!
//
// Setting this flag to true registers an IProvisioner in this container, which is a MUTATING,
// MONEY-SPENDING capability: it can create real infrastructure, and at any provider other than a local
// Docker daemon that infrastructure is billed to a real account for as long as it exists. With the
// authentication gate above open (the default), that capability belongs to whoever holds the operator
// password. With the authentication gate CLOSED, it belongs to anyone who can reach this web port —
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

// ── Branding ─────────────────────────────────────────────────────────────────────────────────────
//
// Presentation only — see BrandingOptions' remarks. Registered unconditionally, like WritableServers
// above, so every page can inject it without the process having opted into anything: unconfigured, it is
// BrandingOptions.Default and every page renders exactly what it always has.
builder.Services.AddSingleton(BrandingOptions.FromConfiguration(builder.Configuration));

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
    // a backup context, so DockerBackupProvider flushes Palworld's world with RCON `Save` before archiving
    // — and refuses to write an archive at all if that flush does not succeed.
    var rconWiring = RconWiringOptions.FromConfiguration(builder.Configuration, provisioningGate);
    builder.Services.AddSingleton(rconWiring);

    if (rconWiring.Any)
    {
        using var rconLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var rconStartupLogger = rconLoggerFactory.CreateLogger("Servyx.Web.Startup");

        // Sourced from the single loaded definition's control.channels[id=rcon].commands map — the same
        // source PalworldDefinitionLoader.TryLoadRconCommands used to parse directly, now read off the
        // already-typed ControlPlane instead. A missing definition, a missing rcon channel, or a channel
        // with no commands all degrade to RconCommandCatalog.Empty, exactly like TryLoadRconCommands'
        // null return: there is still no hardcoded fallback catalogue anywhere in this codebase.
        var rconChannel = singleDefinition?.Control.Channels
            .FirstOrDefault(c => string.Equals(c.Id, "rcon", StringComparison.Ordinal));
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
                sshDockerWiring.Any ? sp.GetRequiredService<IExecutionTarget>() : null),
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

var app = builder.Build();

// Config-driven, startup-only write path into ISecretStore (e.g. importing an SSH private key) — see
// SecretImport's remarks. ISecretStore is resolved optionally: registered unconditionally today, but this
// stays safe if that ever changes.
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Servyx.Web.Startup");
if (app.Services.GetService<ISecretStore>() is { } secretStore)
{
    await SecretImport.RunAsync(app.Configuration, secretStore, startupLog);
}
else
{
    startupLog.LogWarning("Servyx:Secrets:Import was not processed: no ISecretStore is registered.");
}

// The one cross-check between the two gates. Each is defensible alone; "no authentication" plus "can create
// billable infrastructure" is not, and an operator who arrives in that state by editing one line of
// configuration deserves to be told so at Critical rather than to discover it from a bill.
//
// Every WriteModeGrant registered above — from ServerWriteModes, SshDockerWriteModes and
// SshBackupWiringOptions.WriteGrants alike — was added as its own AddSingleton(WriteModeGrant) instance, so
// resolving IEnumerable<WriteModeGrant> here is the complete, transport-agnostic set: exactly what the write
// guard itself would see, with nothing here re-deriving it from configuration a second time.
StartupSafetyWarnings.LogDangerousCombinations(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(OperatorAuthentication.AuditLogCategory),
    authenticationGate,
    provisioningGate,
    [.. app.Services.GetServices<WriteModeGrant>()]);

// Migrations are applied here — inside the flag-gated startup path — rather than inside
// AddServyxPersistence(). Registration stays side-effect-free and testable in isolation (a test fixture
// can compose the container without anything touching disk); migrating the schema is an explicit,
// startup-time action that should only ever happen once persistence was actually registered — either
// because provisioning is enabled, or because multi-definition mode registered it for the server-definition
// binding store (see the "Server-definition binding persistence" block above). With both conditions false,
// this block does not run and nothing here touches the database file.
if (provisioningGate.Enabled || !useSingleCriteriaMode)
{
    using var migrationScope = app.Services.CreateScope();
    migrationScope.ServiceProvider.GetRequiredService<ServyxDbContext>().Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Authentication resolves who the caller is; authorization enforces the FallbackPolicy installed by
// AddServyxOperatorAuthentication. Both must run before the endpoints below, and before UseAntiforgery, so
// that an anonymous request to any page is turned away by the framework rather than by anything Servyx
// wrote. When the authentication gate is closed no fallback policy exists and UseAuthorization is a no-op,
// which is exactly the pre-authentication behaviour, reachable only by setting the flag on purpose.
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// The single anonymous exception, and it is a deliberate one: the sign-in page is served before anyone has
// authenticated, so the stylesheet and favicon it references have to be too. These endpoints serve files
// from wwwroot only — there is no application data behind them.
app.MapStaticAssets().AllowAnonymous();

// GET/POST /login and POST /logout. The two login endpoints are the only AllowAnonymous endpoints in the
// application besides the static assets above.
app.MapServyxOperatorAuthentication();

// Every routable component is mapped here, and not one of them carries authorization metadata — which is
// precisely why the FallbackPolicy is the mechanism: these endpoints, plus the interactive-server SignalR
// endpoint that AddInteractiveServerRenderMode adds, all inherit "an authenticated user is required" without
// anyone having to remember to say so per page.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
