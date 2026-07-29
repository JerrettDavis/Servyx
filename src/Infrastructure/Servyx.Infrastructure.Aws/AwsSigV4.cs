using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// AWS Signature Version 4, implemented from AWS's published specification over
/// <see cref="HMACSHA256"/> and <see cref="SHA256"/> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists at all.</strong> The other two cloud adapters authenticate with a header:
/// DigitalOcean's stored token <em>is</em> the bearer credential, and Azure's stored client secret buys one.
/// AWS does neither — the stored secret access key never travels, and is instead used to derive a per-request
/// HMAC over a canonical rendering of the request. That is a genuine algorithm rather than a string
/// substitution, and it is the reason AWS was previously deferred. Everything below is that algorithm, split
/// into the four steps AWS documents, each exposed as a pure function so it can be pinned by a test
/// individually rather than only through a final signature.
/// </para>
/// <para>
/// <strong>The four steps.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="CanonicalRequest"/> — <c>METHOD\n canonical_uri\n canonical_query\n canonical_headers\n
/// signed_headers\n hex(sha256(payload))</c>.
/// </description></item>
/// <item><description>
/// <see cref="StringToSign"/> — <c>AWS4-HMAC-SHA256\n amz_date\n scope\n hex(sha256(canonical_request))</c>,
/// where the scope is <c>date/region/service/aws4_request</c>.
/// </description></item>
/// <item><description>
/// <see cref="DeriveSigningKey"/> — <c>HMAC(HMAC(HMAC(HMAC("AWS4"+secret, date), region), service),
/// "aws4_request")</c>.
/// </description></item>
/// <item><description>
/// <see cref="AuthorizationHeader"/> — <c>AWS4-HMAC-SHA256 Credential=…, SignedHeaders=…, Signature=…</c>.
/// </description></item>
/// </list>
/// <para>
/// <strong>The details that decide whether it works, and where each one lives.</strong> A SigV4 implementation
/// either matches AWS byte-for-byte or fails with an opaque 403, so the fiddly parts are worth naming:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="UriEncode"/> leaves the RFC 3986 unreserved set <c>A-Za-z0-9-._~</c> alone and percent-encodes
/// everything else with <em>upper-case</em> hex. A space is therefore <c>%20</c> and never <c>+</c>; a literal
/// <c>+</c> is <c>%2B</c>. Getting that one character wrong is the single most common SigV4 defect.
/// </description></item>
/// <item><description>
/// <see cref="CanonicalQuery"/> sorts by the byte order of the <em>encoded</em> parameter name (and by encoded
/// value for repeats), not by the name as written. Parameters are decoded and re-encoded so that a query
/// written with lower-case escapes, or with a raw non-ASCII character, canonicalises identically.
/// </description></item>
/// <item><description>
/// <see cref="CanonicalHeaders"/> lower-cases names, trims values, collapses internal whitespace runs to a
/// single space, sorts by name and joins repeats of one name with <c>,</c>. The block ends with a newline, and
/// the canonical request then adds another — the blank line between the headers and the signed-header list is
/// part of the format, not a formatting accident.
/// </description></item>
/// <item><description>
/// An empty payload hashes to <see cref="HashedEmptyPayload"/>, the SHA-256 of zero bytes. It is written out
/// as a constant so a reader can recognise it in a canonical request without computing it.
/// </description></item>
/// </list>
/// <para>
/// <strong>What this implementation deliberately does not do.</strong> AWS's specification requires each path
/// segment to be URI-encoded <em>twice</em> for services other than Amazon S3. That rule is not implemented
/// here, because every request this assembly makes has the path <c>/</c> (the EC2 Query API posts to the
/// service root), so the rule is unreachable and an untested implementation of it would be worse than its
/// absence. <see cref="CanonicalUri"/> single-encodes and normalises, which is correct for <c>/</c> and for any
/// path made only of unreserved characters; a caller signing an S3-style key path must not assume otherwise.
/// Chunked (streaming) payload signing and query-string presigning are likewise absent: neither is needed by
/// the EC2 Query API.
/// </para>
/// <para>
/// <strong>Nothing here holds a secret.</strong> Every function that touches the secret access key takes it as
/// a <see cref="ReadOnlySpan{T}"/> of bytes borrowed from a <c>SecretLease</c> — it is never a field, never a
/// parameter typed <see cref="string"/>, and never returned. The derived signing key and the
/// <c>"AWS4"</c>-prefixed seed are both zeroed with
/// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> before this class returns, so the only thing
/// that outlives a call is the 64-character hex signature, which is not reversible to the key.
/// </para>
/// </remarks>
public static class AwsSigV4
{
    /// <summary>The only signing algorithm this file implements.</summary>
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The fixed final component of a credential scope and of the signing-key derivation.</summary>
    public const string Terminator = "aws4_request";

