using Docker.DotNet;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// Creates <see cref="IDockerClient"/> instances for a given endpoint. Exists as a seam so
/// <see cref="DockerTransport"/> and friends can be unit tested against a substitute client without a
/// real Docker daemon.
/// </summary>
public interface IDockerClientFactory
{
    /// <summary>Creates a client connected to the given endpoint. Does not itself verify reachability.</summary>
    IDockerClient Create(Uri endpoint);
}

/// <summary>Default <see cref="IDockerClientFactory"/>, backed by <see cref="DockerClientConfiguration"/>.</summary>
public sealed class DockerClientFactory : IDockerClientFactory
{
    /// <inheritdoc />
    public IDockerClient Create(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // Deliberately not disposed here: the created DockerClient holds a reference to this
        // configuration (via IDockerClient.Configuration) and uses its Credentials for the lifetime
        // of the client, so disposing it immediately would be unsafe for non-anonymous credentials.
        // Disposing the returned IDockerClient disposes the configuration transitively.
        var configuration = new DockerClientConfiguration(endpoint);
        return configuration.CreateClient();
    }
}
