using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Servyx.Web.Authentication;

namespace Servyx.Web.Tests.Integration;

/// <summary>
/// Authentication asserted against the real, unmodified application over real HTTP — the only place the
/// claim "<c>/deploy</c> cannot be reached without logging in" can actually be proved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subprocess rather than bUnit:</b> the protection under test is an ASP.NET Core
/// <c>FallbackPolicy</c> enforced by the authorization middleware against an endpoint. bUnit renders
/// components with its own synthetic renderer and never runs routing, middleware, or endpoint metadata
/// resolution at all — so no bUnit test can observe whether an anonymous <c>GET /deploy</c> is refused
/// before a component exists. It can only observe what happens once one does. Both halves matter, and the
/// component half lives in <c>Pages/DeployRouteAuthorizationTests</c>; this is the half where the request
/// never gets that far. The subprocess-launch approach mirrors <c>InteractiveRenderModeTests</c> in the same
/// project.
/// </para>
/// <para>
/// The whole scenario is one test on purpose. It walks a single install through its entire credential
/// lifetime — never bootstrapped, bootstrapped, signed in, signed out — and several of the assertions are
/// only meaningful in that order (in particular, "the first-run flow refuses the second time" requires there
/// to have been a first time). Splitting it into facts would either re-launch the app per fact or make the
/// facts order-dependent, and order-dependent facts are worse than a long one.
/// </para>
/// </remarks>
public sealed class OperatorAuthenticationEndpointTests
{
    private const string Username = "admin";
    private const string OperatorPassword = "correct-horse-battery-staple";
    private const string AuthCookieName = "servyx.auth";

    [Fact]
    public async Task AnUnauthenticatedCallerCanReachNothingButTheLoginPage()
    {
        var port = GetFreeLoopbackPort();
        var serverAddress = $"http://127.0.0.1:{port}";

        // A secrets root AND a database nothing else has ever written to, so this install genuinely starts
        // with no operator password and no User rows, and the first-run path is the real first run rather
        // than a leftover from a previous run. The database matters now in a way it did not before accounts
        // existed: SetupRequired is decided against the Users table, and without an isolated connection
        // string every run would share the fixed default path under the test binary's own directory.
        var installRoot = Path.Combine(Path.GetTempPath(), "servyx-auth-tests", Guid.NewGuid().ToString("n"));
        var secretsRoot = Path.Combine(installRoot, "secrets");
        var databasePath = Path.Combine(installRoot, "servyx.db");
        Directory.CreateDirectory(installRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { LocateServyxWebDll(), "--urls", serverAddress },
            WorkingDirectory = Path.GetDirectoryName(LocateServyxWebDll()),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["Servyx__DataSource"] = "Mock";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["Servyx__Secrets__RootDirectory"] = secretsRoot;
        startInfo.Environment["Servyx__Persistence__ConnectionString"] = $"Data Source={databasePath}";

        // Deliberately NOT setting Servyx__Authentication__Enabled: the default must be "on", and this test
        // exists to prove that a host nobody configured is a host nobody can walk into.

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        try
        {
            using var theOperator = new Browser(serverAddress);
            await WaitUntilReadyAsync(theOperator);

            // ── 1. Nothing is reachable ──────────────────────────────────────────────────────────────
            foreach (var path in new[] { "/", "/deploy", "/servers", "/audit", "/settings" })
            {
                using var response = await theOperator.GetAsync(path);

                response.StatusCode.Should().Be(
                    HttpStatusCode.Redirect,
                    because: $"an anonymous GET {path} must be turned away by the fallback policy");
                LocationOf(response).Should().StartWith(
                    "/login", because: $"an anonymous GET {path} must be sent to the sign-in page");
            }

            // The interactive circuit is an endpoint like any other, and it is protected like one — which is
            // what stops an anonymous caller from rendering components over the wire instead.
            using var negotiate = await theOperator.Client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
            negotiate.StatusCode.Should().Be(
                HttpStatusCode.Redirect, "an anonymous caller must not be able to open a Blazor circuit");

            // ── 2. The two things that are reachable, and only those ─────────────────────────────────
            using var css = await theOperator.GetAsync("/app.css");
            css.StatusCode.Should().Be(
                HttpStatusCode.OK, "the sign-in page's own stylesheet has to load before anyone has signed in");

            var loginPage = await theOperator.GetHtmlAsync("/login");
            loginPage.Should().Contain("data-testid=\"setup-form\"",
                "a never-bootstrapped install must offer the one-time account-creation form");
            loginPage.Should().NotContain("data-testid=\"login-form\"");

            // ── 3. First run creates the first account (Admin) and signs the caller in ───────────────
            using var bootstrap = await theOperator.PostFormAsync("/login", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginPage),
                ["intent"] = "set-password",
                [OperatorAuthentication.ReturnUrlParameter] = "/deploy",
                ["username"] = Username,
                ["newPassword"] = OperatorPassword,
                ["confirmPassword"] = OperatorPassword,
            });

            bootstrap.StatusCode.Should().Be(HttpStatusCode.Redirect);
            LocationOf(bootstrap).Should().Be("/deploy");
            theOperator.Cookie(AuthCookieName).Should().NotBeNullOrEmpty(
                "a successful first run issues the session cookie");

            using var deployAuthenticated = await theOperator.GetAsync("/deploy");
            deployAuthenticated.StatusCode.Should().Be(
                HttpStatusCode.OK, "the signed-in account must actually be able to reach /deploy");

            // ── 4. The first-run flow is not a permanent way in ──────────────────────────────────────
            using var replayer = new Browser(serverAddress);
            var replayerLogin = await replayer.GetHtmlAsync("/login");

            replayerLogin.Should().Contain("data-testid=\"login-form\"",
                "once an account exists, the account-creation form must not be offered again");
            replayerLogin.Should().NotContain("data-testid=\"setup-form\"");

            using var replay = await replayer.PostFormAsync("/login", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(replayerLogin),
                ["intent"] = "set-password",
                ["username"] = "someone-else",
                ["newPassword"] = "some-other-password",
                ["confirmPassword"] = "some-other-password",
            });

