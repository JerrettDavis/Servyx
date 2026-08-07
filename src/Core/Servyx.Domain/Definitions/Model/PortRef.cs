namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// A port value as declared in a definition: either a literal number
/// (<c>capabilities.network[].port: 8211</c>) or a reference to the setting that determines it at runtime
/// (<c>control.channels[].port: "${RCON_PORT}"</c>). Closed so a consumer must handle both shapes rather
/// than assuming every declared port is a fixed number.
/// </summary>
public abstract record PortRef
{
    private PortRef()
    {
    }

    /// <summary>A fixed port number.</summary>
    /// <param name="Port">The port number.</param>
    public sealed record Literal(int Port) : PortRef;

    /// <summary>The port determined by a settings-catalogue entry's current value.</summary>
    /// <param name="Key">The referenced <see cref="SettingDescriptor.Key"/>, e.g. <c>RCON_PORT</c>.</param>
    public sealed record SettingRef(string Key) : PortRef;
}
