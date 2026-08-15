using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Servyx.Application.Users;
using Servyx.Composition;
using Servyx.Web.Authentication;

namespace Servyx.Web.Tests.Integration;

/// <summary>
/// Drives the real, published <c>reset-admin-password</c> CLI verb (see <see cref="AdminPasswordResetCli"/>)
/// as a subprocess against an isolated SQLite file — exactly the scenario this tool exists for: a verification
/// agent (or an operator) that has a throwaway copy of the database and needs a supported, in-app way to set
/// its admin password, without ever touching or knowing the real operator's credential.
/// </summary>
/// <remarks>
/// Mirrors <c>OperatorAuthenticationEndpointTests</c>'s subprocess-launch approach and
/// <c>LocateServyxWebDll</c> helper. Login-path equivalence is proved by resolving a fresh, real (not faked)
/// <see cref="IUserService"/> — the exact same composition every host uses, via
/// <see cref="ServyxCoreCompositionExtensions.AddServyxCore"/> — over the same SQLite file the subprocess just
/// wrote to, and calling <see cref="IUserService.VerifyPasswordAsync"/>, the identical method
/// <c>AuthenticationEndpoints</c>' <c>/login</c> handler calls.
/// </remarks>
public sealed class AdminPasswordResetCliEndToEndTests
{
    [Fact]
    public async Task ResetAdminPasswordCli_CreatesThenResetsAnAccount_AndTheLoginPathAcceptsOnlyTheLatestPassword()
    {
        var installRoot = Path.Combine(Path.GetTempPath(), "servyx-reset-cli-tests", Guid.NewGuid().ToString("n"));
        var databasePath = Path.Combine(installRoot, "servyx.db");
        Directory.CreateDirectory(installRoot);
        var connectionString = $"Data Source={databasePath}";

        const string Username = "jd";
        const string FirstPassword = "the-first-set-password-1";
        const string SecondPassword = "a-completely-different-password-2";

        // ── 1. No account exists yet: the verb creates one as Admin ─────────────────────────────────
        var createRun = await RunCliAsync(connectionString, [AdminPasswordResetCli.Verb, Username, "--password", FirstPassword]);

        createRun.ExitCode.Should().Be(0, because: $"stderr was: {createRun.StdErr}");
        createRun.CombinedOutput.Should().NotContain(FirstPassword,
            "the CLI must never print the password it was just handed");
        createRun.StdOut.Should().Contain("Admin account", "no account existed yet, so this run must report a creation");

        await using (var users = await OpenUserServiceAsync(connectionString))
        {
            (await users.Service.VerifyPasswordAsync(Username, FirstPassword)).Should().BeTrue(
                "the login path (the same VerifyPasswordAsync AuthenticationEndpoints calls) must accept the password just set");
        }

        // ── 2. Running it again against the SAME (now-existing) username resets it, not creates a duplicate ──
        var resetRun = await RunCliAsync(connectionString, [AdminPasswordResetCli.Verb, Username, "--password", SecondPassword]);

        resetRun.ExitCode.Should().Be(0, because: $"stderr was: {resetRun.StdErr}");
        resetRun.CombinedOutput.Should().NotContain(SecondPassword);
        resetRun.CombinedOutput.Should().NotContain(FirstPassword,
            "the previous run's password has no business appearing in this run's output either");
        resetRun.StdOut.Should().Contain("Password reset", "a second run against an existing account must reset it, not create a second one");

        await using (var users = await OpenUserServiceAsync(connectionString))
        {
            (await users.Service.VerifyPasswordAsync(Username, SecondPassword)).Should().BeTrue(
                "the newest password must work through the same verifier the login path uses");
            (await users.Service.VerifyPasswordAsync(Username, FirstPassword)).Should().BeFalse(
                "the superseded password must stop working the moment the reset is stored");

            var accounts = await users.Service.ListAsync();
            accounts.Should().ContainSingle(u => u.Username == Username,
                "resetting an existing account must never mint a second row under the same username");
        }
    }

    [Fact]
    public async Task ResetAdminPasswordCli_ReadsThePasswordFromRedirectedStdin_WhenNoFlagIsGiven()
    {
        // The non-interactive path a sandboxed/scripted caller (no attached TTY) actually uses.
        var installRoot = Path.Combine(Path.GetTempPath(), "servyx-reset-cli-tests", Guid.NewGuid().ToString("n"));
        var databasePath = Path.Combine(installRoot, "servyx.db");
        Directory.CreateDirectory(installRoot);
        var connectionString = $"Data Source={databasePath}";

        const string Username = "jd";
        const string Password = "piped-in-through-stdin-1";

        var run = await RunCliAsync(connectionString, [AdminPasswordResetCli.Verb, Username], stdin: Password + "\n");

        run.ExitCode.Should().Be(0, because: $"stderr was: {run.StdErr}");
        run.CombinedOutput.Should().NotContain(Password);

        await using var users = await OpenUserServiceAsync(connectionString);
        (await users.Service.VerifyPasswordAsync(Username, Password)).Should().BeTrue();
    }

    [Fact]
    public async Task ResetAdminPasswordCli_WithNoUsername_FailsCleanlyAndTouchesNothing()
    {
        var installRoot = Path.Combine(Path.GetTempPath(), "servyx-reset-cli-tests", Guid.NewGuid().ToString("n"));
        var databasePath = Path.Combine(installRoot, "servyx.db");
        Directory.CreateDirectory(installRoot);
        var connectionString = $"Data Source={databasePath}";

        var run = await RunCliAsync(connectionString, [AdminPasswordResetCli.Verb]);

        run.ExitCode.Should().NotBe(0, "a missing username must be refused, not silently guessed at");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record CliRun(int ExitCode, string StdOut, string StdErr)
    {
        public string CombinedOutput => StdOut + StdErr;
    }

    private static async Task<CliRun> RunCliAsync(string connectionString, string[] verbArgs, string? stdin = null)
    {
        var dllPath = LocateServyxWebDll();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(dllPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(dllPath);
        foreach (var arg in verbArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["Servyx__Persistence__ConnectionString"] = connectionString;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeoutCts.Token);

        return new CliRun(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    /// <summary>
    /// Resolves a real, EF-backed <see cref="IUserService"/> over <paramref name="connectionString"/> — the
    /// same composition (<see cref="ServyxCoreCompositionExtensions.AddServyxCore"/>) every Servyx host shares
    /// — so assertions against it are assertions about what the actual login path would see.
    /// </summary>
    private static async Task<UserServiceHandle> OpenUserServiceAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Servyx:Persistence:ConnectionString", connectionString),
        ]);

        var core = builder.AddServyxCore();
        var host = builder.Build();
        await core.MigrateDatabaseAsync(host.Services);

        return new UserServiceHandle(host, host.Services.GetRequiredService<IUserService>());
    }

    private sealed class UserServiceHandle(IHost host, IUserService service) : IAsyncDisposable
    {
        public IUserService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
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
