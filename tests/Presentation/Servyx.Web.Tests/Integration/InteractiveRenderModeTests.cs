using System.Diagnostics;
using System.Net.Sockets;

namespace Servyx.Web.Tests.Integration;

/// <summary>
/// Regression guard for a real production bug found by Playwright E2E testing: at one point, nothing in
/// Servyx.Web's render tree applied <c>@rendermode InteractiveServer</c> (only
/// <c>Program.cs</c>'s <c>.AddInteractiveServerRenderMode()</c>, which merely makes the render mode
/// *available*), so the entire app rendered as static SSR only and every <c>@onclick</c> handler anywhere
/// in the application was inert in a real browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be a bUnit test:</b> bUnit renders a component directly with its own synthetic
/// renderer — it never goes through <c>MapRazorComponents</c>/<c>AddInteractiveServerRenderMode</c>, the
/// actual HTTP pipeline, or render-mode resolution at all. Every one of the 21 bUnit tests elsewhere in
/// this project passed throughout the entire outage, by construction — a bUnit test cannot see whether a
/// render mode was ever applied to a component, because bUnit doesn't have the concept. Only an assertion
/// against the real, served HTML (produced by the real ASP.NET Core pipeline) can catch this class of bug.
/// </para>
/// <para>
/// <b>What "interactive" looks like on the wire:</b> when a component boundary is rendered under
/// <c>InteractiveServer</c>, ASP.NET Core emits an HTML comment marker of the form
/// <c>&lt;!--Blazor:{"type":"server",...}--&gt;</c> around it, in addition to the plain
/// <c>&lt;!--Blazor:{"prerenderId":...}--&gt;</c> markers that static SSR alone also produces. A page with
/// no interactive component anywhere emits only the latter. This test asserts the former is present.
/// </para>
/// <para>
/// <b>Why a real subprocess instead of bUnit or <c>WebApplicationFactory</c>:</b> this needs the real
/// ASP.NET Core request pipeline (<c>MapRazorComponents</c>/<c>AddInteractiveServerRenderMode</c>) to
/// actually run, which requires hosting the real app. It does not need a real browser or SignalR
/// connection (unlike <c>tests/Servyx.E2E.Tests</c>, which drives an actual Playwright browser over
/// WebSocket to verify clicks genuinely take effect) — it only needs to read the initial HTML response, so
/// a plain HTTP GET against a real Kestrel-hosted subprocess is enough and keeps this project free of a
/// Playwright dependency. The subprocess-launch approach mirrors <c>ServyxAppProcess</c> in
/// <c>tests/Servyx.E2E.Tests</c>; it is intentionally duplicated in miniature here (rather than referenced)
/// so that this project — which owns only <c>src/Presentation/Servyx.Web</c> — has no project dependency
/// on a sibling test project.
/// </para>
/// </remarks>
public sealed class InteractiveRenderModeTests
{
    [Fact]
    public async Task ServedHtml_ContainsAnInteractiveServerComponentMarker()
    {
        var port = GetFreeLoopbackPort();
        var serverAddress = $"http://127.0.0.1:{port}";
        var webDllPath = LocateServyxWebDll();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { webDllPath, "--urls", serverAddress },
            WorkingDirectory = Path.GetDirectoryName(webDllPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["Servyx__DataSource"] = "Mock";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        // This test is about render-mode resolution, and it reads the HTML of "/". With authentication left
        // at its default (on), "/" is a 302 to /login — a deliberately static, circuit-free document that has
        // no interactive component boundary in it by design, so the assertion below would fail for a reason
        // that has nothing to do with what this test guards. The app is therefore started in its documented
        // unauthenticated mode; the authentication behaviour it is opting out of is asserted directly by
        // OperatorAuthenticationEndpointTests, against the same real subprocess with authentication ON.
        startInfo.Environment["Servyx__Authentication__Enabled"] = "false";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        try
        {
            // No per-call HttpClient.Timeout here, deliberately: WaitForHtmlAsync already bounds the whole
            // retry loop with its own 30s CancellationTokenSource, passed to every GetAsync call. A second,
            // shorter timeout layered on top of that (this used to be 2s) does not shorten the overall
            // wait — it just makes an individual slow attempt throw TaskCanceledException, which IS an
            // OperationCanceledException, so WaitForHtmlAsync's "is not OperationCanceledException" retry
            // filter cannot tell that failure apart from its own 30s deadline and lets it propagate instead
            // of retrying. Under light load a 2s per-call timeout never fires and this bug is invisible; under
            // parallel test-suite load, one slow request to the freshly-started subprocess is enough to fail
            // the test outright with 28 of the 30 budgeted seconds still unused — exactly the flake this
            // comment exists to prevent from coming back. Relying solely on the shared CancellationToken for
            // the deadline means a slow individual attempt is cancelled at the same 30s boundary as everything
            // else, and reaching that boundary is the only way this loop ever gives up.
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var html = await WaitForHtmlAsync(client, serverAddress);

            html.Should().Contain(
                "\"type\":\"server\"",
                because: "an interactive server component boundary must exist somewhere in the render tree " +
                          "(see App.razor's Routes/HeadOutlet @rendermode) or every @onclick handler in the " +
                          "app is silently inert in a real browser, as happened before this test existed");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<string> WaitForHtmlAsync(HttpClient client, string baseAddress)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Exception? lastFailure = null;
        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var response = await client.GetAsync(baseAddress, timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(timeoutCts.Token);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Servyx.Web did not become ready at {baseAddress} within 30s.", lastFailure);
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
    /// directory. <c>dotnet test</c> always builds the referenced <c>Servyx.Web</c> project first (it is a
    /// <c>ProjectReference</c>), so this path is guaranteed to exist by the time this test runs.
    /// </summary>
    private static string LocateServyxWebDll()
    {
        var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = testOutputDir.Name;
        var config = testOutputDir.Parent?.Name;

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
                $"Servyx.Web.dll was not found at '{dllPath}'. Build Servyx.Web (or the whole solution) before running Servyx.Web.Tests.",
                dllPath);
        }

        return dllPath;
    }
}
