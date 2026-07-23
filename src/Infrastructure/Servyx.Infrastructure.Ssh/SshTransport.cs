using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// <see cref="ITransport"/> implementation reaching a host over SSH (exec) and SFTP (files). Unlike
/// <see cref="IConnector"/>, this is the stateless, singleton-lifetime "kind of pipe" — see
/// <c>docs/connectors.md</c>, "<c>ITransport</c> vs <c>IConnector</c>". Each <see cref="ConnectAsync"/>
/// call builds a throwaway <see cref="SshConnector"/> from <paramref name="target"/> internally; the
/// persisted, pooled, credentialed connector instance a real deployment uses is <see cref="SshConnector"/>
/// itself, addressed through <see cref="IConnectorPool"/>.
/// </summary>
public sealed class SshTransport : ITransport
{
    private readonly ISecretStore _secretStore;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates an <see cref="SshTransport"/>.</summary>
    public SshTransport(ISecretStore secretStore, IHostKeyVerifier hostKeyVerifier, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        _secretStore = secretStore;
        _hostKeyVerifier = hostKeyVerifier;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public string TransportId => "ssh";

    /// <inheritdoc />
    /// <remarks>
    /// Declares the full potential of the SSH+SFTP pairing at the transport-kind level. A specific
    /// <see cref="IConnector"/> instance's actually-observed channels (e.g. missing file access because the
    /// sftp subsystem is disabled on this particular host) are reported separately, per-instance, via
    /// <see cref="ConnectorHealth"/> — see <c>docs/connectors.md</c> for why conflating the two would make
    /// this static property instance-varying.
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.ExecuteCommand |
        TransportCapabilities.StreamOutput |
        TransportCapabilities.StreamStdin |
        TransportCapabilities.FileRead |
        TransportCapabilities.FileWrite |
        TransportCapabilities.DirectoryList |
        TransportCapabilities.ProcessApi;

    /// <inheritdoc />
    public async Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var stopwatch = Stopwatch.StartNew();
        IExecutionTarget? session = null;
        try
        {
            session = await ConnectAsync(target, ct).ConfigureAwait(false);
            stopwatch.Stop();
            return new TargetHealth(true, stopwatch.Elapsed, "SSH connection established and verified.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TargetHealth(false, null, $"SSH host unreachable: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        var descriptor = BuildConnectorDescriptor(target);
        var connector = new SshConnector(descriptor, _secretStore, _hostKeyVerifier, _loggerFactory);
        var session = await connector.OpenAsync(ct).ConfigureAwait(false);
        return session.ExecutionTarget;
    }

    /// <summary>
    /// Builds a throwaway <see cref="ConnectorDescriptor"/> from a <see cref="TargetDescriptor"/>. By
    /// convention: <see cref="TargetDescriptor.CredentialUrn"/> is the password or private-key secret (its
    /// <see cref="SecretUrn.Name"/> segment distinguishes which); <see cref="TargetDescriptor.Options"/> may
    /// additionally supply <c>"passphraseUrn"</c>, <c>"usernameUrn"</c>, <c>"trustPolicy"</c>
    /// (<c>"requirePinned"</c> [default] or <c>"trustOnFirstUse"</c>), <c>"pinnedFingerprints"</c>
    /// (comma-separated), and <c>"declaredChannels"</c> (comma-separated <see cref="ConnectorChannel"/>
    /// member names; defaults to exec plus full file access).
    /// </summary>
    private static ConnectorDescriptor BuildConnectorDescriptor(TargetDescriptor target)
    {
        var credentialRefs = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.CredentialUrn))
        {
            credentialRefs.Add(target.CredentialUrn);
        }

        if (target.Options.TryGetValue("passphraseUrn", out var passphraseUrn) && !string.IsNullOrWhiteSpace(passphraseUrn))
        {
            credentialRefs.Add(passphraseUrn);
        }

        if (target.Options.TryGetValue("usernameUrn", out var usernameUrn) && !string.IsNullOrWhiteSpace(usernameUrn))
        {
            credentialRefs.Add(usernameUrn);
        }

        var declaredChannels = ConnectorChannel.Exec | ConnectorChannel.Stdin |
            ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList;
        if (target.Options.TryGetValue("declaredChannels", out var channelsText) && !string.IsNullOrWhiteSpace(channelsText))
        {
            declaredChannels = ParseChannels(channelsText);
        }

        return new ConnectorDescriptor(
            ConnectorId: target.Endpoint,
            Kind: "ssh",
            DisplayName: target.Endpoint,
            TransportId: "ssh",
            Endpoint: target.Endpoint,
            CredentialRefs: credentialRefs,
            Trust: BuildTrustPolicy(target.Options),
            Timeouts: TimeoutPolicy.Default,
            DeclaredChannels: declaredChannels);
    }

    private static TrustPolicy BuildTrustPolicy(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("pinnedFingerprints", out var pinned) && !string.IsNullOrWhiteSpace(pinned))
        {
            return new TrustPolicy.PinnedFingerprints(pinned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (options.TryGetValue("trustPolicy", out var mode) && string.Equals(mode, "trustOnFirstUse", StringComparison.OrdinalIgnoreCase))
        {
            return new TrustPolicy.TrustOnFirstUse();
        }

        return new TrustPolicy.RequirePinned();
    }

    private static ConnectorChannel ParseChannels(string commaSeparated)
    {
        var result = ConnectorChannel.None;
        foreach (var part in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ConnectorChannel>(part, ignoreCase: true, out var channel))
            {
                result |= channel;
            }
        }

        return result;
    }
}
