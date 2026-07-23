using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Secrets;

/// <summary>
/// <see cref="ISecretStore"/> backed by ASP.NET Core Data Protection with a file-backed key ring. Each
/// <see cref="SecretUrn"/> maps to exactly one file under <see cref="SecretsOptions.SecretsRootDirectory"/>,
/// containing a small JSON envelope whose only secret-derived field is base64 ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// Filenames are derived from the URN's already-validated segments (<see cref="SecretUrn"/> guarantees each
/// segment is free of <c>/</c>, path-traversal tokens, whitespace, and control characters), but this type
/// does not stop at trusting that guarantee: every path is additionally routed through
/// <see cref="SandboxedPathResolver"/> — the same lexical-containment guard used elsewhere in this codebase
/// for target paths — and re-validated segment-by-segment via <see cref="SecretUrn.IsValidSegment"/> before
/// any I/O happens. This defends against a <c>default(SecretUrn)</c> (whose properties are all null, since
/// <see cref="SecretUrn"/> is a struct) or any other value that did not actually pass through
/// <see cref="SecretUrn.Create"/>/<see cref="SecretUrn.TryParse"/> reaching the filesystem layer.
/// </para>
/// <para>
/// Adequate for a self-hosted single-box deployment; swapping in a real KMS-backed <see cref="ISecretStore"/>
/// later requires no change above this interface.
/// </para>
/// </remarks>
public sealed class DataProtectionSecretStore : ISecretStore, IDisposable
{
    private const string FileExtension = ".secret";
    private const string TempFileSuffix = ".tmp";

    private readonly string _root;
    private readonly SandboxedPathResolver _pathResolver;
    private readonly ServiceProvider _dataProtectionServices;
    private readonly IDataProtector _protector;

    /// <summary>Creates a <see cref="DataProtectionSecretStore"/> configured by <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public DataProtectionSecretStore(SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _root = Path.GetFullPath(options.SecretsRootDirectory);
        Directory.CreateDirectory(_root);
        _pathResolver = new SandboxedPathResolver(_root);

        var keyRingDirectory = Path.GetFullPath(options.ResolveKeyRingDirectory());
        Directory.CreateDirectory(keyRingDirectory);

        // A standalone (non-hosted) Data Protection provider, scoped to its own tiny DI container, backed
        // by a file-system key ring rooted at keyRingDirectory. This is the supported pattern for using
        // Data Protection from a plain class library that is not itself an ASP.NET Core host. The container
        // is kept alive for the lifetime of this store (disposed alongside it) rather than disposed
        // immediately after resolving the protector, because internal Data Protection components may
        // re-resolve services from it lazily on later Protect/Unprotect calls.
        var services = new ServiceCollection();
        services
            .AddDataProtection()
            .SetApplicationName("Servyx")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingDirectory));

        _dataProtectionServices = services.BuildServiceProvider();
        var provider = _dataProtectionServices.GetRequiredService<IDataProtectionProvider>();
        _protector = provider.CreateProtector(options.DataProtectionPurpose);
    }

    /// <summary>Disposes the internal Data Protection service container.</summary>
    public void Dispose() => _dataProtectionServices.Dispose();

    /// <inheritdoc />
    public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(ResolveFilePath(urn)));
    }

    /// <inheritdoc />
    public async Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
    {
        var path = ResolveFilePath(urn);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<SecretEnvelope>(json)
            ?? throw new InvalidOperationException($"Secret file for '{urn}' could not be parsed.");

        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        var plaintext = _protector.Unprotect(ciphertext);
        return new SecretLease(plaintext);
    }

    /// <inheritdoc />
    public async Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var path = ResolveFilePath(urn);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not determine a directory for secret path '{path}'.");
        Directory.CreateDirectory(directory);

        var ciphertext = _protector.Protect(value.ToArray());
        var envelope = new SecretEnvelope(actor, DateTimeOffset.UtcNow, Convert.ToBase64String(ciphertext));
        var json = JsonSerializer.Serialize(envelope);

        var tempPath = path + TempFileSuffix + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Atomic rename: readers of `path` see either the old complete file or the new complete file,
        // never a partially written one.
        File.Move(tempPath, path, overwrite: true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var path = ResolveFilePath(urn);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default)
    {
        if (!SecretUrn.IsValidSegment(scope))
        {
            throw new ArgumentException("'scope' is not a valid secret URN segment.", nameof(scope));
        }

        if (!SecretUrn.IsValidSegment(scopeId))
        {
            throw new ArgumentException("'scopeId' is not a valid secret URN segment.", nameof(scopeId));
        }

        var scopeDirectory = ResolveScopeDirectory(scope, scopeId);
        var results = new List<SecretUrn>();

        if (!Directory.Exists(scopeDirectory))
        {
            return Task.FromResult<IReadOnlyList<SecretUrn>>(results);
        }

        foreach (var categoryDirectory in Directory.EnumerateDirectories(scopeDirectory))
        {
            var category = Path.GetFileName(categoryDirectory);
            if (!SecretUrn.IsValidSegment(category))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(categoryDirectory, "*" + FileExtension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!SecretUrn.IsValidSegment(name))
                {
                    continue;
                }

                results.Add(SecretUrn.Create(scope, scopeId, category, name));
            }
        }

        return Task.FromResult<IReadOnlyList<SecretUrn>>(results);
    }

    /// <summary>
    /// Resolves the on-disk path for <paramref name="urn"/>: <c>{root}/{scope}/{scopeId}/{category}/{name}.secret</c>,
    /// re-validated and lexically contained via <see cref="SandboxedPathResolver"/>.
    /// </summary>
    private string ResolveFilePath(SecretUrn urn)
    {
        if (!SecretUrn.IsValidSegment(urn.Scope)
            || !SecretUrn.IsValidSegment(urn.ScopeId)
            || !SecretUrn.IsValidSegment(urn.Category)
            || !SecretUrn.IsValidSegment(urn.Name))
        {
            throw new ArgumentException(
                "The secret URN is not fully validated (e.g. default(SecretUrn)) and cannot be resolved to storage.",
                nameof(urn));
        }

        var relative = $"{urn.Scope}/{urn.ScopeId}/{urn.Category}/{urn.Name}{FileExtension}";
        return ResolveContainedPath(relative);
    }

    private string ResolveScopeDirectory(string scope, string scopeId)
    {
        var relative = $"{scope}/{scopeId}";
        return ResolveContainedPath(relative);
    }

    private string ResolveContainedPath(string relative)
    {
        var targetPath = _pathResolver.Resolve(relative);
        return Path.GetFullPath(Path.Combine(_root, targetPath.Value.Replace('/', Path.DirectorySeparatorChar)));
    }

    private sealed record SecretEnvelope(string Actor, DateTimeOffset ModifiedAtUtc, string CiphertextBase64);
}
