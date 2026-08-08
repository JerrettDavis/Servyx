using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Secrets;

namespace Servyx.Composition;

/// <summary>
/// One imported (or skipped) secret, reported so a caller — or a test — can assert precisely on what
/// <see cref="SecretImport.RunAsync"/> did without re-deriving it from log output.
/// </summary>
/// <param name="Imported">URNs newly written to the store, in configuration order.</param>
/// <param name="Skipped">URNs that already existed and were therefore left untouched.</param>
public sealed record SecretImportReport(IReadOnlyList<SecretUrn> Imported, IReadOnlyList<SecretUrn> Skipped)
{
    /// <summary>An empty report — no <c>Servyx:Secrets:Import</c> section was configured.</summary>
    public static readonly SecretImportReport Empty = new([], []);
}

/// <summary>
/// A minimal, config-driven, startup-only write path into <see cref="ISecretStore"/> for the credentials an
/// operator has to get into the store somehow before a connector can use them — an SSH private key, for
/// example — since nothing else in this codebase writes to <see cref="ISecretStore"/> except through a
/// feature-specific store like <c>OperatorCredentialStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>Servyx:Secrets:Import</c>: each child key is a <see cref="SecretUrn"/> string, each value is an
/// absolute path to a file whose exact bytes become the secret's value. Nothing is trimmed, decoded, or
/// otherwise transformed — a private key is whitespace- and newline-sensitive, so the file's bytes are
/// copied to the store byte-for-byte.
/// </para>
/// <para>
/// <strong>Never overwrites.</strong> An existing secret at the target URN is left untouched and reported as
/// skipped, which is what makes this idempotent across restarts — the second and every later run of the same
/// process sees the same configured import and does nothing.
/// </para>
/// <para>
/// <strong>Fails loudly, not quietly.</strong> A configured source file that does not exist or cannot be
/// read, a malformed URN key, or an empty source file all throw — deliberately failing this process's startup
/// rather than letting it come up with a connector missing the credential it needs, which would otherwise
/// surface later as a confusing authentication failure with no obvious cause.
/// </para>
/// </remarks>
public static class SecretImport
{
    /// <summary>The configuration section this import reads: <c>Servyx:Secrets:Import</c>.</summary>
    public const string SectionKey = "Servyx:Secrets:Import";

    /// <summary>The actor recorded against every import write, since a secret write is an audit event.</summary>
    private const string Actor = "servyx.web/startup-import";

    /// <summary>
    /// Imports every configured secret that does not already exist in <paramref name="store"/>.
    /// </summary>
    /// <param name="config">The application configuration.</param>
    /// <param name="store">The secret store to write into.</param>
    /// <param name="logger">
    /// Where progress is logged. Only URNs and source paths are ever logged — never a secret's bytes or
    /// text.
    /// </param>
    /// <param name="ct">Cancels the import between individual entries.</param>
    /// <returns>
    /// <see cref="SecretImportReport.Empty"/> when <c>Servyx:Secrets:Import</c> is absent; otherwise a report
    /// naming every URN imported and every URN skipped.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A configured source path does not exist or cannot be read, or names an empty file.
    /// </exception>
    /// <exception cref="ArgumentException">A configured key is not a well-formed <see cref="SecretUrn"/>.</exception>
    public static async Task<SecretImportReport> RunAsync(
        IConfiguration config, ISecretStore store, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        var section = config.GetSection(SectionKey);
        if (!section.Exists())
        {
            return SecretImportReport.Empty;
        }

        var imported = new List<SecretUrn>();
        var skipped = new List<SecretUrn>();

        // Not section.GetChildren(): a secret:// URN contains ':' itself (the scheme separator), and .NET
        // configuration's own path delimiter is also ':' — GetChildren() would split "secret://host/..."
        // into a "secret" child with a "//host/..." grandchild instead of one key. AsEnumerable(relative)
        // walks the whole subtree and hands back full relative keys as configured, colons intact, with only
        // genuine leaves (an actual configured value) carrying a non-null Value — which is exactly what lets
        // this skip the intermediate nodes that same split produces without mistaking them for entries.
        foreach (var entry in section.AsEnumerable(makePathsRelative: true))
        {
            if (entry.Value is null)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            var key = entry.Key;
            var path = entry.Value;

            if (!SecretUrn.TryParse(key, out var urn))
            {
                throw new ArgumentException(
                    $"'{SectionKey}' names '{key}' as a secret to import, but that is not a valid secret URN.",
                    nameof(config));
            }

            if (await store.ExistsAsync(urn, ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Secret import: {Urn} already exists in the secret store; skipping (source {Path}).",
                    urn, path);
                skipped.Add(urn);
                continue;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Secret import for '{urn}' names source path '{path}', which does not exist or is not "
                    + "a file. Startup is refusing to continue rather than start without this credential.");
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Secret import for '{urn}' could not read source path '{path}': {ex.Message}", ex);
            }

            if (bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Secret import for '{urn}' names source path '{path}', which is empty. An empty file is "
                    + "always an error for a secret value.");
            }

            await store.SetAsync(urn, bytes, Actor, ct).ConfigureAwait(false);

            logger.LogInformation("Secret import: wrote {Urn} from {Path}.", urn, path);
            logger.LogWarning(
                "Secret import: {Urn} was imported from plaintext file {Path}. Delete that file now — the "
                + "secret store holds the only copy this process needs.",
                urn, path);

            imported.Add(urn);
        }

        return new SecretImportReport(imported, skipped);
    }
}
