using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// <see cref="IConnector"/> implementation for an SSH-reachable host, providing exec (via
/// <see cref="SshExecChannel"/>) and file access (via <see cref="SftpFileChannel"/>, or
/// <see cref="ShellFileChannel"/> when the sftp subsystem is unavailable) composed through
/// <see cref="CompositeExecutionTarget"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host-key verification happens before any command runs, as a structural guarantee rather than a
/// runtime check.</b> Each underlying SSH.NET client's <c>HostKeyReceived</c> event is wired, before
/// <c>Connect</c>/<c>ConnectAsync</c> is called, to <see cref="HostKeyGate.EnforceAsync"/>: the presented
/// key is verified via the injected <see cref="IHostKeyVerifier"/>, and <c>HostKeyEventArgs.CanTrust</c> is
/// set <see langword="true"/> only for <see cref="HostKeyVerdict.Trusted"/>. For every other verdict,
/// SSH.NET itself aborts the key exchange and <c>Connect</c>/<c>ConnectAsync</c> throws — no
/// <see cref="SshExecChannel"/>, <see cref="SftpFileChannel"/>, or <see cref="ShellFileChannel"/> object is
/// ever constructed in that case, because channel construction only happens after a successful connect.
/// </para>
/// </remarks>
public sealed class SshConnector : IConnector
{
    private readonly ISecretStore _secretStore;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SshConnector> _logger;
    private ConnectorChannel _availableChannels;

    /// <summary>Creates an <see cref="SshConnector"/> for <paramref name="descriptor"/>.</summary>
    public SshConnector(
        ConnectorDescriptor descriptor,
        ISecretStore secretStore,
        IHostKeyVerifier hostKeyVerifier,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        Descriptor = descriptor;
        _secretStore = secretStore;
        _hostKeyVerifier = hostKeyVerifier;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SshConnector>();
    }

    /// <inheritdoc />
    public ConnectorDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ConnectorChannel AvailableChannels => _availableChannels;

    /// <inheritdoc />
    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var issues = new List<string>();
        var reachable = false;
        var working = ConnectorChannel.None;

        IConnectorSession? session = null;
        try
        {
            session = await OpenAsync(ct).ConfigureAwait(false);
            reachable = true;
            working = session.AvailableChannels;
        }
        catch (HostKeyRejectedException ex)
        {
            issues.Add($"Host key verification failed for {Descriptor.Endpoint}: verdict was {ex.Verdict}. Refusing to connect until this is resolved.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"Connection to {Descriptor.Endpoint} failed: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            stopwatch.Stop();
        }

        _availableChannels = working;
        return ConnectorHealthBuilder.Build(Descriptor.DeclaredChannels, working, reachable, stopwatch.Elapsed, DateTimeOffset.UtcNow, issues);
    }

