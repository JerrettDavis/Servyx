using Servyx.Domain.Transport;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// A test-only <see cref="ITransport"/> decorator that adds
/// <see cref="TransportCapabilities.ContainerScopedFiles"/> to whatever it wraps, so a host-rooted stand-in
/// (typically <c>LocalProcessTransport</c> over a temp directory) can play the part of the Docker Engine
/// transport in tests about something other than container scoping.
/// </summary>
/// <remarks>
/// This exists so the flag has to be stated deliberately in tests that need it. It is the test-side
/// counterpart of <c>ServyxBackupContextSource</c>'s refusal: a suite that forgot the flag would be refused
/// rather than quietly exercising the misrouted path the guard exists to prevent.
/// </remarks>
internal sealed class ContainerScopedFilesTransport : ITransport
{
    private readonly ITransport _inner;

    public ContainerScopedFilesTransport(ITransport inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public string TransportId => _inner.TransportId;

    public TransportCapabilities Capabilities =>
        _inner.Capabilities | TransportCapabilities.ContainerScopedFiles;

    public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
        _inner.ProbeAsync(target, ct);

    public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default) =>
        _inner.ConnectAsync(target, ct);
}
