namespace Servyx.Infrastructure.Docker;

/// <summary>
/// Abstracts the ambient facts <see cref="DockerEndpointResolver"/> needs from the operating
/// environment, so endpoint resolution can be unit tested without mutating real process-wide state
/// (environment variables) or depending on the real OS.
/// </summary>
public interface IDockerEnvironment
{
    /// <summary>Reads an environment variable, mirroring <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>Whether the current process is running on Windows.</summary>
    bool IsWindows { get; }
}

/// <summary>Default <see cref="IDockerEnvironment"/> backed by the real process environment and OS.</summary>
public sealed class SystemDockerEnvironment : IDockerEnvironment
{
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc />
    public bool IsWindows => OperatingSystem.IsWindows();
}
