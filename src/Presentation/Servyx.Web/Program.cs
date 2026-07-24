using Microsoft.Extensions.Logging;
using Servyx.Application;
using Servyx.Application.Servers;
using Servyx.Infrastructure.Docker;
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
