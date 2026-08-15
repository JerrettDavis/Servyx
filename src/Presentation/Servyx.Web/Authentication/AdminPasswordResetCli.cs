using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Users;
using Servyx.Composition;
using Servyx.Domain.Entities;

namespace Servyx.Web.Authentication;

/// <summary>
/// A break-glass administrative CLI verb — <c>dotnet run -- reset-admin-password &lt;username&gt;</c> — that
/// sets or resets one account's password directly against whatever database
/// <c>Servyx:Persistence:ConnectionString</c> (or the process's default <c>servyx-data/servyx.db</c>) resolves
/// to, through the exact same <see cref="Servyx.Domain.Secrets.PasswordHash"/> verifier
/// <see cref="IUserService"/>'s own <c>CreateAsync</c>/<c>ChangePasswordAsync</c> already use.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> There is deliberately no in-app way to recover a lost password without
/// already holding one: <see cref="IUserService.ChangePasswordAsync"/> requires the current password, and
/// nothing in the web UI can set an <em>existing</em> account's credential out of band. That is correct for the
/// running production instance, but it left exactly one legitimate, supported gap unaddressed: pointing a
/// throwaway copy of the database at this same binary (e.g. via <c>Servyx:Persistence:ConnectionString</c>)
/// for local verification, with no way to sign in to it, without ever touching — or even knowing — the real
/// operator's password. This verb is that gap's supported answer, in place of hand-writing the
/// <c>PasswordHash</c> column with raw SQL.
/// </para>
/// <para>
/// <strong>Explicit and inert by default.</strong> This is not a flag consulted during normal startup, an
/// environment variable checked on every boot, or anything reachable over HTTP — <see cref="IsInvoked"/> only
/// returns <see langword="true"/> when <c>reset-admin-password</c> is literally the first command-line
/// argument, which only happens when an operator (or a script) deliberately runs it. <c>Program.cs</c> checks
/// this before any of the normal web-host composition (authentication, Razor components, Kestrel) even starts,
/// so the moment this verb is recognized, the process builds just enough of the shared composition root
/// (<see cref="ServyxCoreCompositionExtensions.AddServyxCore"/>) to reach <see cref="IUserService"/>, does one
/// write, and exits — it never opens a port and is not reachable remotely by definition.
/// </para>
/// <para>
/// <strong>Never logs the password.</strong> The plaintext is read from <c>--password &lt;value&gt;</c>,
/// redirected stdin (for scripted/non-interactive use — exactly what a sandboxed verification agent needs),
/// or a masked console prompt, held only long enough to pass to <see cref="IUserService.ResetPasswordAsync"/>
/// or <see cref="IUserService.CreateAsync"/>, and never written to the console or any logger — only the
/// username, the outcome, and (on failure) the non-secret reason are.
/// </para>
/// </remarks>
public static class AdminPasswordResetCli
{
    /// <summary>The command-line verb that invokes this tool.</summary>
    public const string Verb = "reset-admin-password";

    private const string Actor = "cli/reset-admin-password";

    /// <summary>
    /// Whether <paramref name="args"/> invokes this verb — <see cref="Verb"/>, case-insensitively, as the
    /// first argument. Every other shape of <paramref name="args"/> (empty, Kestrel's own <c>--urls</c>, an
    /// unrelated first token) returns <see langword="false"/>, which is what keeps this tool from ever firing
    /// on an ordinary web-host launch.
    /// </summary>
    public static bool IsInvoked(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the verb: resolves <paramref name="args"/>' username and password, builds just enough of the
    /// composition root over <paramref name="builder"/> to reach a durable <see cref="IUserService"/>, and
    /// sets that account's password — creating it as <see cref="UserRole.Admin"/> first if no account exists
    /// yet under that username. Returns the process exit code the caller should use (0 on success).
    /// </summary>
    public static async Task<int> RunAsync(string[] args, WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(builder);

        var username = args.Length > 1 ? args[1].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Error.WriteLine($"Usage: dotnet run -- {Verb} <username> [--password <new-password>]");
            return 1;
        }

        var password = TryReadFlagValue(args, "--password")
            ?? (Console.IsInputRedirected ? Console.ReadLine() : ReadPasswordMasked($"New password for '{username}': "));

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("No password was supplied.");
            return 1;
        }

        var core = builder.AddServyxCore();
        await using var app = builder.Build();
        await core.MigrateDatabaseAsync(app.Services).ConfigureAwait(false);

        var users = app.Services.GetRequiredService<IUserService>();

        var reset = await users.ResetPasswordAsync(username, password, Actor).ConfigureAwait(false);
        switch (reset.Outcome)
        {
            case ResetPasswordOutcome.Reset:
                Console.WriteLine($"Password reset for '{username}'.");
                return 0;

            case ResetPasswordOutcome.UserNotFound:
                var created = await users.CreateAsync(username, password, UserRole.Admin, Actor).ConfigureAwait(false);
                if (created.Outcome != CreateUserOutcome.Created)
                {
                    Console.Error.WriteLine($"Could not create '{username}': {created.Detail}");
                    return 1;
                }

                Console.WriteLine($"No account existed under '{username}'; created a new Admin account with the supplied password.");
                return 0;

            default:
                Console.Error.WriteLine($"Could not reset '{username}': {reset.Detail}");
                return 1;
        }
    }

    /// <summary>The value following <paramref name="flag"/> in <paramref name="args"/>, or <see langword="null"/> if absent.</summary>
    private static string? TryReadFlagValue(string[] args, string flag)
    {
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Prompts on the console with each keystroke masked as <c>*</c>, so the password never appears in a scrollback buffer.</summary>
    private static string ReadPasswordMasked(string prompt)
    {
        Console.Write(prompt);

        var buffer = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return buffer.ToString();
    }
}
