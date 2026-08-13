using Microsoft.Extensions.Logging;
using Servyx.Application.Hosts;
using Servyx.Composition;
using Servyx.Domain.Hosts;
using Servyx.Domain.Transport;
using Servyx.Web.Authentication;
using Servyx.Web.Components;
using Servyx.Web.Hosts;
using Servyx.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The shared composition root: game definition catalog, provisioning/write-mode gates, RCON, lifecycle,
// backups, and persistence — everything a second host (an MCP server, coming in a later phase) needs
// byte-for-byte identically to this one, so a safety gate can never be registered here and silently missing
// there. See ServyxCoreCompositionExtensions.AddServyxCore's remarks for what it does and does not include.
var core = builder.AddServyxCore();

// ── Authentication gate ──────────────────────────────────────────────────────────────────────────
//
// Servyx is a single-operator, self-hosted application: one person, one password, one session. There is no
// user table, no roles, no API keys — see AddServyxOperatorAuthentication's remarks for the full list of
// what this deliberately does not provide.
//
// Servyx:Authentication:Enabled defaults to TRUE. That is the opposite default from the provisioning gate
// AddServyxCore already resolved, and both defaults follow the same rule: a misconfiguration must never
// widen what an anonymous caller can do. Provisioning is a capability, so an unreadable flag leaves it off;
// authentication is a protection, so an unreadable flag leaves it on. Only an explicit, parseable `false`
// turns it off.
//
// What "on" means in practice is one line inside AddServyxOperatorAuthentication: an authorization
// FallbackPolicy requiring an authenticated user. It applies to every endpoint that does not carry
// authorization metadata of its own — every page in this app, including pages nobody has written yet, and
// the Blazor SignalR endpoint itself. The only things that opt out are /login and the static assets the
// login page needs to render.
//
// Web-only: an MCP host authenticates its own transport, not through this operator-password gate, so this
// registration stays here rather than in the shared composition root.
var authenticationGate = AuthenticationGate.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(authenticationGate);
builder.Services.AddServyxOperatorAuthentication(
    authenticationGate,
    builder.Environment.IsDevelopment(),
    // Lets an operator (or a test host) point the encrypted secret files somewhere other than the default
    // servyx-data/secrets beside the binaries, in the same spirit as Servyx:Persistence:ConnectionString.
    builder.Configuration["Servyx:Secrets:RootDirectory"]);

// The runtime (UI-driven) counterpart to the config-driven Servyx:Secrets:Import startup path: lets a future
// "adopt a remote host" form put an SSH private key into the same encrypted secret store without an
// operator editing configuration and restarting. Registered here, immediately after
// AddServyxOperatorAuthentication, because that call is what registers the ISecretStore this store is built
// on (see AddServyxSecrets inside it) — same singleton lifetime as OperatorCredentialStore, for the same
// reason: it owns no state of its own beyond the ISecretStore reference, so one instance for the process
// lifetime is exactly right.
builder.Services.AddSingleton<SshHostCredentialStore>();

// ── Host registration ────────────────────────────────────────────────────────────────────────────
//
// The use case itself (probe → confirm a fingerprint → register) lives in Servyx.Application, where it belongs:
// it orchestrates the Hosts table, the host key store, the secret store, and the discovery cache, and it
// contains the security decision this whole feature rests on — a caller-supplied fingerprint is only ever
// compared against one this process observed on the wire, never pinned on faith. See
// IHostRegistrationService.RegisterAsync's remarks.
//
// The wiring is here, in the outer composition root, for one reason: exactly one of its collaborators —
// IHostCredentialImporter — is implemented by a Presentation-layer type. SshHostCredentialStore lives in this
// project because the ISecretStore it writes through is registered by AddServyxOperatorAuthentication above,
// which is Web-only (an MCP host authenticates its own transport and does not run it). Servyx.Application must
// never reference Servyx.Web, so the seam is declared in Servyx.Domain (IHostCredentialImporter, alongside
// IHostRepository and for the same documented reason) and bound to the concrete store here — a composition
// root is precisely the place that is allowed to know both halves. The other two collaborators need no such
// indirection: IHostKeyStore is already a Domain abstraction with a Servyx.Infrastructure implementation, and
// IHostKeyProbe/IHostConnectionRefresher are registered by AddServyxSshDocker inside AddServyxCore.
builder.Services.AddSingleton<IHostCredentialImporter>(sp => sp.GetRequiredService<SshHostCredentialStore>());
builder.Services.AddSingleton<IHostRegistrationService, HostRegistrationService>();

