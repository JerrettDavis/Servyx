namespace Servyx.Domain.Common;

/// <summary>
/// Strongly-typed identifier for a managed game server.
/// </summary>
public readonly record struct ServerId
{
    /// <summary>
    /// Creates a <see cref="ServerId"/> wrapping the given <see cref="Guid"/>.
    /// </summary>
    public ServerId(Guid value) => Value = value;

    /// <summary>
    /// The underlying identifier value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates a new, unique <see cref="ServerId"/>.
    /// </summary>
    public static ServerId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="ServerId"/> from its canonical string form. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static ServerId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a <see cref="ServerId"/> from its canonical string form.
    /// </summary>
    public static bool TryParse(string? value, out ServerId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new ServerId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Strongly-typed identifier for a managed host (a physical or virtual machine reachable via a transport).
/// </summary>
public readonly record struct HostId
{
    /// <summary>
    /// Creates a <see cref="HostId"/> wrapping the given <see cref="Guid"/>.
    /// </summary>
    public HostId(Guid value) => Value = value;

    /// <summary>
    /// The underlying identifier value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates a new, unique <see cref="HostId"/>.
    /// </summary>
    public static HostId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="HostId"/> from its canonical string form. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static HostId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a <see cref="HostId"/> from its canonical string form.
    /// </summary>
    public static bool TryParse(string? value, out HostId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new HostId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Strongly-typed identifier for a backup artifact.
/// </summary>
public readonly record struct BackupId
{
    /// <summary>
    /// Creates a <see cref="BackupId"/> wrapping the given <see cref="Guid"/>.
    /// </summary>
    public BackupId(Guid value) => Value = value;

    /// <summary>
    /// The underlying identifier value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates a new, unique <see cref="BackupId"/>.
    /// </summary>
    public static BackupId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="BackupId"/> from its canonical string form. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static BackupId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a <see cref="BackupId"/> from its canonical string form.
    /// </summary>
    public static bool TryParse(string? value, out BackupId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new BackupId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Strongly-typed identifier for a previewed, not-yet-applied <c>ConfigChangePlan</c>.
/// </summary>
public readonly record struct ChangePlanId
{
    /// <summary>
    /// Creates a <see cref="ChangePlanId"/> wrapping the given <see cref="Guid"/>.
    /// </summary>
    public ChangePlanId(Guid value) => Value = value;

    /// <summary>
    /// The underlying identifier value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates a new, unique <see cref="ChangePlanId"/>.
    /// </summary>
    public static ChangePlanId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="ChangePlanId"/> from its canonical string form. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static ChangePlanId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a <see cref="ChangePlanId"/> from its canonical string form.
    /// </summary>
    public static bool TryParse(string? value, out ChangePlanId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new ChangePlanId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Strongly-typed identifier for a receipt recording an applied change.
/// </summary>
public readonly record struct ChangeReceiptId
{
    /// <summary>
    /// Creates a <see cref="ChangeReceiptId"/> wrapping the given <see cref="Guid"/>.
    /// </summary>
    public ChangeReceiptId(Guid value) => Value = value;

    /// <summary>
    /// The underlying identifier value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates a new, unique <see cref="ChangeReceiptId"/>.
    /// </summary>
    public static ChangeReceiptId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="ChangeReceiptId"/> from its canonical string form. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static ChangeReceiptId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a <see cref="ChangeReceiptId"/> from its canonical string form.
    /// </summary>
    public static bool TryParse(string? value, out ChangeReceiptId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new ChangeReceiptId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Strongly-typed identifier for a planned mod installation.
/// </summary>
public readonly record struct ModInstallId
{
    /// <summary>
    /// Creates a <see cref="ModInstallId"/> wrapping the given <see cref="Guid"/>.
    /// </summary>
    public ModInstallId(Guid value) => Value = value;

    /// <summary>
    /// The underlying identifier value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates a new, unique <see cref="ModInstallId"/>.
    /// </summary>
    public static ModInstallId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="ModInstallId"/> from its canonical string form. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static ModInstallId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a <see cref="ModInstallId"/> from its canonical string form.
    /// </summary>
    public static bool TryParse(string? value, out ModInstallId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new ModInstallId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
