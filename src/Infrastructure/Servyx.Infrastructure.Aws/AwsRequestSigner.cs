using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The identity Servyx signs AWS requests as: the <see cref="SecretUrn"/>s its access key id, secret access
/// key, and optional STS session token are stored at.
/// </summary>
/// <remarks>
/// <para>
/// Only the <em>URNs</em> are carried, never the values. That is the whole reason this is a type rather than
/// three loose constructor parameters — it is a value that is safe to hold, compare, or put in a descriptor,
/// holding the addresses of three values that are none of those things. It is the direct counterpart of
/// <c>AzureServicePrincipal</c>.
/// </para>
/// <para>
/// <strong>Why the access key id is a stored secret here when Azure's client id is a plain string.</strong>
/// An AWS access key id is, strictly, an identifier: it appears in CloudTrail and in IAM's console. But unlike
/// an Azure client id it is issued <em>as one half of a key pair</em> and is rotated with its secret, so
/// splitting the pair across two stores (one in configuration, one in the secret store) would mean a rotation
/// could leave the two halves mismatched with nothing to notice. Keeping both in the secret store makes a
/// rotation one atomic operator action. The consequence is one extra <see cref="ISecretStore"/> resolution per
/// request, which is stated rather than hidden.
/// </para>
/// <para>
/// <strong>The session token is optional and is a real capability, not a placeholder.</strong> When Servyx is
/// given temporary STS credentials (an assumed role) rather than a long-lived IAM user key, AWS requires the
/// session token to travel as a signed <c>x-amz-security-token</c> header. Omitting support for it would mean
/// this adapter could only be used with long-lived keys, which is the credential shape AWS itself advises
/// against.
/// </para>
/// </remarks>
public sealed class AwsSigningIdentity
{
    /// <summary>Creates a signing identity from the URNs its three components are stored at.</summary>
    /// <param name="accessKeyIdUrn">Where the access key id is stored, e.g. <c>secret://global/aws/api/access-key-id</c>.</param>
    /// <param name="secretAccessKeyUrn">Where the secret access key is stored. Never the key itself.</param>
    /// <param name="sessionTokenUrn">
    /// Where an STS session token is stored, when temporary credentials are used, or <see langword="null"/> for
    /// a long-lived IAM user key.
    /// </param>
    /// <exception cref="ArgumentException">Either required URN is a <c>default(SecretUrn)</c>.</exception>
    public AwsSigningIdentity(
        SecretUrn accessKeyIdUrn,
        SecretUrn secretAccessKeyUrn,
        SecretUrn? sessionTokenUrn = null)
    {
        RequireRealUrn(accessKeyIdUrn, nameof(accessKeyIdUrn), "access key id", "access-key-id");
        RequireRealUrn(secretAccessKeyUrn, nameof(secretAccessKeyUrn), "secret access key", "secret-access-key");

        if (sessionTokenUrn is { } sessionToken)
        {
            RequireRealUrn(sessionToken, nameof(sessionTokenUrn), "session token", "session-token");
        }

        AccessKeyIdUrn = accessKeyIdUrn;
        SecretAccessKeyUrn = secretAccessKeyUrn;
        SessionTokenUrn = sessionTokenUrn;
    }

    /// <summary>Where the access key id is stored. Only the address is held here.</summary>
    public SecretUrn AccessKeyIdUrn { get; }

    /// <summary>Where the secret access key is stored. Only the address is held here.</summary>
    public SecretUrn SecretAccessKeyUrn { get; }

    /// <summary>Where an STS session token is stored, if temporary credentials are in use.</summary>
    public SecretUrn? SessionTokenUrn { get; }

    private static void RequireRealUrn(SecretUrn urn, string paramName, string what, string suggestedName)
    {
        if (string.IsNullOrEmpty(urn.Value))
        {
            throw new ArgumentException(
                $"An AWS {what} URN is required. Build one with SecretUrn.Create, e.g. "
                + $"SecretUrn.Create(\"global\", \"aws\", \"api\", \"{suggestedName}\"); a default(SecretUrn) is "
                + "not a valid URN.",
                paramName);
        }
    }
}

