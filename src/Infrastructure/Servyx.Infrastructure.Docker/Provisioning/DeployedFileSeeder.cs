using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// One file to place into a workload's storage before that workload is started for the first time — the
/// runtime half of a game definition's <c>deployments[].files[]</c> entry.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Content"/> is bytes, never a <see cref="string"/>.</strong> A seeded file's content
/// is, in the case this feature exists for, a credential. Materializing it as a managed string would make
/// it unerasable (see the remarks on <see cref="Domain.Secrets.SecretLease"/>) and, worse, would make it
/// trivially interpolatable into a log line or an exception message. Holding bytes means the only way to
/// render this object as text is <see cref="ToString"/>, which is overridden below to mask.
/// </para>
/// <para>
/// <strong>Content is resolved before it gets here.</strong> Turning a definition's
/// <c>contentFrom: secret:...</c> into actual bytes needs a secret store and the instance scope to address
/// it with, both of which live above the infrastructure layer. This type is the already-resolved result, so
/// nothing in this assembly needs to know what a <c>SecretRef</c> is.
/// </para>
/// </remarks>
public sealed class SeededFile
{
    /// <summary>
    /// The fixed placeholder that stands in for a sensitive value in any human-readable rendering. The same
    /// eight asterisks the settings read-model uses for a secret-typed setting's value, so a mask in a
    /// provisioning diagnostic looks like a mask everywhere else in the product rather than like a value.
    /// </summary>
    public const string Mask = "********";

    /// <summary>Creates a description of a file to seed.</summary>
    /// <param name="path">Where the file lands, relative to the target's root.</param>
    /// <param name="content">The exact bytes to write.</param>
    /// <param name="mode">The POSIX permission bits as an octal string, e.g. <c>0600</c>.</param>
    /// <param name="createOnly">Whether an already-present file at <paramref name="path"/> is left untouched.</param>
    /// <param name="isSensitive">
    /// Whether <paramref name="content"/> came from the secret store. Only ever affects how this object
    /// renders itself as text — the bytes are handled identically either way.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="mode"/> is not a POSIX permission mode written in octal (e.g. <c>0600</c>, <c>640</c>).
    /// Rejected here rather than at write time so a definition that declares a nonsense mode fails while the
    /// container is still an idea, not after it has been created.
    /// </exception>
    public SeededFile(TargetPath path, ReadOnlyMemory<byte> content, string mode = "0600", bool createOnly = true, bool isSensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        Path = path;
        Content = content;
        Mode = mode;
        PosixMode = ParseOctalMode(mode);
        CreateOnly = createOnly;
        IsSensitive = isSensitive;
    }

    /// <summary>Where the file lands, relative to the target's root.</summary>
    public TargetPath Path { get; }

    /// <summary>The exact bytes to write.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>The POSIX permission bits to create the file with, as an octal string.</summary>
    public string Mode { get; }

    /// <summary>
    /// <see cref="Mode"/> decoded into the permission bits <see cref="FileWriteOptions.Mode"/> takes, so the
    /// declared mode travels inside the write itself rather than as a follow-up command.
    /// </summary>
    public int PosixMode { get; }

    /// <summary>Whether an already-present file at <see cref="Path"/> is left untouched.</summary>
    public bool CreateOnly { get; }

    /// <summary>Whether <see cref="Content"/> came from the secret store.</summary>
    public bool IsSensitive { get; }

    /// <summary>
    /// A rendering safe to put in a log line, a diagnostic, or an exception message. Content is never
    /// included: a sensitive file renders as <see cref="Mask"/> and a non-sensitive one renders only as a
    /// byte count, because "non-sensitive" is a caller's claim and a leak that depends on that claim being
    /// right is not a control.
    /// </summary>
    /// <remarks>
    /// This is deliberately <see cref="ToString"/> rather than a separately-named helper. A named helper
    /// only protects the call sites that remember to use it, whereas overriding <see cref="ToString"/> means
    /// string interpolation — the way a value actually ends up in a log by accident — masks by default.
    /// </remarks>
    public override string ToString() =>
        $"'{Path.Value}' (mode {Mode}, createOnly {(CreateOnly ? "true" : "false")}, "
        + $"{Content.Length} bytes, content {(IsSensitive ? Mask : "not sensitive")})";

