using System.Diagnostics;
using System.Net.Sockets;

namespace Servyx.E2E.Tests;

/// <summary>
/// Hosts the real Servyx.Web app for Playwright to drive over an actual HTTP+WebSocket connection, by
/// launching it as a subprocess on a dynamically chosen port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subprocess instead of <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>:</b>
/// out of the box, <c>WebApplicationFactory</c> hosts the app on an in-memory <c>TestServer</c> with no
/// real socket — fine for the bUnit component tests elsewhere in this solution, but Blazor <b>Server</b>
/// cannot be driven by a real browser that way: its circuit is carried over a genuine SignalR WebSocket
/// connection, and Playwright's Chromium instance is a separate OS process that cannot attach to an
/// in-memory <c>TestServer</c> at all. The commonly documented fix — overriding
/// <c>WebApplicationFactory.CreateHost</c> to additionally build and start a real Kestrel host — was
/// tried first here and rejected: this ASP.NET Core version's <c>WebApplicationFactory</c> registers its
/// in-memory <c>TestServer</c> as <c>IServer</c> in a way that keeps winning dependency-injection
/// resolution even after explicitly removing it and re-registering Kestrel from the overridden
/// <c>ConfigureWebHost</c>/<c>CreateHost</c> hooks — fighting framework internals rather than testing the
/// app. A subprocess sidesteps that entirely: it is simply the real, unmodified app, started exactly the
/// way an operator would start it, listening on a real socket picked up front.
/// </para>
/// <para>
/// The dynamic port is chosen by briefly binding a <see cref="TcpListener"/> to port 0, reading back
/// whatever the OS assigned, and releasing it immediately before the subprocess binds the same port
/// itself — a standard, small-window TOCTOU accepted for test infrastructure. Readiness is confirmed by
/// polling the root page with a real <see cref="HttpClient"/> until it responds, never a fixed delay.
/// </para>
/// </remarks>
public sealed class ServyxAppProcess : IAsyncDisposable
{
    private Process? _process;

    /// <summary>The base address of the running app once <see cref="StartAsync"/> has completed.</summary>
    public string ServerAddress { get; private set; } = string.Empty;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var port = GetFreeLoopbackPort();
        ServerAddress = $"http://127.0.0.1:{port}";

        var webDllPath = LocateServyxWebDll();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { webDllPath, "--urls", ServerAddress },
            // Without this, the child process inherits the test host's own working directory (the test
            // assembly's output folder), so ASP.NET Core's default content root resolves to the WRONG
            // directory: Servyx.Web then can't find its own wwwroot static web assets (blazor.web.js,
            // scoped CSS, etc). The page still renders (Razor markup doesn't need wwwroot), but the
            // Blazor Server JS bundle 404s, the SignalR circuit never connects, and every @onclick
            // handler silently does nothing — exactly what was observed before this fix.
            WorkingDirectory = Path.GetDirectoryName(webDllPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["Servyx__DataSource"] = "Mock";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output.Enqueue(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output.Enqueue("[stderr] " + e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReadyAsync(ServerAddress, ct).ConfigureAwait(false);
    }

    /// <summary>Captured stdout/stderr lines from the subprocess, useful for diagnosing a failed or unresponsive app.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Output { get; } = new();

    private static async Task WaitUntilReadyAsync(string baseAddress, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        Exception? lastFailure = null;
        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var response = await client.GetAsync(baseAddress, timeoutCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException(
            $"Servyx.Web did not become ready at {baseAddress} within 30s.", lastFailure);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Locates the already-built <c>Servyx.Web.dll</c> relative to this test assembly's own output
    /// directory (<c>tests/Servyx.E2E.Tests/bin/&lt;Config&gt;/&lt;TFM&gt;/</c>), reusing the same
    /// configuration/target-framework segment names so it works under both <c>Debug</c> and
    /// <c>Release</c> without hardcoding either. <c>dotnet test</c> always builds the referenced
    /// <c>Servyx.Web</c> project first (it is a <c>ProjectReference</c>), so this path is guaranteed to
    /// exist by the time a test runs.
    /// </summary>
    private static string LocateServyxWebDll()
    {
        var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = testOutputDir.Name; // e.g. "net10.0"
        var config = testOutputDir.Parent?.Name; // e.g. "Debug" or "Release"

        var repoRoot = testOutputDir;
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "Servyx.sln")))
        {
            repoRoot = repoRoot.Parent;
        }

        if (repoRoot is null || config is null)
        {
            throw new InvalidOperationException($"Could not locate the repository root (Servyx.sln) above '{AppContext.BaseDirectory}'.");
        }

        var dllPath = Path.Combine(repoRoot.FullName, "src", "Presentation", "Servyx.Web", "bin", config, tfm, "Servyx.Web.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"Servyx.Web.dll was not found at '{dllPath}'. Build Servyx.Web (or the whole solution) before running Servyx.E2E.Tests.",
                dllPath);
        }

        return dllPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill attempt — nothing to clean up.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
