using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Servyx.Infrastructure.Persistence.Converters;

/// <summary>
/// Value converters and matching value comparers for the small collection-valued properties Servyx stores
/// inline on a row rather than in a child table.
/// </summary>
/// <remarks>
/// <para>
/// <strong>JSON, not a delimiter.</strong> Every collection here holds provider- or user-supplied strings —
/// secret-store URNs, provider tag keys and values — none of which Servyx controls the character set of. Any
/// delimiter scheme would therefore need escaping to stay lossless, and a delimiter-based encoding that
/// silently corrupts a credential URN containing the delimiter is precisely the kind of failure that shows up
/// as an unresolvable secret long after the write. JSON is self-describing, already escapes everything, and
/// reads the same on SQLite and PostgreSQL, so it is used for all of them.
/// </para>
/// <para>
/// <strong>Every converter here is paired with a comparer, and that pairing is load-bearing.</strong> A
/// converted collection property is a reference type as far as EF Core is concerned, so without an explicit
/// <see cref="ValueComparer{T}"/> EF falls back to reference equality and takes no snapshot. Mutating the
/// collection in place then leaves the tracked instance reference-identical to the "original" value, EF
/// detects no change, and <c>SaveChanges</c> silently writes nothing — no exception, no warning, just a lost
/// update. The comparers below supply structural equality plus a deep-copy snapshot so in-place mutation is
/// detected. See <c>CredentialUrnsValueComparerTests</c> for the regression test that pins this down.
/// </para>
/// </remarks>
public static class JsonCollectionConverters
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    /// <summary>Converts an ordered string collection to and from a JSON array.</summary>
    public static readonly ValueConverter<IReadOnlyList<string>, string> StringList = new(
        list => JsonSerializer.Serialize(list, SerializerOptions),
        json => JsonSerializer.Deserialize<List<string>>(json, SerializerOptions) ?? new List<string>());

    /// <summary>
    /// Structural comparer for <see cref="StringList"/>. Order-sensitive, because the order of a credential
    /// URN list is the resolution order and changing it is a real change.
    /// </summary>
    public static readonly ValueComparer<IReadOnlyList<string>> StringListComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
        list => list.ToList());

    /// <summary>Converts a string-to-string map to and from a JSON object.</summary>
    public static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> StringDictionary = new(
        map => JsonSerializer.Serialize(map, SerializerOptions),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions) ?? new Dictionary<string, string>());

    /// <summary>
    /// Structural comparer for <see cref="StringDictionary"/>. Order-insensitive — provider tag maps have no
    /// meaningful ordering, so the hash is combined with XOR rather than an order-dependent fold.
    /// </summary>
    public static readonly ValueComparer<IReadOnlyDictionary<string, string>> StringDictionaryComparer = new(
        (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
        map => map.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        map => map.ToDictionary(pair => pair.Key, pair => pair.Value));
}
