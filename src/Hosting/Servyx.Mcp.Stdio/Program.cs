using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Servyx.Composition;
using Servyx.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// STDOUT IS THE PROTOCOL CHANNEL. A single write to stdout anywhere in this process corrupts the
// JSON-RPC stream and the client disconnects with a parse error that names nothing useful. Clear
// first: the default host builder has already installed a stdout console provider by this line.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Clearing providers is NOT sufficient on its own. AddServyxCore builds bootstrap loggers before the
// DI container exists, so they cannot be reached by the host's logging configuration — hence the
// explicit stderr-writing factory. See ServyxCoreCompositionExtensions' remarks.
using var bootstrapLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.ClearProviders();
    logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
});

// AddServiceDefaults() is deliberately NOT called: it wires ASP.NET Core instrumentation and its
// companion endpoint mapping needs a WebApplication. Neither means anything in a stdio process, and
// every extra provider is one more thing that could write to stdout.
var core = builder.AddServyxCore(bootstrapLoggerFactory);

// Registers the already-built composition RESULT into DI so tool methods can read CatalogMode/Capabilities/
// Provisioning off it directly, the same way AddServyxCoreCore already registers each of its individual
// pieces (GameDefinitionCatalog, ProvisioningGate, WritableServers, ...) as singletons. This is not a second
// composition root: nothing here builds a gate, a grant, or a guarded transport — it only exposes the one
// object AddServyxCore already constructed. See CompositionRootSingleSourceTests for the line this test
// suite draws between "register the composed result" (here) and "compose a safety gate a second time"
// (forbidden in both Program.cs files).
builder.Services.AddSingleton(core);

builder.Services.AddServyxMcpTools();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = ServyxMcpServer.Name, Version = ServyxMcpServer.Version };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(ServyxMcpToolsServiceCollectionExtensions).Assembly);

var host = builder.Build();
await core.RunStartupTasksAsync(host.Services);
await host.RunAsync();
