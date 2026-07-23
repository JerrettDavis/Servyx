namespace Servyx.Domain.Transport;

/// <summary>
/// A path scoped to a target's server root. The constructor is internal so that path traversal is
/// rejected at construction time, at the type level, rather than being re-validated ad hoc at every call
/// site that accepts a path. Outside this assembly, the only way to obtain one is to already hold one —
/// there is no public constructor and no implicit conversion from <see cref="string"/>. Within this
/// assembly, <see cref="SandboxedPathResolver"/> is the sanctioned factory.
/// </summary>
/// <remarks>
/// <para>
/// <c>default(TargetPath)</c> is always constructible because this is a struct — that is unavoidable in
/// C# and is not a bypass of the sandbox. A default-initialized instance has <see cref="Value"/> equal to
/// <see langword="null"/> and MUST NOT be treated as a validated, resolved path: callers should only ever
/// use a <see cref="TargetPath"/> obtained from <see cref="SandboxedPathResolver.Resolve(string)"/>.
/// </para>
/// <para>
/// Equality on this type is the compiler-generated ordinal string comparison over <see cref="Value"/>,
/// which is case-sensitive even on Windows, where <see cref="SandboxedPathResolver"/> itself resolves
/// containment case-insensitively. Two <see cref="TargetPath"/> values that differ only in case may
/// therefore refer to the same on-disk file on Windows without comparing equal. Do not use this type as a
/// dictionary key (or in a <see cref="HashSet{T}"/>) when on-disk identity, rather than exact string
/// identity, is what matters.
/// </para>
/// </remarks>
public readonly record struct TargetPath
{
    /// <summary>The normalized, root-relative path value, using <c>/</c> as the segment separator.</summary>
    public string Value { get; }

    /// <summary>
    /// Constructs a <see cref="TargetPath"/> from an already-validated, root-relative value. Internal:
    /// only code within <c>Servyx.Domain</c> (in practice, <see cref="SandboxedPathResolver"/>) may call this.
    /// </summary>
    internal TargetPath(string value) => Value = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Internal factory allowing <see cref="SandboxedPathResolver"/> to construct <see cref="TargetPath"/>
/// instances after validating them, without exposing a public constructor on the type itself.
/// </summary>
internal static class TargetPathFactory
{
    /// <summary>Creates a <see cref="TargetPath"/> from an already-validated, root-relative value.</summary>
    internal static TargetPath Create(string value) => new(value);
}
