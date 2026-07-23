namespace Servyx.Domain.Connectors;

/// <summary>
/// The network location a connector reaches, independent of the credentials used to authenticate to it.
/// Distinct from <see cref="ConnectorDescriptor.Endpoint"/> (a display/opaque string suitable for a
/// <see cref="ConnectorKey"/>): this is the structured form connector implementations actually dial.
/// </summary>
/// <param name="Host">The hostname or IP address to connect to.</param>
/// <param name="Port">The port to connect to.</param>
/// <param name="Options">
/// Additional connector-specific options (e.g. a jump-host, a preferred key algorithm hint). Never a
/// credential — see <see cref="Secrets.SecretUrn"/> for how credentials are referenced instead.
/// </param>
public sealed record EndpointDescriptor(
    string Host,
    int Port,
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Renders this endpoint in <c>host:port</c> form, suitable for logging and host-key lookups.</summary>
    public override string ToString() => $"{Host}:{Port}";
}