/// <summary>
/// Stamps an AWS Signature Version 4 <c>Authorization</c> header onto an <see cref="HttpRequestMessage"/>,
/// resolving the key pair from <see cref="ISecretStore"/> at the moment of use.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The secret access key is never a field.</strong> <see cref="SignAsync"/> resolves it on every single
/// request, holds the <see cref="SecretLease"/> only for the duration of the HMAC chain, and disposes it —
/// zeroing the buffer — before the request is handed back to be sent. There is deliberately no cached
/// credential, no <c>string _secretAccessKey</c>, and nothing parked on the shared <see cref="HttpClient"/>.
/// This matches the DigitalOcean adapter's discipline exactly, and is <em>stronger</em> than the Azure
/// adapter's, which caches a derived access token: SigV4 needs no exchange, so there is nothing to cache and
/// revoking a stored key takes effect on the very next request.
/// </para>
/// <para>
/// Note also what never travels at all: unlike both existing adapters, the stored secret is not transmitted in
/// any form. What goes on the wire is a 64-character hex HMAC over a canonical rendering of the request. An
/// intercepted Servyx AWS request does not disclose the credential that signed it.
/// </para>
/// <para>
/// <strong>What is signed, and why it is an allow-list rather than "everything".</strong> The signed set is
/// <c>host</c>, <c>x-amz-date</c>, <c>content-type</c> when there is a body, and every <c>x-amz-*</c> header
/// present. Signing every header on the message would be closer to what some SDKs do and is tempting, but
/// <see cref="HttpClient"/> adds headers <em>after</em> a handler sees the request (<c>Content-Length</c>,
/// transfer encoding, a default user agent), and a signed header whose value changes in flight is an
/// unexplainable 403. An allow-list is deterministic, and AWS only ever verifies the headers the signature
/// claims.
/// </para>
/// <para>
/// <strong>Signed and sent are the same bytes, by construction.</strong> Before computing anything, the signer
/// rewrites the request's query string to its canonical form. That closes the one real trap in
/// <see cref="AwsSigV4.CanonicalQuery"/> — a query whose raw text and canonical text differ (lower-case
/// escapes, unsorted parameters, a raw <c>+</c>) would otherwise be signed in one form and sent in another. A
/// test asserts the property directly on every request the suite makes.
/// </para>
/// </remarks>
public sealed class AwsRequestSigner
{
    private readonly ISecretStore _secretStore;
    private readonly AwsSigningIdentity _identity;
    private readonly string _region;
    private readonly string _service;
    private readonly TimeProvider _timeProvider;
    private readonly bool _includeContentSha256Header;