    /// <inheritdoc />
    public async Task<IConnectorSession> OpenAsync(CancellationToken ct = default)
    {
        var (endpoint, usernameHint) = SshEndpoint.Parse(Descriptor.Endpoint);

        SshClient? sshClient = null;
        SftpClient? sftpClient = null;
        IExecutionTarget? execChannel = null;
        IExecutionTarget? fileChannel = null;

        try
        {
            using var credentials = await SshCredentialResolver.ResolveAsync(Descriptor, usernameHint, _secretStore, ct).ConfigureAwait(false);
            var connectionInfo = BuildConnectionInfo(endpoint, credentials);
            connectionInfo.Timeout = Descriptor.Timeouts.Connect;

            var wantsExec = (Descriptor.DeclaredChannels & ConnectorChannel.Exec) != ConnectorChannel.None;
            var wantsFile = (Descriptor.DeclaredChannels & (ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList)) != ConnectorChannel.None;

            if (wantsExec)
            {
                sshClient = new SshClient(connectionInfo);
                await ConnectWithHostKeyGateAsync(sshClient, endpoint, ct).ConfigureAwait(false);
                execChannel = new SshExecChannel(sshClient, ownsClient: true, Descriptor.Timeouts.Command);
            }

            if (wantsFile)
            {
                try
                {
                    sftpClient = new SftpClient(connectionInfo);
                    await ConnectWithHostKeyGateAsync(sftpClient, endpoint, ct).ConfigureAwait(false);
                    fileChannel = new SftpFileChannel(sftpClient, ownsClient: true, _loggerFactory.CreateLogger<SftpFileChannel>());
                }
                catch (HostKeyRejectedException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is SshConnectionException or SshAuthenticationException or NotSupportedException or System.Net.Sockets.SocketException)
                {
                    sftpClient?.Dispose();
                    sftpClient = null;

                    if (sshClient is not null)
                    {
                        _logger.LogInformation(ex, "SFTP unavailable for {Endpoint}; falling back to shell-synthesized file access over the exec channel.", endpoint);
                        fileChannel = new ShellFileChannel(sshClient, ownsClient: false, Descriptor.Timeouts.Command);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "SFTP unavailable for {Endpoint} and no exec channel was requested to fall back to.", endpoint);
                    }
                }
            }

            if (execChannel is null && fileChannel is null)
            {
                throw new InvalidOperationException($"Connector '{Descriptor.ConnectorId}' declares no usable channels (DeclaredChannels = {Descriptor.DeclaredChannels}).");
            }

            var composite = new CompositeExecutionTarget(execChannel, fileChannel);
            return new SshConnectorSession(composite);
        }
        catch
        {
            sshClient?.Dispose();
            sftpClient?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> ResolveIdentityAsync(CancellationToken ct = default)
    {
        var (endpoint, usernameHint) = SshEndpoint.Parse(Descriptor.Endpoint);
        using var credentials = await SshCredentialResolver.ResolveAsync(Descriptor, usernameHint, _secretStore, ct).ConfigureAwait(false);
        return $"{credentials.Username}@{endpoint.Host}:{endpoint.Port}";
    }

    /// <summary>
    /// Connects <paramref name="client"/> with the host-key gate wired onto its <c>HostKeyReceived</c>
    /// event, and translates SSH.NET's generic <see cref="SshConnectionException"/> ("Host key could not be
    /// verified.") into a specific <see cref="HostKeyRejectedException"/> carrying the verdict that caused
    /// it. See the type-level remarks for why this makes host-key rejection structural rather than a
    /// runtime check: <see cref="HostKeyEventArgs.CanTrust"/> defaults to <see langword="false"/> here
    /// (fail closed) and is flipped to <see langword="true"/> only by
    /// <see cref="HostKeyGate.EnforceAsync"/>'s <c>onTrusted</c> callback, which fires only for
    /// <see cref="HostKeyVerdict.Trusted"/> — so any other verdict leaves <c>CanTrust</c> false, SSH.NET
    /// aborts the key exchange itself, and no channel object is ever constructed from this client.
    /// </summary>
    private async Task ConnectWithHostKeyGateAsync(BaseClient client, EndpointDescriptor endpoint, CancellationToken ct)
    {
        HostKeyVerdict? capturedVerdict = null;

        void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
        {
            e.CanTrust = false; // Fail closed: only flipped true below, and only for a Trusted verdict.

            capturedVerdict = HostKeyGate.EnforceAsync(
                _hostKeyVerifier,
                endpoint.Host,
                endpoint.Port,
                e.HostKeyName,
                e.HostKey,
                Descriptor.Trust,
                onTrusted: () => e.CanTrust = true,
                ct: CancellationToken.None).GetAwaiter().GetResult();

            if (capturedVerdict != HostKeyVerdict.Trusted)
            {
                _logger.LogWarning("Host key for {Endpoint} was not trusted (verdict: {Verdict}).", endpoint, capturedVerdict);
            }
        }

        client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (SshConnectionException ex) when (capturedVerdict is not null and not HostKeyVerdict.Trusted)
        {
            throw new HostKeyRejectedException(endpoint.Host, endpoint.Port, capturedVerdict.Value, ex);
        }
        finally
        {
            client.HostKeyReceived -= OnHostKeyReceived;
        }
    }

    private static ConnectionInfo BuildConnectionInfo(EndpointDescriptor endpoint, ResolvedSshCredentials credentials)
    {
        var methods = new List<AuthenticationMethod>();

        if (credentials.PrivateKey is not null)
        {
            using var keyStream = new MemoryStream(credentials.PrivateKey.Value.ToArray());
            var keyFile = credentials.Passphrase is not null
                ? new PrivateKeyFile(keyStream, credentials.Passphrase.ToUtf8String())
                : new PrivateKeyFile(keyStream);
            methods.Add(new PrivateKeyAuthenticationMethod(credentials.Username, keyFile));
        }

        if (credentials.Password is not null)
        {
            methods.Add(new PasswordAuthenticationMethod(credentials.Username, credentials.Password.Value.ToArray()));
        }

        return new ConnectionInfo(endpoint.Host, endpoint.Port, credentials.Username, methods.ToArray());
    }
}

/// <summary>
/// The <see cref="IConnectorSession"/> returned by <see cref="SshConnector.OpenAsync"/>, wrapping a
/// <see cref="CompositeExecutionTarget"/>.
/// </summary>
internal sealed class SshConnectorSession : IConnectorSession
{
    private readonly CompositeExecutionTarget _target;

    public SshConnectorSession(CompositeExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <inheritdoc />
    public ConnectorChannel AvailableChannels => _target.AvailableChannels;

    /// <inheritdoc />
    public IExecutionTarget ExecutionTarget => _target;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _target.DisposeAsync();
}