    /// <summary>
    /// Decodes an octal permission string into its bits, refusing anything that is not exactly that.
    /// </summary>
    /// <remarks>
    /// Set-user-id, set-group-id and the sticky bit are rejected rather than silently dropped: a definition
    /// asking for <c>4755</c> wants something this seam cannot deliver, and quietly writing <c>0755</c>
    /// instead would be a security decision made by a parser.
    /// </remarks>
    private static int ParseOctalMode(string mode)
    {
        foreach (var c in mode)
        {
            if (c is < '0' or > '7')
            {
                throw new ArgumentException(
                    $"'{mode}' is not a POSIX permission mode: it must be octal digits only, e.g. 0600.", nameof(mode));
            }
        }

        if (mode.Length > 4)
        {
            throw new ArgumentException(
                $"'{mode}' is not a POSIX permission mode: at most four octal digits, e.g. 0600.", nameof(mode));
        }

        var parsed = Convert.ToInt32(mode, 8);
        if (parsed > 0x1FF)
        {
            throw new ArgumentException(
                $"'{mode}' sets a bit outside the low nine permission bits. Set-user-id, set-group-id and the "
                + "sticky bit are deliberately not expressible on a seeded file.", nameof(mode));
        }

        return parsed;
    }
}

/// <summary>What <see cref="DeployedFileSeeder"/> did about one <see cref="SeededFile"/>.</summary>
public enum SeededFileAction
{
    /// <summary>The bytes were written through the target's write path.</summary>
    Written,

    /// <summary>The file already existed and <see cref="SeededFile.CreateOnly"/> was set, so it was left alone.</summary>
    SkippedBecauseItAlreadyExists,
}

/// <summary>The outcome of seeding one file.</summary>
/// <param name="Path">The file's path, relative to the target's root.</param>
/// <param name="Action">What was done about it.</param>
public sealed record SeededFileOutcome(TargetPath Path, SeededFileAction Action);

