namespace Servyx.Domain.Connectors;

/// <summary>
/// A specific, user-configured instance of a connector kind — as opposed to <see cref="Transport.ITransport"/>,
/// which describes a stateless kind of pipe. See <c>docs/connectors.md</c>, "<c>ITransport</c> vs
/// <c>IConnector</c>".
/// </summary>
/// <param name="ConnectorId">Stable identifier for this connector instance, e.g. <c>"ssh-prod-1"</c>.</param>
/// <param name="Kind">The connector kind, e.g. <c>"ssh"</c>, <c>"sftp"</c>, <c>"docker"</c>.</param>
/// <param name="DisplayName">A human-readable name shown in the UI.</param>
/// <param name="TransportId">The <see cref="Transport.ITransport.TransportId"/> this connector is built on.</param>
/// <param name="Endpoint">
/// A display/opaque endpoint string, e.g. <c>"ssh:steam@10.0.0.4:22"</c>. Implementations that need a
/// structured host/port pair use <see cref="EndpointDescriptor"/> instead; this string exists for display
/// and for <see cref="ConnectorKey.EndpointKey"/>.
/// </param>
/// <param name="CredentialRefs">
/// Secret URNs (as strings — see <see cref="Secrets.SecretUrn"/>) this connector resolves credentials from.
/// Never a literal credential.
/// </param>
/// <param name="Trust">The host-key trust posture for this connector.</param>
/// <param name="Timeouts">Timeout and concurrency policy for sessions opened against this connector.</param>
/// <param name="DeclaredChannels">
/// The channels this connector kind normally supports. A specific instance's
/// <see cref="IConnector.AvailableChannels"/> is always a subset of this — see the remarks there.
/// </param>
public sealed record ConnectorDescriptor(
    string ConnectorId,
    string Kind,
    string DisplayName,
    string TransportId,
    string Endpoint,
    IReadOnlyList<string> CredentialRefs,
    TrustPolicy Trust,
    TimeoutPolicy Timeouts,
    ConnectorChannel DeclaredChannels);