// ── Presentation ─────────────────────────────────────────────────────────────────────────────────
//
// Web-only, like the authentication gate above: branding is a Blazor concern, and Servyx:DataSource
// selects between the real Docker-backed dashboard service and the in-memory mock, so the UI stays
// developable/testable without a Docker daemon. Defaults to Live; the mock remains available (and is what
// all 13 bUnit tests bind directly, independent of this registration) for local UI work without Docker
// running.
builder.Services.AddSingleton(BrandingOptions.FromConfiguration(builder.Configuration));
if (string.Equals(builder.Configuration["Servyx:DataSource"], "Mock", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDashboardDataService, MockDashboardDataService>();
}
else
{
    builder.Services.AddSingleton<IDashboardDataService, LiveDashboardDataService>();
}

// The /settings page's own data service, deliberately separate from IDashboardDataService: that interface is
// about servers, games and backups and shares not one collaborator with this. No Mock counterpart and no
// Servyx:DataSource branch either — every section it reports describes this process's own composition (the
// retention window it resolved, the hosts it registered, whether an operator password exists), so a mock
// would be describing a process that does not exist, and with nothing composed the live service already
// answers correctly, which is that nothing is configured. Its collaborators past the authentication gate are
// nullable with default null, so DI's default-value fallback supplies them exactly the way it does for
// LiveDashboardDataService's optional backup dashboard and definition catalog.
builder.Services.AddSingleton<ISettingsDataService, LiveSettingsDataService>();

var app = builder.Build();

// The post-Build() half of the shared composition root, deliberately NOT run via
// ServyxCoreComposition.RunStartupTasksAsync — that convenience method runs secret import and the database
// migration back-to-back with nothing observable in between. Here they are split and the Critical-level
// safety warning below is sequenced BETWEEN them, on purpose: if Database.Migrate() throws (a locked file,
// a permissions error, a drifted schema) the process dies right after, but the operator's log already has
// the warning below in it. Losing that ordering would mean a migration failure could take down the process
// before the one log line that would have told the operator their configuration was dangerous ever got
// written. See ServyxCoreComposition.RunStartupTasksAsync's remarks for the full rationale.
await core.ImportSecretsAsync(app.Services);

// The one cross-check between the two gates. Each is defensible alone; "no authentication" plus "can create
// billable infrastructure" is not, and an operator who arrives in that state by editing one line of
// configuration deserves to be told so at Critical rather than to discover it from a bill.
//
// This enumeration covers the CONFIG-SOURCED grants only. SshDockerWriteModes and
// SshBackupWiringOptions.WriteGrants each add their grants as their own AddSingleton(WriteModeGrant)
// instance, so resolving IEnumerable<WriteModeGrant> here reads exactly what the shared composition root
// already built for those transports, with nothing re-deriving it from configuration a second time. It is
// NOT the whole picture: the local docker path no longer emits a WriteModeGrant at all — an adopted server's
// grant is a database row — and those arrive through the WritableServers argument below.
StartupSafetyWarnings.LogDangerousCombinations(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(OperatorAuthentication.AuditLogCategory),
    authenticationGate,
    core.Provisioning,
    [.. app.Services.GetServices<WriteModeGrant>()],
    // The per-server grant for an adopted server is a database row, not a registered WriteModeGrant, so the
    // line above no longer sees it. Reading the live view the composition root already built is what keeps
    // the "unauthenticated with write access" Critical warning from going quiet on exactly the hosts that
    // need it. GetRequiredService, not GetService: AddServyxCore always registers this, so the nullable form
    // would protect against nothing while turning a dropped registration into a silently-blinded alarm — a
    // clean startup log that no longer sees any database grant at all. (MainLayout and NavMenu resolve it
    // optionally on purpose; an unregistered service degrades them closed. This one is required.)
    app.Services.GetRequiredService<WritableServers>());

await core.MigrateDatabaseAsync(app.Services);

// One-time upgrade path: an install that has a legacy shared operator password but no User rows yet gets a
// bootstrap Admin account seeded from that same password (byte-for-byte compatible hash — see
// UserBootstrapMigration's own remarks), so switching the login pipeline over to per-account authentication
// never locks out an operator who upgrades into it. Runs after the database migration above, since it needs
// the Users table to exist, and before the app starts listening, since it is not safe to race against a
// concurrent first sign-in.
await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(app.Services);

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
