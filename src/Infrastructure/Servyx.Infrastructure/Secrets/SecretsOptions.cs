namespace Servyx.Infrastructure.Secrets;

/// <summary>
/// Configuration for <see cref="DataProtectionSecretStore"/> and the host key store registered by
/// <see cref="ServiceCollectionExtensions.AddServyxSecrets"/>.
/// </summary>
public sealed class SecretsOptions
{
    /// <summary>
    /// The root directory under which encrypted secret files are stored, one file per
    /// <c>Servyx.Domain.Secrets.SecretUrn</c>. Defaults to <c>{AppContext.BaseDirectory}/servyx-data/secrets</c>.
    /// </summary>
    public string SecretsRootDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "servyx-data", "secrets");

    /// <summary>
    /// The directory Data Protection persists its key ring to. Defaults to a <c>.keys</c> subdirectory of
    /// <see cref="SecretsRootDirectory"/>, kept separate from ciphertext files so the key ring can be
    /// backed up or rotated independently.
    /// </summary>
    public string? KeyRingDirectory { get; set; }

    /// <summary>
    /// The Data Protection "purpose" string used to derive the protector. Changing this after secrets have
    /// been written makes them unrecoverable — treat it as fixed for the lifetime of a deployment.
    /// </summary>
    public string DataProtectionPurpose { get; set; } = "Servyx.Secrets.v1";

    /// <summary>
    /// The JSON file host key pins and revocations are persisted to. Defaults to
    /// <c>{AppContext.BaseDirectory}/servyx-data/host-keys.json</c>.
    /// </summary>
    public string HostKeyStoreFilePath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "servyx-data", "host-keys.json");

    /// <summary>Resolves <see cref="KeyRingDirectory"/>, falling back to a subdirectory of <see cref="SecretsRootDirectory"/>.</summary>
    public string ResolveKeyRingDirectory() => KeyRingDirectory ?? Path.Combine(SecretsRootDirectory, ".keys");
}
