using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>Dependency-injection registration for viewing a remote Docker container over SSH.</summary>
/// <remarks>
/// <para>
/// Mirrors <c>Servyx.Infrastructure.Ssh.ServiceCollectionExtensions.AddServyxSsh</c>'s guard shape exactly:
/// the only <see cref="ITransport"/> registered here is a <see cref="WriteGuardedTransport"/> wrapping
/// <see cref="SshDockerTransport"/> wrapping <see cref="SshTransport"/>, never a bare instance of either
/// inner type under any service type. <c>TransportWriteGuardArchitectureTests</c> asserts this the same way
/// it does for every other Servyx transport.
/// </para>
/// <para>
/// <strong>No-op with nothing configured.</strong> When <paramref name="options"/> (see
/// <see cref="AddServyxSshDocker(IServiceCollection, SshDockerWiringOptions, ILogger)"/>) has
/// <see cref="SshDockerWiringOptions.Any"/> <see langword="false"/>, this call does nothing at all — in
/// particular it does not touch whatever <c>AddServyxDocker</c> already registered for
/// <see cref="ITransport"/>, <see cref="IServerDiscovery"/>, <see cref="ILogStream"/>, or
/// <see cref="IMetricsSource"/>. Only a declared host causes anything here to run.
/// </para>
/// </remarks>
public static class SshDockerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ssh+docker transport, write-guarded, and — when <paramref name="options"/> declares a
    /// host — replaces the local Docker observation surface (<see cref="ITransport"/>,
    /// <see cref="IServerDiscovery"/>, <see cref="ILogStream"/>, <see cref="IMetricsSource"/>) with one
    /// backed by that remote host instead, plus the <see cref="TargetDescriptor"/> the dashboard's probe
    /// consumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Session lifetime.</strong> <see cref="SshDockerServerDiscovery"/>, <see cref="SshDockerLogStream"/>,
    /// and <see cref="SshDockerMetricsSource"/> each take an already-connected <see cref="IExecutionTarget"/>
    /// in their constructor — there is no lazy/async-factory shape in any of the three. A dependency-injection
    /// singleton factory is a synchronous <c>Func&lt;IServiceProvider, T&gt;</c>, so obtaining a genuinely
    /// connected session inside one would ordinarily force a choice between blocking the calling thread on
    /// <see cref="ITransport.ConnectAsync"/> or wrapping the three services behind an adapter that defers the
    /// connect.
    /// </para>
    /// <para>
    /// This registration takes neither. <see cref="LazyConnectingExecutionTarget"/> is a trivial-to-construct
    /// <see cref="IExecutionTarget"/> that captures a connect delegate and does nothing with it until the
    /// first real call — <see cref="IExecutionTarget.ExecuteAsync"/>, <see cref="IExecutionTarget.ExistsAsync"/>,
    /// etc. — at which point it awaits <see cref="ITransport.ConnectAsync"/> once (a
    /// <see cref="SemaphoreSlim"/>-guarded memoization; concurrent first callers all await the same connect
    /// rather than opening a session each) and forwards to the result from then on. Resolving
    /// <see cref="IServerDiscovery"/>/<see cref="ILogStream"/>/<see cref="IMetricsSource"/> from the container
    /// therefore constructs a real <see cref="SshDockerServerDiscovery"/> (etc.) synchronously and instantly —
    /// nothing here calls <c>.Result</c> or <c>.GetAwaiter().GetResult()</c> — and the SSH connection is opened
    /// lazily, on the same first-use schedule <c>ServyxSshBackupContextSource</c> already uses for its own
    /// cached sessions, not at startup or at DI-resolution time.
    /// </para>
    /// <para>
    /// <strong>Disposal.</strong> <see cref="LazyConnectingExecutionTarget"/> is registered as the
    /// <see cref="IExecutionTarget"/> service type as well, so its cached inner session — if one was ever
    /// opened — is disposed when the container is, the same pattern <c>ServyxBackupContextSource</c> and
    /// <c>ServyxSshBackupContextSource</c> use.
    /// </para>
    /// <para>
    /// <strong>Single host.</strong> Only <c>options.Hosts[0]</c> is wired — see
    /// <see cref="SshDockerWiringOptions"/>'s remarks for why a second declared host is accepted but unused
    /// (and warned about, by <see cref="SshDockerWiringOptions.FromConfiguration"/>).
    /// </para>
    /// <para>
    /// <strong>Not silent about what it did.</strong> When a host is wired, this logs at
    /// <see cref="LogLevel.Information"/> which host key, endpoint, and container were used — never a secret
    /// value. <c>CredentialUrn</c> is a reference into <see cref="Servyx.Domain.Secrets.ISecretStore"/>, not
    /// the credential itself, so logging it is as safe as logging the endpoint.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxSshDocker(
        this IServiceCollection services, SshDockerWiringOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!options.Any)
        {
            return services;
        }

        var host = options.Hosts[0];

        logger.LogInformation(
            "ssh+docker: wired host '{HostKey}' — endpoint {Endpoint}, container {Container}, credential "
            + "{CredentialUrn}.",
            host.Name, host.Target.Endpoint, host.ContainerName,
            host.Target.CredentialUrn ?? "(none configured)");

        services.TryAddSingleton<IWriteModeResolver>(sp =>
            new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()));

        services.RemoveAll<ITransport>();
        services.AddSingleton<ITransport>(sp => new WriteGuardedTransport(
            new SshDockerTransport(
                ActivatorUtilities.CreateInstance<SshTransport>(sp),
                sp.GetService<ILoggerFactory>()),
            sp.GetRequiredService<IWriteModeResolver>()));

        services.RemoveAll<TargetDescriptor>();
        services.AddSingleton(host.Target);

        services.RemoveAll<IExecutionTarget>();
        services.AddSingleton<IExecutionTarget>(sp => new LazyConnectingExecutionTarget(
            ct => sp.GetRequiredService<ITransport>().ConnectAsync(sp.GetRequiredService<TargetDescriptor>(), ct)));

        services.RemoveAll<IServerDiscovery>();
        services.AddSingleton<IServerDiscovery>(sp =>
            new SshDockerServerDiscovery(sp.GetRequiredService<IExecutionTarget>()));

        services.RemoveAll<ILogStream>();
        services.AddSingleton<ILogStream>(sp =>
            new SshDockerLogStream(sp.GetRequiredService<IExecutionTarget>()));

        services.RemoveAll<IMetricsSource>();
        services.AddSingleton<IMetricsSource>(sp =>
            new SshDockerMetricsSource(sp.GetRequiredService<IExecutionTarget>()));

        return services;
    }

    /// <summary>
    /// An <see cref="IExecutionTarget"/> that connects on first real use instead of at construction, so a
    /// dependency-injection singleton factory can hand out a working target without blocking the resolving
    /// thread on <see cref="ITransport.ConnectAsync"/>. See <see cref="AddServyxSshDocker(IServiceCollection, SshDockerWiringOptions, ILogger)"/>'s
    /// remarks for why this exists instead of a blocking factory or an adapter wrapping the three ssh+docker
    /// observation services.
    /// </summary>
    private sealed class LazyConnectingExecutionTarget : IExecutionTarget
    {
        private readonly Func<CancellationToken, Task<IExecutionTarget>> _connect;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IExecutionTarget? _inner;

        public LazyConnectingExecutionTarget(Func<CancellationToken, Task<IExecutionTarget>> connect)
        {
            ArgumentNullException.ThrowIfNull(connect);
            _connect = connect;
        }

        public async Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).ExecuteAsync(spec, ct).ConfigureAwait(false);

        public async IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(
            CommandSpec spec, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var target = await ResolveAsync(ct).ConfigureAwait(false);
            await foreach (var chunk in target.ExecuteStreamingAsync(spec, ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        public async Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).ExistsAsync(path, ct).ConfigureAwait(false);

        public async Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).StatAsync(path, ct).ConfigureAwait(false);

        public async Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).ListDirectoryAsync(path, ct).ConfigureAwait(false);

        public async Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).OpenReadAsync(path, ct).ConfigureAwait(false);

        public async Task<FileWriteReceipt> WriteFileAsync(
            TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).WriteFileAsync(path, content, options, ct).ConfigureAwait(false);

        public async Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
            await (await ResolveAsync(ct).ConfigureAwait(false)).DeleteAsync(path, ct).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            _gate.Dispose();
            if (_inner is not null)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<IExecutionTarget> ResolveAsync(CancellationToken ct)
        {
            if (_inner is not null)
            {
                return _inner;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _inner ??= await _connect(ct).ConfigureAwait(false);
                return _inner;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
