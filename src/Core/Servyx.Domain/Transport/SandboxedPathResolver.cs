namespace Servyx.Domain.Transport;

/// <summary>
/// Resolves caller-supplied relative or absolute path strings against a fixed sandbox root, producing a
/// <see cref="TargetPath"/> only when the result is guaranteed to stay within that root. This is the
/// sanctioned factory for <see cref="TargetPath"/>: because <see cref="TargetPath"/>'s constructor is
/// internal, every path entering the rest of the system has passed through this validation, so traversal
/// is rejected at the type level rather than being re-checked at each call site.
/// </summary>
/// <remarks>
/// This resolver performs <b>lexical</b> containment checking only: it normalizes the input string and
/// compares it, as text, against the sandbox root. It has no filesystem access and therefore cannot
/// detect symlinks, junctions, hardlinks, bind mounts, or 8.3 short names (e.g. <c>PROGRA~1</c>) that
/// might cause the same string to refer to a location outside the sandbox once the target's filesystem
/// resolves it. <b>Infrastructure implementations that turn a <see cref="TargetPath"/> into real I/O MUST
/// canonicalize the fully resolved path — <c>GetFinalPathNameByHandle</c> on Windows, <c>realpath</c> on
/// Unix — and re-verify containment against the sandbox root before performing the I/O.</b> This type
/// only guarantees that the string form cannot lexically escape; it cannot see through the filesystem.
/// </remarks>
public sealed class SandboxedPathResolver
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _root;

    /// <summary>Creates a resolver scoped to the given sandbox root.</summary>
    /// <param name="sandboxRoot">
    /// The absolute or relative directory that all resolved paths must stay within. It is normalized
    /// (via <see cref="Path.GetFullPath(string)"/>) at construction time.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="sandboxRoot"/> is null, empty, or whitespace.</exception>
    public SandboxedPathResolver(string sandboxRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxRoot);
        _root = NormalizeRoot(sandboxRoot);
    }

    /// <summary>
    /// Resolves <paramref name="relativeOrAbsolute"/> against the sandbox root, returning a
    /// <see cref="TargetPath"/> whose <see cref="TargetPath.Value"/> is root-relative and
    /// forward-slash-separated.
    /// </summary>
    /// <exception cref="PathEscapesSandboxException">
    /// The input is empty-but-whitespace, contains a null byte, is a UNC or device path, contains an
    /// illegal colon (a possible NTFS alternate data stream), contains a reserved Windows device name
    /// segment (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM0</c>-<c>COM9</c>,
    /// <c>LPT0</c>-<c>LPT9</c>), or normalizes to a location outside the sandbox root.
    /// </exception>
    public TargetPath Resolve(string relativeOrAbsolute)
    {
        ArgumentNullException.ThrowIfNull(relativeOrAbsolute);

        if (relativeOrAbsolute.Length > 0 && string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            throw new PathEscapesSandboxException("Path is whitespace-only, which is not a valid path.", relativeOrAbsolute);
        }

        if (relativeOrAbsolute.Contains('\0'))
        {
            throw new PathEscapesSandboxException("Path contains a null byte, which is never permitted.", relativeOrAbsolute);
        }

        if (IsUncOrDevicePath(relativeOrAbsolute))
        {
            throw new PathEscapesSandboxException(
                $"Path '{relativeOrAbsolute}' is a UNC or device path, which is never permitted inside a sandbox.",
                relativeOrAbsolute);
        }

        var combined = Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(_root, relativeOrAbsolute);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PathEscapesSandboxException($"Path '{relativeOrAbsolute}' could not be normalized.", relativeOrAbsolute);
        }

        if (HasIllegalColon(fullPath))
        {
            throw new PathEscapesSandboxException(
                $"Path '{relativeOrAbsolute}' contains an illegal colon (possible NTFS alternate data stream).",
                relativeOrAbsolute);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var withinRoot = fullPath.Equals(_root, comparison)
            || fullPath.StartsWith(_root + Path.DirectorySeparatorChar, comparison);

        if (!withinRoot)
        {
            throw new PathEscapesSandboxException(
                $"Path '{relativeOrAbsolute}' escapes the sandbox root '{_root}'.",
                relativeOrAbsolute);
        }

        var relative = fullPath.Equals(_root, comparison)
            ? string.Empty
            : fullPath[(_root.Length + 1)..];

        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsReservedDeviceSegment(segment))
            {
                throw new PathEscapesSandboxException(
                    $"Path '{relativeOrAbsolute}' contains the reserved device name '{segment}'.",
                    relativeOrAbsolute);
            }
        }

        return TargetPathFactory.Create(normalized);
    }

    private static bool IsUncOrDevicePath(string path)
    {
        // Covers both \\server\share and \\?\C:\... device paths, as well as the forward-slash variant.
        return path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects colons other than the legitimate Windows drive-letter separator (e.g. the <c>:</c> in
    /// <c>C:\data</c>, at index 1 immediately after a drive letter). Any other colon — including the
    /// classic NTFS alternate-data-stream marker (<c>file.txt:stream</c>) — is rejected. Only meaningful
    /// on Windows; colons are ordinary filename characters elsewhere.
    /// </summary>
    private static bool HasIllegalColon(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        for (var i = 0; i < fullPath.Length; i++)
        {
            if (fullPath[i] != ':')
            {
                continue;
            }

            var isDriveColon = i == 1 && char.IsAsciiLetter(fullPath[0]);
            if (!isDriveColon)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a single path segment's base name (ignoring its extension) is one of Windows' reserved
    /// device names. Comparison is case-insensitive and exact — "CONFIG" or "CONSOLE" must not match.
    /// Note: this does not attempt to detect Unicode "fullwidth"/superscript lookalikes (e.g. some
    /// "COM¹"-style variants) that Windows also treats as device names; that is a known, accepted
    /// limitation of this lexical check.
    /// </summary>
    private static bool IsReservedDeviceSegment(string segment)
    {
        var dotIndex = segment.IndexOf('.');
        var baseName = dotIndex >= 0 ? segment[..dotIndex] : segment;
        return ReservedDeviceNames.Contains(baseName);
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
