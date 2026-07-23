using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// Resolves the Docker Engine API endpoint to connect to, honouring (in priority order) an explicit
/// <see cref="TargetDescriptor.Endpoint"/>, the <c>DOCKER_HOST</c> environment variable, and finally an
/// OS-appropriate default (the Docker Desktop named pipe on Windows, the standard Unix socket
/// elsewhere). Supports the <c>npipe://</c>, <c>unix://</c>, and <c>tcp://</c> (or <c>http(s)://</c>)
/// schemes that Docker.DotNet understands.
/// </summary>
public static class DockerEndpointResolver
{
    /// <summary>Default endpoint for local Docker Desktop's WSL2/Linux-containers engine on Windows.</summary>
    public const string DefaultWindowsEndpoint = "npipe://./pipe/dockerDesktopLinuxEngine";

    /// <summary>Default endpoint for the standard Docker Engine Unix socket.</summary>
    public const string DefaultUnixEndpoint = "unix:///var/run/docker.sock";

    private static readonly string[] SupportedSchemes = ["npipe", "unix", "tcp", "http", "https"];

    /// <summary>
    /// Resolves the Docker endpoint for the given <paramref name="target"/>. Resolution order:
    /// <list type="number">
    /// <item><description><paramref name="target"/>.<see cref="TargetDescriptor.Endpoint"/>, if non-empty.</description></item>
    /// <item><description>The <c>DOCKER_HOST</c> environment variable, if set and non-empty.</description></item>
    /// <item><description>An OS-appropriate default.</description></item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The resolved candidate string is empty, is not a well-formed absolute URI, or uses a scheme
    /// other than <c>npipe</c>, <c>unix</c>, <c>tcp</c>, <c>http</c>, or <c>https</c>.
    /// </exception>
    public static Uri Resolve(TargetDescriptor target, IDockerEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        environment ??= new SystemDockerEnvironment();

        var candidate = SelectCandidate(target.Endpoint, environment);
        return ParseEndpoint(candidate);
    }

    /// <summary>
    /// Resolves an endpoint from raw inputs directly, without requiring a <see cref="TargetDescriptor"/>.
    /// Useful for default (non-target-specific) client construction, e.g. in DI registration.
    /// </summary>
    public static Uri Resolve(string? explicitEndpoint, IDockerEnvironment? environment = null)
    {
        environment ??= new SystemDockerEnvironment();
        var candidate = SelectCandidate(explicitEndpoint, environment);
        return ParseEndpoint(candidate);
    }

    private static string SelectCandidate(string? explicitEndpoint, IDockerEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            return explicitEndpoint;
        }

        var dockerHost = environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            return dockerHost;
        }

        return environment.IsWindows ? DefaultWindowsEndpoint : DefaultUnixEndpoint;
    }

    /// <summary>Parses and validates a candidate endpoint string. Exposed internally for direct unit testing.</summary>
    internal static Uri ParseEndpoint(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("Docker endpoint must not be empty.", nameof(candidate));
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"'{candidate}' is not a valid Docker endpoint URI.", nameof(candidate));
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (Array.IndexOf(SupportedSchemes, scheme) < 0)
        {
            throw new ArgumentException(
                $"Unsupported Docker endpoint scheme '{uri.Scheme}' in '{candidate}'. Expected one of: {string.Join(", ", SupportedSchemes)}.",
                nameof(candidate));
        }

        return uri;
    }
}
