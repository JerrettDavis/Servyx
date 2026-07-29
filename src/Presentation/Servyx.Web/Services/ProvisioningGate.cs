namespace Servyx.Web.Services;

/// <summary>
/// The single, explicit answer to "may this process provision infrastructure?", resolved once at startup
/// from configuration and injected wherever the question is asked.
/// </summary>
/// <remarks>
/// <para>
/// Provisioning creates real infrastructure and — at any provider that is not the local Docker daemon —
/// spends real money. The gate therefore defaults to <see langword="false"/> whenever the key is absent,
/// unparseable, or explicitly false, and turning it on is a deliberate act by whoever wrote the
/// configuration file. See the comment at the composition root in <c>Program.cs</c>.
/// </para>
/// <para>
/// Servyx now requires the operator password before any page is served — see
/// <see cref="AuthenticationGate"/>, which defaults to <see langword="true"/> for exactly the mirrored
/// reason this one defaults to <see langword="false"/>. That does not make an open provisioning gate
/// cheap: it means the capability belongs to whoever holds that one password rather than to anyone on the
/// network path. If <see cref="AuthenticationGate"/> has been explicitly switched off, it means the latter
/// again, and <see cref="StartupSafetyWarnings"/> logs that combination at
/// <see cref="LogLevel.Critical"/> on every start.
/// </para>
/// <para>
/// This type only reports the decision. The enforcement that matters is in <c>Program.cs</c>: when the gate
/// is closed, no <see cref="Domain.Provisioning.IProvisioner"/> is registered in dependency injection at
/// all, so there is no object in the process capable of creating anything, regardless of what any component
/// renders. The gate being consulted in the UI is defence in depth, not the defence.
/// </para>
/// </remarks>
public sealed class ProvisioningGate
{
    /// <summary>The configuration key that opens the gate. Absent means closed.</summary>
    public const string ConfigurationKey = "Servyx:Provisioning:Enabled";

    /// <summary>A closed gate, for hosts and tests that do not configure provisioning at all.</summary>
    public static readonly ProvisioningGate Closed = new(enabled: false);

    /// <summary>Creates a gate in the given state.</summary>
    /// <param name="enabled">Whether provisioning is permitted in this process.</param>
    public ProvisioningGate(bool enabled) => Enabled = enabled;

    /// <summary>Whether provisioning is permitted in this process.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Reads <see cref="ConfigurationKey"/> from <paramref name="configuration"/>. Anything that is not a
    /// value <see cref="bool.TryParse(string?, out bool)"/> accepts as <see langword="true"/> — including a
    /// missing key, an empty string, and a typo — yields a closed gate. A misconfiguration must fail
    /// closed, never open.
    /// </summary>
    /// <param name="configuration">The application configuration to read from.</param>
    public static ProvisioningGate FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ProvisioningGate(bool.TryParse(configuration[ConfigurationKey], out var enabled) && enabled);
    }
}
