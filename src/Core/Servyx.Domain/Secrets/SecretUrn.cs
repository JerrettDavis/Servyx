using System.Diagnostics.CodeAnalysis;

namespace Servyx.Domain.Secrets;

/// <summary>
/// A strongly-validated reference to a secret's location, in the form
/// <c>secret://{scope}/{scopeId}/{category}/{name}</c> — for example
/// <c>secret://server/palworld-01/rcon/password</c> or <c>secret://connector/ssh-prod-1/ssh/private-key</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="SecretUrn"/> never carries a secret value — it is a locator only. Descriptors and other
/// domain models hold a <see cref="SecretUrn"/>, never the resolved secret; resolution happens through
/// <see cref="ISecretStore"/> at the point of use, as late as possible.
/// </para>
/// <para>
/// Every segment (<see cref="Scope"/>, <see cref="ScopeId"/>, <see cref="Category"/>, <see cref="Name"/>)
/// is validated at construction: it must be non-empty, at most <see cref="MaxSegmentLength"/> characters,
/// composed only of ASCII letters, digits, <c>-</c>, <c>_</c>, or <c>.</c>, and must not consist entirely
/// of <c>.</c> characters (which rules out <c>.</c> and <c>..</c> traversal segments). Because <c>/</c> is
/// never an allowed character within a segment, no segment can smuggle in extra path components, and a
/// parsed or constructed <see cref="SecretUrn"/> can never resolve to more or fewer than four segments —
/// this is what makes "escape its own scope" unrepresentable rather than merely discouraged.
/// </para>
/// <para>
/// By convention <see cref="Scope"/> is one of <c>"server"</c>, <c>"connector"</c>, or <c>"global"</c>, and
/// <see cref="Category"/> is one of <c>"ssh"</c>, <c>"rcon"</c>, <c>"docker"</c>, or <c>"api"</c> — but this
/// type does not enforce a closed set for either, since new scopes and categories are expected as new
/// connector kinds are added. What it enforces unconditionally is the character-and-shape safety described
/// above.
/// </para>
/// <para>
/// <c>default(SecretUrn)</c> is always constructible because this is a struct — that is unavoidable in C#
/// and is not a bypass of validation. A default-initialized instance has every property equal to
/// <see langword="null"/> and MUST NOT be treated as a valid URN: only a value obtained from
/// <see cref="Create"/> or a successful <see cref="TryParse"/> is valid.
/// </para>
/// </remarks>
public readonly record struct SecretUrn
{
    private const string Scheme = "secret://";

    /// <summary>The maximum length, in characters, of any single segment.</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>The maximum length, in characters, of the fully composed URN string.</summary>
    public const int MaxValueLength = 512;

    /// <summary>The full, canonical URN string, e.g. <c>secret://server/palworld-01/rcon/password</c>.</summary>
    public string Value { get; }

    /// <summary>The scope segment (by convention <c>"server"</c>, <c>"connector"</c>, or <c>"global"</c>).</summary>
    public string Scope { get; }

    /// <summary>The identifier of the scoped entity (e.g. a server id or connector id).</summary>
    public string ScopeId { get; }

    /// <summary>The category segment (by convention <c>"ssh"</c>, <c>"rcon"</c>, <c>"docker"</c>, or <c>"api"</c>).</summary>
    public string Category { get; }

    /// <summary>The name of the specific secret within its category.</summary>
    public string Name { get; }

    private SecretUrn(string value, string scope, string scopeId, string category, string name)
    {
        Value = value;
        Scope = scope;
        ScopeId = scopeId;
        Category = category;
        Name = name;
    }

    /// <summary>
    /// Constructs a <see cref="SecretUrn"/> from its four segments, validating each one.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any segment is null, empty, too long, contains a character outside the conservative allowed
    /// charset (letters, digits, <c>-</c>, <c>_</c>, <c>.</c>), or consists entirely of <c>.</c> characters.
    /// </exception>
    public static SecretUrn Create(string scope, string scopeId, string category, string name)
    {
        ValidateSegment(scope, nameof(scope));
        ValidateSegment(scopeId, nameof(scopeId));
        ValidateSegment(category, nameof(category));
        ValidateSegment(name, nameof(name));

        var value = $"{Scheme}{scope}/{scopeId}/{category}/{name}";
        if (value.Length > MaxValueLength)
        {
            throw new ArgumentException(
                $"The composed secret URN exceeds the maximum allowed length of {MaxValueLength} characters.");
        }

        return new SecretUrn(value, scope, scopeId, category, name);
    }

    /// <summary>
    /// Attempts to parse <paramref name="s"/> as a <see cref="SecretUrn"/>. Returns <see langword="false"/>
    /// for anything that is not exactly <c>secret://</c> followed by four non-empty, validly-charactered
    /// segments separated by <c>/</c> — including inputs with empty segments (e.g. a doubled slash),
    /// too few or too many segments, path-traversal segments, whitespace, control characters, or a total
    /// length beyond <see cref="MaxValueLength"/>.
    /// </summary>
    public static bool TryParse(string? s, out SecretUrn urn)
    {
        urn = default;

        if (string.IsNullOrEmpty(s) || s.Length > MaxValueLength)
        {
            return false;
        }

        if (!s.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = s[Scheme.Length..];

        // Deliberately not StringSplitOptions.RemoveEmptyEntries: an empty segment (from a doubled slash,
        // or a leading/trailing slash) must be rejected, not silently collapsed away.
        var segments = remainder.Split('/');
        if (segments.Length != 4)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                return false;
            }
        }

        urn = new SecretUrn(s, segments[0], segments[1], segments[2], segments[3]);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Whether <paramref name="segment"/> is a valid <see cref="SecretUrn"/> segment on its own: non-empty,
    /// at most <see cref="MaxSegmentLength"/> characters, composed only of ASCII letters, digits, <c>-</c>,
    /// <c>_</c>, or <c>.</c>, and not consisting entirely of <c>.</c> characters. Exposed publicly so that
    /// callers accepting a bare scope/scope-id/category/name (for example <see cref="ISecretStore.ListAsync"/>)
    /// can apply the identical validation without re-deriving it.
    /// </summary>
    public static bool IsValidSegment([NotNullWhen(true)] string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > MaxSegmentLength)
        {
            return false;
        }

        var allDots = true;

        foreach (var c in segment)
        {
            if (c != '.')
            {
                allDots = false;
            }

            var isAllowed = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '-' or '_' or '.';

            if (!isAllowed)
            {
                return false;
            }
        }

        return !allDots;
    }

    private static void ValidateSegment(string? segment, string paramName)
    {
        if (!IsValidSegment(segment))
        {
            throw new ArgumentException(
                $"'{segment}' is not a valid secret URN segment. Segments must be 1-{MaxSegmentLength} " +
                "characters of ASCII letters, digits, '-', '_', or '.', must not be empty or whitespace, " +
                "must not contain '/', and must not consist solely of '.' characters.",
                paramName);
        }
    }
}
