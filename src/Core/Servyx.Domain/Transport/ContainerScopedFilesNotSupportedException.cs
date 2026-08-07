namespace Servyx.Domain.Transport;

/// <summary>
/// Thrown when something asks a transport for a session whose file and directory operations are rooted
/// <em>inside</em> a container, and that transport's file channel actually reaches the <em>host</em>
/// filesystem instead.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the alternative is silent misrouting, and misrouting writes.</strong> A
/// transport can be perfectly container-aware on its control plane and host-scoped on its file plane — the
/// ssh+docker transport is exactly that shape: lifecycle, discovery, logs and metrics all run
/// <c>docker &lt;verb&gt; &lt;container&gt;</c> over an SSH exec channel and are correct, while
/// <see cref="IExecutionTarget"/>'s file members land on SFTP against the SSH host's own root. Handed a
/// descriptor carrying a container root such as <c>/palworld</c>, a host-scoped file channel does not fail —
/// it succeeds against the wrong filesystem. A capture reads nothing and produces an empty archive the
/// operator believes is a backup; a restore writes the archive's bytes into real host paths outside any
/// container, bounded only by the SSH user's permissions.
/// </para>
/// <para>
/// <strong>Neither the write guard nor path containment catches it.</strong>
/// <see cref="WriteGuardedExecutionTarget"/> answers "may this server be written to at all", which a backup
/// restore has already had to answer <em>yes</em> to before it runs; <c>SandboxedPathResolver</c> keeps a
/// path inside its declared root, and the root itself is what has been lost. Refusing the session outright
/// is the only barrier positioned to see the mismatch.
/// </para>
/// <para>
/// A transport declares itself fit for this by advertising
/// <see cref="TransportCapabilities.ContainerScopedFiles"/>. The flag is opt-in on purpose: a transport that
/// says nothing is treated as host-scoped and refused, so a future transport cannot inherit the defect by
/// omission.
/// </para>
/// </remarks>
public sealed class ContainerScopedFilesNotSupportedException : Exception
{
    /// <summary>Creates a <see cref="ContainerScopedFilesNotSupportedException"/> with a default message.</summary>
    public ContainerScopedFilesNotSupportedException()
        : base("This transport's file operations reach the host filesystem, not the container's, so a "
            + "container-rooted session cannot be served through it.")
    {
    }

    /// <summary>Creates a <see cref="ContainerScopedFilesNotSupportedException"/> with the given message.</summary>
    public ContainerScopedFilesNotSupportedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ContainerScopedFilesNotSupportedException"/> with the given message and inner exception.</summary>
    public ContainerScopedFilesNotSupportedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="ContainerScopedFilesNotSupportedException"/> naming what was refused.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="transportId">The transport that cannot serve container-scoped file operations.</param>
    /// <param name="containerRef">The container the refused session was rooted at, if known.</param>
    /// <param name="containerRootPath">The in-container root path that would have been misrouted, if known.</param>
    public ContainerScopedFilesNotSupportedException(
        string message,
        string? transportId,
        string? containerRef = null,
        string? containerRootPath = null)
        : base(message)
    {
        TransportId = transportId;
        ContainerRef = containerRef;
        ContainerRootPath = containerRootPath;
    }

    /// <summary>The transport that was refused, if known.</summary>
    public string? TransportId { get; }

    /// <summary>The container the refused session was rooted at, if known.</summary>
    public string? ContainerRef { get; }

    /// <summary>The in-container root path that would have been misrouted onto the host, if known.</summary>
    public string? ContainerRootPath { get; }
}