    /// <summary>Creates a signer for one region and one service.</summary>
    /// <param name="secretStore">Where the key pair is resolved from, freshly, on every request.</param>
    /// <param name="identity">The URNs of the key pair. Only URNs are held.</param>
    /// <param name="region">The AWS region the credential scope names, e.g. <c>us-east-1</c>.</param>
    /// <param name="service">The AWS service the credential scope names, e.g. <c>ec2</c>.</param>
    /// <param name="timeProvider">Clock used for the <c>x-amz-date</c> header and the credential scope.</param>
    /// <param name="includeContentSha256Header">
    /// Whether to send (and therefore sign) <c>x-amz-content-sha256</c>. Required by Amazon S3, not by the EC2
    /// Query API, so this defaults to <see langword="false"/> and the EC2 client leaves it off.
    /// </param>
    public AwsRequestSigner(
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        string service,
        TimeProvider? timeProvider = null,
        bool includeContentSha256Header = false)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        _secretStore = secretStore;
        _identity = identity;
        _region = region;
        _service = service;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _includeContentSha256Header = includeContentSha256Header;
    }

    /// <summary>The AWS region this signer scopes signatures to.</summary>
    public string Region => _region;

    /// <summary>The AWS service this signer scopes signatures to.</summary>
    public string Service => _service;

    /// <summary>
    /// Signs <paramref name="request"/> in place: canonicalises its query, stamps <c>x-amz-date</c> (and
    /// <c>x-amz-security-token</c> when temporary credentials are configured), and adds the
    /// <c>Authorization</c> header.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A required secret is not stored at its URN. Raised before any signing work happens, and it names the URN
    /// rather than anything that was read from it.
    /// </exception>
    public async Task SignAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            throw new InvalidOperationException(
                "An AWS request must carry an absolute request URI before it can be signed: the host is part of "
                + "the canonical request, so a relative URI has nothing to sign against.");
        }

        // Read the body once, before any secret is resolved. The payload hash is part of the canonical request,
        // and reading it here means the lease below is held for the shortest possible window.
        var payload = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        using var accessKeyIdLease = await Resolve(_identity.AccessKeyIdUrn, "access key id", ct).ConfigureAwait(false);
        using var sessionTokenLease = _identity.SessionTokenUrn is { } sessionTokenUrn
            ? await Resolve(sessionTokenUrn, "session token", ct).ConfigureAwait(false)
            : null;
        using var secretLease = await Resolve(_identity.SecretAccessKeyUrn, "secret access key", ct).ConfigureAwait(false);

        Sign(
            request,
            uri,
            accessKeyIdLease.ToUtf8String(),
            secretLease.Value,
            sessionTokenLease?.ToUtf8String(),
            payload);
    }

    private void Sign(
        HttpRequestMessage request,
        Uri uri,
        string accessKeyId,
        ReadOnlySpan<byte> secretAccessKey,
        string? sessionToken,
        ReadOnlySpan<byte> payload)
    {
        var signedAt = _timeProvider.GetUtcNow();
        var canonicalQuery = AwsSigV4.CanonicalQuery(uri.Query);

        // Signed == sent, by construction: if the caller's query was not already canonical, the request is
        // rewritten to the canonical form rather than signed in a form it will not be sent in.
        if (!string.Equals(canonicalQuery, uri.Query.TrimStart('?'), StringComparison.Ordinal))
        {
            uri = new Uri(
                uri.GetLeftPart(UriPartial.Path) + (canonicalQuery.Length == 0 ? string.Empty : "?" + canonicalQuery),
                UriKind.Absolute);
            request.RequestUri = uri;
        }

        var host = uri.IsDefaultPort
            ? uri.IdnHost
            : $"{uri.IdnHost}:{uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        request.Headers.Host = host;

        request.Headers.Remove(AwsSigV4.AmzDateHeader);
        request.Headers.TryAddWithoutValidation(AwsSigV4.AmzDateHeader, AwsSigV4.AmzDate(signedAt));

        if (sessionToken is not null)
        {
            request.Headers.Remove(AwsSigV4.SecurityTokenHeader);
            request.Headers.TryAddWithoutValidation(AwsSigV4.SecurityTokenHeader, sessionToken);
        }

        var payloadHash = AwsSigV4.Sha256Hex(payload);

        if (_includeContentSha256Header)
        {
            request.Headers.Remove(AwsSigV4.ContentSha256Header);
            request.Headers.TryAddWithoutValidation(AwsSigV4.ContentSha256Header, payloadHash);
        }

        var (canonicalHeaders, signedHeaders) = AwsSigV4.CanonicalHeaders(HeadersToSign(request, host));

        var canonicalRequest = AwsSigV4.CanonicalRequest(
            request.Method.Method,
            AwsSigV4.CanonicalUri(uri.AbsolutePath),
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var scope = AwsSigV4.CredentialScope(signedAt, _region, _service);
        var stringToSign = AwsSigV4.StringToSign(signedAt, scope, canonicalRequest);
        var signature = AwsSigV4.Signature(secretAccessKey, signedAt, _region, _service, stringToSign);

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            AwsSigV4.AuthorizationHeader(accessKeyId, scope, signedHeaders, signature));
    }

    /// <summary>
    /// The exact header set a signature covers: <c>host</c>, every <c>x-amz-*</c> header on the message, and
    /// <c>content-type</c> when there is a body. See the type remarks for why this is an allow-list.
    /// </summary>
    private static List<KeyValuePair<string, string>> HeadersToSign(HttpRequestMessage request, string host)
    {
        var headers = new List<KeyValuePair<string, string>> { new("host", host) };

        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in header.Value)
                {
                    headers.Add(new KeyValuePair<string, string>(header.Key, value));
                }
            }
        }

        if (request.Content?.Headers.ContentType is { } contentType)
        {
            headers.Add(new KeyValuePair<string, string>("content-type", contentType.ToString()));
        }

        return headers;
    }

    private async Task<SecretLease> Resolve(SecretUrn urn, string what, CancellationToken ct) =>
        await _secretStore.GetAsync(urn, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            $"No AWS {what} is stored at '{urn}'. Store the IAM credential there before provisioning; AWS "
            + "credentials are never read from configuration, from the environment, from ~/.aws, or from EC2 "
            + "instance metadata - Servyx resolves them by URN and from nowhere else.");
}