            replay.StatusCode.Should().Be(HttpStatusCode.OK, "the replay is refused, not redirected onwards");
            (await replay.Content.ReadAsStringAsync()).Should().Contain("already been set up");
            replayer.Cookie(AuthCookieName).Should().BeNullOrEmpty(
                "replaying the one-time bootstrap must never issue a session");

            using var replayerDeploy = await replayer.GetAsync("/deploy");
            replayerDeploy.StatusCode.Should().Be(HttpStatusCode.Redirect);

            // ── 5. A wrong password authenticates nobody ─────────────────────────────────────────────
            using var guesser = new Browser(serverAddress);
            var guesserLogin = await guesser.GetHtmlAsync("/login");

            using var guess = await guesser.PostFormAsync("/login", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(guesserLogin),
                ["username"] = Username,
                ["password"] = "not-the-right-password",
            });

            guess.StatusCode.Should().Be(HttpStatusCode.OK);
            (await guess.Content.ReadAsStringAsync()).Should().Contain("was not accepted");
            guesser.Cookie(AuthCookieName).Should().BeNullOrEmpty();

            using var guesserDeploy = await guesser.GetAsync("/deploy");
            guesserDeploy.StatusCode.Should().Be(HttpStatusCode.Redirect);

            // ── 5b. An unknown username authenticates nobody either, indistinguishably ────────────────
            using var guesser2 = new Browser(serverAddress);
            var guesser2Login = await guesser2.GetHtmlAsync("/login");

            using var guess2 = await guesser2.PostFormAsync("/login", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(guesser2Login),
                ["username"] = "nobody-registered",
                ["password"] = OperatorPassword,
            });

            guess2.StatusCode.Should().Be(HttpStatusCode.OK);
            (await guess2.Content.ReadAsStringAsync()).Should().Contain("was not accepted");
            guesser2.Cookie(AuthCookieName).Should().BeNullOrEmpty();

            // ── 6. The right username and password do, and sign-out undoes it ────────────────────────
            using var returning = new Browser(serverAddress);
            var signInPage = await returning.GetHtmlAsync("/login?returnUrl=%2Fdeploy");

            using var signIn = await returning.PostFormAsync("/login", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(signInPage),
                [OperatorAuthentication.ReturnUrlParameter] = "/deploy",
                ["username"] = Username,
                ["password"] = OperatorPassword,
            });

            signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
            LocationOf(signIn).Should().Be(
                "/deploy", "the operator lands where they were originally headed");

            var sessionCookie = returning.Cookie(AuthCookieName);
            sessionCookie.Should().NotBeNullOrEmpty();
            sessionCookie.Should().NotContain(
                OperatorPassword, "the session cookie must not carry the credential that produced it");

            using var signedOut = await returning.PostFormAsync(OperatorAuthentication.LogoutPath, []);
            signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);
            LocationOf(signedOut).Should().StartWith(OperatorAuthentication.LoginPath);

            using var afterSignOut = await returning.GetAsync("/deploy");
            afterSignOut.StatusCode.Should().Be(
                HttpStatusCode.Redirect, "signing out must actually end the session");
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

    /// <summary>
    /// One browser: an <see cref="HttpClient"/> that never follows redirects (so a 302 to <c>/login</c> is an
    /// observable fact rather than something silently followed) and its own cookie jar, so each caller in
    /// the scenario is genuinely a separate session.
    /// </summary>
    private sealed class Browser : IDisposable
    {
        private readonly CookieContainer _cookies = new();
        private readonly Uri _baseAddress;

        public Browser(string baseAddress)
        {
            _baseAddress = new Uri(baseAddress);
            Client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = _cookies,
            })
            {
                BaseAddress = _baseAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public HttpClient Client { get; }

        public string? Cookie(string name) => _cookies.GetCookies(_baseAddress)[name]?.Value;

        public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

        public async Task<string> GetHtmlAsync(string path)
        {
            using var response = await Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path} must be served");
            return await response.Content.ReadAsStringAsync();
        }

        public Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> fields)
            => Client.PostAsync(path, new FormUrlEncodedContent(fields));

        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// The Location header as an app-relative path. The cookie handler's own challenge redirect is absolute
    /// (it builds a full URI from the request), while the endpoints' LocalRedirect responses are relative —
    /// so both shapes appear in one scenario and both mean the same thing.
    /// </summary>
    private static string LocationOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        location.Should().NotBeNull("a redirect must say where to");

        return location!.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" value=\"(?<token>[^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue("the sign-in form must carry an antiforgery token");
        return match.Groups["token"].Value;
    }

    private static async Task WaitUntilReadyAsync(Browser browser)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Exception? lastFailure = null;
        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                // Readiness is "/login answers", not "/ answers": with authentication on, / is a redirect.
                using var response = await browser.Client.GetAsync("/login", timeoutCts.Token);
                if (response.StatusCode == HttpStatusCode.OK)
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
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException(
            $"Servyx.Web did not become ready at {browser.Client.BaseAddress} within 60s.", lastFailure);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

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
            throw new InvalidOperationException(
                $"Could not locate the repository root (Servyx.sln) above '{AppContext.BaseDirectory}'.");
        }

        var dllPath = Path.Combine(
            repoRoot.FullName, "src", "Presentation", "Servyx.Web", "bin", config, tfm, "Servyx.Web.dll");

        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"Servyx.Web.dll was not found at '{dllPath}'. Build Servyx.Web (or the whole solution) first.",
                dllPath);
        }

        return dllPath;
    }
}