/// <summary>
/// Materializes a deployment's declared <see cref="SeededFile"/>s into a target's storage.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything goes through <see cref="IExecutionTarget.WriteFileAsync"/>, and nothing goes around
/// it.</strong> This type holds no Docker client, no archive API, and no private path to the daemon — it
/// takes an <see cref="IExecutionTarget"/> and calls the same write member every other write in the product
/// calls. That is what puts seeding behind <see cref="WriteGuardedExecutionTarget"/>: a session obtained
/// from a Servyx-registered transport is always the guarded decorator, whose
/// <see cref="WriteGuardedExecutionTarget.WriteFileAsync"/> throws
/// <see cref="WritesDisabledException"/> before any I/O unless the server's <see cref="WriteMode"/> is
/// <see cref="WriteMode.Enabled"/>. Seeding a file into a read-only server is therefore refused
/// structurally, not by a check this type performs and a future edit could forget.
/// </para>
/// <para>
/// <strong><see cref="SeededFile.CreateOnly"/> is check-then-write, and that is honest about what it
/// buys.</strong> <see cref="IExecutionTarget"/> exposes no create-exclusive write, so the check and the
/// write are two calls and a file appearing between them would be overwritten. That race is acceptable here
/// and only here: seeding runs against a container that has been created but never started, so the only
/// writer that could be racing is another Servyx provisioning run against the same brand-new container —
/// which the ledger already prevents. It would not be acceptable against a live workload, and this type
/// must not be repurposed for one without an atomic primitive underneath it.
/// </para>
/// <para>
/// <strong>Failures name the file, never its content.</strong> Nothing here formats
/// <see cref="SeededFile.Content"/>, and <see cref="SeededFile.ToString"/> masks, so an exception escaping
/// this type — including one thrown by the target itself — carries a path and a mode and no bytes.
/// </para>
/// <para>
/// <strong>The write is declared <see cref="FileWriteStrategy.DirectPlacement"/>, and the mode rides along
/// with it.</strong> Seeding runs against a container that has been created and not started, which is
/// exactly the state in which no process exists to perform the rename an
/// <see cref="FileWriteStrategy.AtomicRename"/> write finalizes with, or the <c>chmod</c> a separate mode
/// step would need. Both are therefore folded into the single archive placement the transport already had
/// to make: the file appears at its declared path with its declared permissions, or it does not appear.
/// Nothing here inspects whether the container is running and nothing degrades from one strategy to the
/// other — the strategy is stated because this caller knows what it is writing into, not discovered.
/// </para>
/// </remarks>
public static class DeployedFileSeeder
{
    /// <summary>
    /// Writes each of <paramref name="files"/> to <paramref name="target"/>, in order, skipping any
    /// <see cref="SeededFile.CreateOnly"/> file that already exists.
    /// </summary>
    /// <param name="target">
    /// The session to write through. Expected to be a <see cref="WriteGuardedExecutionTarget"/>, since that
    /// is the only kind a Servyx-registered transport hands out; nothing here depends on it being one, which
    /// is precisely why the guard is the transport's job rather than this type's.
    /// </param>
    /// <param name="files">The files to seed. An empty list is a no-op that touches the target not at all.</param>
    /// <param name="rootPath">
    /// The target's in-container root. Every <see cref="SeededFile.Path"/> is already resolved relative to
    /// it, so this is not used to build paths; it is validated and named in failure messages, so an operator
    /// reading one can tell which target's root the seed was aimed at. Defaults to <c>/</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One outcome per entry of <paramref name="files"/>, in the same order.</returns>
    /// <exception cref="WritesDisabledException">
    /// The target refused the write because the server's write mode is not <see cref="WriteMode.Enabled"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The target cannot honour a <see cref="FileWriteStrategy.DirectPlacement"/> write carrying an explicit
    /// mode. Loud rather than silent on purpose: a transport that ignored either would hand back a receipt
    /// for a file that is not the file that was asked for.
    /// </exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static async Task<IReadOnlyList<SeededFileOutcome>> SeedAsync(
        IExecutionTarget target,
        IReadOnlyList<SeededFile> files,
        string rootPath = "/",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var outcomes = new List<SeededFileOutcome>(files.Count);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file, nameof(files));

            if (file.CreateOnly && await target.ExistsAsync(file.Path, ct).ConfigureAwait(false))
            {
                outcomes.Add(new SeededFileOutcome(file.Path, SeededFileAction.SkippedBecauseItAlreadyExists));
                continue;
            }

            using var content = new MemoryStream(file.Content.ToArray(), writable: false);

            // No ExpectedPreImageHash: this is a first-write, and the only "expectation" that matters —
            // that an existing file is not clobbered — is the CreateOnly check above. Passing a hash of
            // nothing would turn a legitimate deliberate overwrite (createOnly: false) into a drift error.
            var options = new FileWriteOptions(null)
            {
                Strategy = FileWriteStrategy.DirectPlacement,
                Mode = file.PosixMode,
            };

            try
            {
                await target.WriteFileAsync(file.Path, content, options, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // Interpolating `file` uses SeededFile.ToString, which masks — the content never reaches
                // this message even though this is the path taken when something has already gone wrong.
                throw new IOException($"Seeded file {file} could not be written under '{rootPath}'.", ex);
            }

            outcomes.Add(new SeededFileOutcome(file.Path, SeededFileAction.Written));
        }

        return outcomes;
    }
}