    /// <summary>The header carrying the request's signing timestamp. Always signed.</summary>
    public const string AmzDateHeader = "x-amz-date";

    /// <summary>The header carrying the payload hash, for services that require it (S3 does; EC2 does not).</summary>
    public const string ContentSha256Header = "x-amz-content-sha256";

    /// <summary>The header carrying an STS session token, when temporary credentials are used.</summary>
    public const string SecurityTokenHeader = "x-amz-security-token";

    /// <summary>The SHA-256 of zero bytes — the payload hash of every bodiless request.</summary>
    public const string HashedEmptyPayload = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>The format of the <c>x-amz-date</c> header value, e.g. <c>20150830T123600Z</c>.</summary>
    public const string AmzDateFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The format of the date component of a credential scope, e.g. <c>20150830</c>.</summary>
    public const string DateStampFormat = "yyyyMMdd";

    private const string Unreserved = "-._~";

    /// <summary>Renders <paramref name="instant"/> as an <c>x-amz-date</c> value, in UTC.</summary>
    public static string AmzDate(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(AmzDateFormat, CultureInfo.InvariantCulture);

    /// <summary>Renders <paramref name="instant"/> as the date component of a credential scope, in UTC.</summary>
    public static string DateStamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(DateStampFormat, CultureInfo.InvariantCulture);

    /// <summary>The lower-case hex SHA-256 of <paramref name="data"/>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The lower-case hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text ?? string.Empty));

    /// <summary>
    /// Percent-encodes <paramref name="value"/> per RFC 3986, which is what SigV4 means by "URI-encode".
    /// </summary>
    /// <remarks>
    /// Not <see cref="Uri.EscapeDataString(string)"/>: the two agree today, but that method's exact set has
    /// changed across .NET versions, and a signing algorithm cannot afford to inherit a framework escaping
    /// policy it does not control. The rules are stated here so they cannot move: the unreserved set
    /// <c>A-Za-z0-9-._~</c> is emitted verbatim, <c>/</c> is emitted verbatim when
    /// <paramref name="encodeSlash"/> is <see langword="false"/> (path segments) and as <c>%2F</c> otherwise
    /// (query values), and every other byte of the UTF-8 encoding is emitted as <c>%</c> followed by two
    /// <em>upper-case</em> hex digits. A space becomes <c>%20</c>; there is no code path here that can produce
    /// <c>+</c> for a space.
    /// </remarks>
    /// <param name="value">The text to encode.</param>
    /// <param name="encodeSlash">Whether <c>/</c> is encoded. <see langword="false"/> only for path segments.</param>
    public static string UriEncode(string? value, bool encodeSlash)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 3);

        foreach (var b in bytes)
        {
            var c = (char)b;

            if (char.IsAsciiLetterOrDigit(c) || Unreserved.Contains(c, StringComparison.Ordinal))
            {
                builder.Append(c);
            }
            else if (c == '/' && !encodeSlash)
            {
                builder.Append('/');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the canonical query string from a raw query (with or without its leading <c>?</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each parameter is decoded and then re-encoded with <see cref="UriEncode"/>, and the whole list is sorted
    /// by encoded name and then by encoded value. Decoding first is what makes the function idempotent and what
    /// makes a raw non-ASCII parameter (AWS's own <c>get-vanilla-utf8-query</c> vector) canonicalise correctly.
    /// </para>
    /// <para>
    /// <strong>The one input this cannot round-trip, named rather than hidden.</strong> A raw <c>+</c> in the
    /// input is treated as the character <c>+</c> and encoded to <c>%2B</c>, never as a space — which is the
    /// RFC 3986 reading, but means the canonical form differs from the bytes given. That would be a signature
    /// mismatch, so <see cref="AwsRequestSigner"/> closes the hole rather than documenting around it: it
    /// rewrites the request's query to the canonical form before signing, so what is signed and what is sent
    /// are the same string by construction. A test pins that.
    /// </para>
    /// </remarks>
    public static string CanonicalQuery(string? rawQuery)
    {
        var query = rawQuery?.TrimStart('?');
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var pairs = new List<(string Name, string Value)>();

        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];

            pairs.Add((
                UriEncode(Uri.UnescapeDataString(name), true),
                UriEncode(Uri.UnescapeDataString(value), true)));
        }

        return string.Join(
            '&',
            pairs
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value}"));
    }

    /// <summary>
    /// Builds the canonical URI from an absolute path: normalised, with each segment URI-encoded.
    /// </summary>
    /// <remarks>
    /// An empty path is <c>/</c>, which is the only path this assembly ever signs. See the type remarks for the
    /// double-encoding rule that is deliberately not implemented here.
    /// </remarks>
    public static string CanonicalUri(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || string.Equals(absolutePath, "/", StringComparison.Ordinal))
        {
            return "/";
        }

        var segments = new List<string>();

        foreach (var segment in absolutePath.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(UriEncode(Uri.UnescapeDataString(segment), true));
        }

        if (segments.Count == 0)
        {
            return "/";
        }

        var trailing = absolutePath.EndsWith('/') ? "/" : string.Empty;
        return "/" + string.Join('/', segments) + trailing;
    }

