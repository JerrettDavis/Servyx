namespace Servyx.Composition;

/// <summary>
/// The single, explicit answer to "must a caller prove who they are before this process serves them
/// anything?", resolved once at startup from configuration and injected wherever the question is asked.
/// </summary>
/// <remarks>
/// <para>
/// This is the deliberate mirror image of <see cref="ProvisioningGate"/>, and its default is the opposite
/// one on purpose. Provisioning is a capability, so an unreadable or absent flag must leave it
/// <em>off</em>; authentication is a protection, so an unreadable or absent flag must leave it <em>on</em>.
/// Both rules are the same rule — a misconfiguration must never widen what an anonymous caller can do.
/// </para>
/// <para>
/// Only an explicit, parseable <c>false</c> turns authentication off. A typo (<c>"no"</c>, <c>"0"</c>,
/// <c>"disabled"</c>), an empty string, or a missing key all leave the app authenticated, because the cost
/// of getting this wrong in the other direction is an unauthenticated administrator on whatever network
/// path can reach the web port.
/// </para>
/// <para>
/// This type only reports the decision. The enforcement that matters lives in <c>Program.cs</c>: when the
/// gate is open, an <see cref="Microsoft.AspNetCore.Authorization.AuthorizationOptions.FallbackPolicy"/>
/// requiring an authenticated user is installed, so <em>every</em> endpoint that does not explicitly opt out
/// is protected — including pages that do not exist yet. The gate being consulted in the UI (for example by
/// <c>AuthenticationBoundary</c> or the copy on <c>/deploy</c>) is defence in depth, not the defence.
/// </para>
/// </remarks>
public sealed class AuthenticationGate
{
    /// <summary>The configuration key that turns authentication off. Absent means on.</summary>
    public const string ConfigurationKey = "Servyx:Authentication:Enabled";

    /// <summary>
    /// An enforcing gate, used as the fail-closed default anywhere the gate has not been registered — so a
    /// component that forgot to compose it demands a login rather than silently granting one.
    /// </summary>
    public static readonly AuthenticationGate Enforced = new(enabled: true);

    /// <summary>An open gate, for the explicitly-configured, documented unauthenticated mode.</summary>
    public static readonly AuthenticationGate Disabled = new(enabled: false);

    /// <summary>Creates a gate in the given state.</summary>
    /// <param name="enabled">Whether authentication is required in this process.</param>
    public AuthenticationGate(bool enabled) => Enabled = enabled;

    /// <summary>Whether authentication is required in this process.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Reads <see cref="ConfigurationKey"/> from <paramref name="configuration"/>. Authentication is left
    /// enabled unless the value is something <see cref="bool.TryParse(string?, out bool)"/> accepts and that
    /// parses to <see langword="false"/>. A missing key, an empty string, and a typo all leave it enabled: a
    /// misconfiguration must fail closed, never open.
    /// </summary>
    /// <param name="configuration">The application configuration to read from.</param>
    public static AuthenticationGate FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[ConfigurationKey];
        var explicitlyDisabled = bool.TryParse(raw, out var parsed) && !parsed;

        return new AuthenticationGate(!explicitlyDisabled);
    }
}
