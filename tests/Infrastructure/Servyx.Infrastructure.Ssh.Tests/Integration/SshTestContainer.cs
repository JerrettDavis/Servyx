using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// Starts a throwaway <c>linuxserver/openssh-server</c> container for integration tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this image, not <c>Testcontainers.Sftp</c>:</b> the <c>Testcontainers.Sftp</c> module (backed by
/// <c>atmoz/sftp</c>) was evaluated first, since it's the purpose-built Testcontainers module for this
/// scenario. Its builder (<c>SftpContainerBuilder.WithUsername</c>/<c>WithPassword</c>) only exposes
/// password-based accounts — <c>atmoz/sftp</c> itself supports mounting an <c>authorized_keys</c> file for
/// key auth, but the Testcontainers module's builder has no method to configure that, and it also runs only
/// an SFTP subsystem, not a general-purpose shell (no exec capability to test at all). Confirmed by reading
/// the module's public API surface; there is no supported way to get key-based auth or exec through it.
/// </para>
/// <para>
/// <c>linuxserver/openssh-server</c>, driven through the generic <see cref="ContainerBuilder"/>, supports
/// both password auth (<c>PASSWORD_ACCESS</c>/<c>USER_PASSWORD</c>) and key auth (<c>PUBLIC_KEY</c>, a
/// literal authorized_keys line via environment variable — no bind mount required) simultaneously, plus a
/// full shell for exec testing. This is what every integration test in this project actually uses.
/// </para>
/// </remarks>
internal static class SshTestContainer
{
    private const string Image = "lscr.io/linuxserver/openssh-server:latest";
    internal const int ContainerPort = 2222;
    internal const string Username = "servyx";

    /// <summary>
    /// Builds and starts a container. If <paramref name="fixedHostPort"/> is given, the container binds to
    /// that exact host port (used only by the "changed host key" scenario, which needs two containers to
    /// occupy the same host:port in sequence); otherwise a random host port is assigned.
    /// </summary>
    public static async Task<IContainer> StartAsync(
        string? password,
        string? publicKeyLine,
        int? fixedHostPort = null,
        CancellationToken ct = default)
    {
        var environment = new Dictionary<string, string>
        {
            ["PUID"] = "1000",
            ["PGID"] = "1000",
            ["TZ"] = "Etc/UTC",
            ["USER_NAME"] = Username,
            ["SUDO_ACCESS"] = "false",
            ["PASSWORD_ACCESS"] = password is not null ? "true" : "false",
        };

        if (password is not null)
        {
            environment["USER_PASSWORD"] = password;
        }

        if (publicKeyLine is not null)
        {
            environment["PUBLIC_KEY"] = publicKeyLine;
        }

        var builder = new ContainerBuilder(Image)
            .WithEnvironment(environment)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ContainerPort));

        builder = fixedHostPort is { } port
            ? builder.WithPortBinding(port, ContainerPort)
            : builder.WithPortBinding(ContainerPort, assignRandomHostPort: true);

        var container = builder.Build();
        await container.StartAsync(ct).ConfigureAwait(false);

        // The internal-port wait strategy confirms sshd is listening; give it a brief additional moment to
        // finish key generation and start accepting the protocol banner on first boot, which very
        // occasionally lags a few hundred milliseconds behind the socket itself opening.
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

        return container;
    }
}