    /// <summary>
    /// Builds the canonical header block and the matching signed-header list.
    /// </summary>
    /// <remarks>
    /// The returned <c>CanonicalHeaders</c> already ends with a newline. That is not a convenience — the
    /// canonical request format places a blank line between the header block and the signed-header list, and it
    /// only appears if the block's own trailing newline is preserved.
    /// </remarks>
    /// <param name="headers">Header name/value pairs. A name may repeat; its values are joined with <c>,</c>.</param>
    public static (string CanonicalHeaders, string SignedHeaders) CanonicalHeaders(
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var byName = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var header in headers)
        {
            var name = header.Key.Trim().ToLowerInvariant();
            if (name.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var values))
            {
                values = [];
                byName[name] = values;
            }

            values.Add(CollapseWhitespace(header.Value));
        }

        var block = new StringBuilder();
        foreach (var pair in byName)
        {
            block.Append(pair.Key).Append(':').Append(string.Join(',', pair.Value)).Append('\n');
        }

        return (block.ToString(), string.Join(';', byName.Keys));
    }

    /// <summary>Assembles the canonical request from its six already-canonical components.</summary>
    public static string CanonicalRequest(
        string method,
        string canonicalUri,
        string canonicalQuery,
        string canonicalHeaders,
        string signedHeaders,
        string payloadHashHex) =>
        $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHashHex}";

    /// <summary>Builds the credential scope: <c>date/region/service/aws4_request</c>.</summary>
    public static string CredentialScope(DateTimeOffset signedAt, string region, string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        return $"{DateStamp(signedAt)}/{region}/{service}/{Terminator}";
    }

    /// <summary>Builds the string to sign from the canonical request.</summary>
    public static string StringToSign(DateTimeOffset signedAt, string credentialScope, string canonicalRequest) =>
        $"{Algorithm}\n{AmzDate(signedAt)}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

    /// <summary>
    /// Derives the request signing key: <c>HMAC(HMAC(HMAC(HMAC("AWS4"+secret, date), region), service),
    /// "aws4_request")</c>.
    /// </summary>
    /// <remarks>
    /// The <c>"AWS4"</c>-prefixed seed is the only place the raw secret is copied, and it is zeroed before this
    /// method returns. The three intermediate keys are zeroed too: each of them is as good as the secret for
    /// signing anything in its own (narrower) scope, so leaving one on the heap would be a smaller version of
    /// leaving the key on the heap. The caller owns the returned array and should zero it when finished —
    /// <see cref="Signature"/> does.
    /// </remarks>
    public static byte[] DeriveSigningKey(
        ReadOnlySpan<byte> secretAccessKey,
        string dateStamp,
        string region,
        string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dateStamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var seed = new byte[4 + secretAccessKey.Length];
        byte[]? kDate = null;
        byte[]? kRegion = null;
        byte[]? kService = null;

        try
        {
            "AWS4"u8.CopyTo(seed);
            secretAccessKey.CopyTo(seed.AsSpan(4));

            kDate = HmacSha256(seed, dateStamp);
            kRegion = HmacSha256(kDate, region);
            kService = HmacSha256(kRegion, service);

            return HmacSha256(kService, Terminator);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            ZeroIfNotNull(kDate);
            ZeroIfNotNull(kRegion);
            ZeroIfNotNull(kService);
        }
    }

    /// <summary>Computes the lower-case hex signature for <paramref name="stringToSign"/>.</summary>
    /// <remarks>The derived signing key is zeroed before this method returns.</remarks>
    public static string Signature(
        ReadOnlySpan<byte> secretAccessKey,
        DateTimeOffset signedAt,
        string region,
        string service,
        string stringToSign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stringToSign);

        var signingKey = DeriveSigningKey(secretAccessKey, DateStamp(signedAt), region, service);

        try
        {
            return Convert.ToHexStringLower(HmacSha256(signingKey, stringToSign));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <summary>Assembles the <c>Authorization</c> header value.</summary>
    /// <remarks>
    /// The spacing is part of the format AWS parses: exactly one space after the algorithm, and
    /// <c>", "</c> between the three comma-separated fields.
    /// </remarks>
    public static string AuthorizationHeader(
        string accessKeyId,
        string credentialScope,
        string signedHeaders,
        string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);

        return $"{Algorithm} Credential={accessKeyId}/{credentialScope}, "
            + $"SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] HmacSha256(ReadOnlySpan<byte> key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static void ZeroIfNotNull(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>Trims a header value and collapses every internal whitespace run to one space.</summary>
    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
