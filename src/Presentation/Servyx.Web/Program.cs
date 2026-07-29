using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Servyx.Application;
using Servyx.Application.Backups;
using Servyx.Application.Provisioning;
using Servyx.Application.Servers;
using Servyx.Infrastructure.Docker.Backups;
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
using Servyx.Web.Authentication;
using Servyx.Web.Components;
using Servyx.Web.Definitions;
using Servyx.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Load the bundled game definition's metadata/adoption-criteria block once at startup (only the
// `metadata` and docker-kind `deployments` entry's `detect`/`image` blocks — see PalworldDefinitionLoader
// for why this is not a full schema-validated parse). A missing or malformed file degrades to the
// hardcoded AdoptionCriteria.PalworldDefault rather than failing startup — logged via a bootstrap logger
// (the DI container isn't built yet) so the fallback is diagnosable rather than silent. Scoped to this
// block alone — the factory is only needed for the single TryLoad call, not the app's whole lifetime.
PalworldDefinitionInfo? definition;
{
    using var startupLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var startupLogger = startupLoggerFactory.CreateLogger("Servyx.Web.Startup");
    definition = PalworldDefinitionLoader.TryLoad(AppContext.BaseDirectory, startupLogger);
}
if (definition is not null)
{
    builder.Services.AddSingleton(definition);
}

var adoptionCriteria = definition is not null
    ? new AdoptionCriteria(definition.GameId, definition.GameName, definition.ImageRepository, definition.RequiredMountContainerPath)
    : AdoptionCriteria.PalworldDefault;

builder.Services.AddServyxDocker();
builder.Services.AddServyxApplication(adoptionCriteria);

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
    foreach (var writeGrant in ServerWriteModes.ReadGrants(builder.Configuration, provisioningGate))
    {
        builder.Services.AddSingleton(writeGrant);
    }

    // The one call in this file that makes container creation/destruction reachable at all. It is
    // deliberately NOT part of AddServyxDocker() — see AddServyxDockerProvisioning's own remarks.
    builder.Services.AddServyxDockerProvisioning();

    // Durable storage for the provisioning ledger — only composed when provisioning itself is enabled, so
    // a read-only host never gets a ServyxDbContext, a SQLite file, or an IProvisioningLedger in its
    // container. Servyx:Persistence:ConnectionString lets an operator point at a different file (or a
    // different provider-compatible connection string) without touching code; the default keeps the
    // database alongside the other on-disk state under servyx-data/, matching the convention
    // SecretsOptions already uses for secrets/host-keys (see Servyx.Infrastructure.Secrets.SecretsOptions).
    var persistenceConnectionString = builder.Configuration["Servyx:Persistence:ConnectionString"]
        ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "servyx-data", "servyx.db")}";
    builder.Services.AddServyxPersistence(persistenceConnectionString);

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
        var rconCommands = PalworldDefinitionLoader.TryLoadRconCommands(
            AppContext.BaseDirectory,
            rconLoggerFactory.CreateLogger("Servyx.Web.Startup"));

        var rconCatalog = rconCommands is null ? RconCommandCatalog.Empty : new RconCommandCatalog(rconCommands);

        builder.Services.AddServyxRcon();
        builder.Services.AddSingleton(sp => new ServyxRconChannels(
            rconWiring,
            rconCatalog,
            sp.GetRequiredService<IRconClient>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<WritableServers>(),
            // The audited raw escape hatch stays unavailable until a host implements IRconAuditSink. A raw
            // command bypasses the catalogue's readOnly classification, so the audit record is the only
            // remaining account of what was run — and RconSession refuses to send one it cannot record.
            audit: null));
    }
    else
    {
        // Registered on both sides so ServyxBackupContextSource always resolves; None composes no client,
        // no secret lookup and no session, and every TryGetSession returns null.
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
    // container, a data root and a capture set is host knowledge. Registered as the implementation type
    // as well so its cached sessions are disposed with the container.
    builder.Services.AddSingleton<ServyxBackupContextSource>();
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
    var sshBackups = SshBackupWiringOptions.FromConfiguration(builder.Configuration, provisioningGate);
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

// The one cross-check between the two gates. Each is defensible alone; "no authentication" plus "can create
// billable infrastructure" is not, and an operator who arrives in that state by editing one line of
// configuration deserves to be told so at Critical rather than to discover it from a bill.
StartupSafetyWarnings.LogDangerousCombinations(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(OperatorAuthentication.AuditLogCategory),
    authenticationGate,
    provisioningGate);

// Migrations are applied here — inside the flag-gated startup path — rather than inside
// AddServyxPersistence(). Registration stays side-effect-free and testable in isolation (a test fixture
// can compose the container without anything touching disk); migrating the schema is an explicit,
// startup-time action that should only ever happen when provisioning is actually enabled, so it is gated
// by the exact same provisioningGate.Enabled check as the registrations above. With the flag off, this
// block does not run and nothing here touches the database file.
if (provisioningGate.Enabled)
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
