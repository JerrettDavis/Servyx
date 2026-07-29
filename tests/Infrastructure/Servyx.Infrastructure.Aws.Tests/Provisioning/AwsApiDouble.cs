using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>One request the adapter attempted, captured before any socket could exist.</summary>
/// <param name="Method">The HTTP verb.</param>
/// <param name="Uri">The absolute request URI, including query string, exactly as it would have been sent.</param>
/// <param name="Authorization">The raw <c>Authorization</c> header, or <see langword="null"/> if none was set.</param>
/// <param name="AmzDate">The raw <c>x-amz-date</c> header, or <see langword="null"/> if none was set.</param>
/// <param name="Body">The request body as text, or <see langword="null"/> for a bodiless request.</param>
/// <param name="Target">
/// The raw <c>X-Amz-Target</c> header, or <see langword="null"/> if none was set. EC2 requests never carry
/// one - it is the routing mechanism the JSON-protocol Lightsail client uses in place of EC2's <c>Action</c>
/// form/query parameter, so this is <see langword="null"/> for every request the EC2 suite makes and non-null
/// for every request the Lightsail suite makes.
/// </param>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string? AmzDate,
    string? Body,
    string? Target = null)
{
    /// <summary>Whether this request went to a regional EC2 endpoint.</summary>
    internal bool IsEc2 => Uri.Host.StartsWith("ec2.", StringComparison.Ordinal)
        && Uri.Host.EndsWith(".amazonaws.com", StringComparison.Ordinal);

    /// <summary>Whether this request went to a regional Lightsail endpoint.</summary>
    internal bool IsLightsail => Uri.Host.StartsWith("lightsail.", StringComparison.Ordinal)
        && Uri.Host.EndsWith(".amazonaws.com", StringComparison.Ordinal);

    /// <summary>The Lightsail action this request names, read off the <see cref="Target"/> header.</summary>
    internal string? LightsailAction => Target is null ? null : Target[(Target.IndexOf('.') + 1)..];

    /// <summary>The EC2 Query <c>Action</c> this request names, wherever the parameters happen to live.</summary>
    /// <remarks>
    /// Reads the query string for a GET and the form body for a POST, which is the split the client makes and
    /// therefore the split a routing function has to understand.
    /// </remarks>
    internal string? Action => ParameterOf("Action");

    /// <summary>The signed-headers list the <c>Authorization</c> header claims, or <see langword="null"/>.</summary>
    internal string? SignedHeaders => Field("SignedHeaders=");

    /// <summary>The hex signature the <c>Authorization</c> header carries, or <see langword="null"/>.</summary>
    internal string? Signature => Field("Signature=");

    /// <summary>The credential field the <c>Authorization</c> header carries, or <see langword="null"/>.</summary>
    internal string? Credential => Field("Credential=");

    /// <summary>One EC2 Query parameter's value, from the query string or the form body.</summary>
    internal string? ParameterOf(string name)
    {
        var source = Method == HttpMethod.Get ? Uri.Query.TrimStart('?') : Body;
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        foreach (var part in source.Split('&'))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(Uri.UnescapeDataString(part[..separator]), name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part[(separator + 1)..]);
            }
        }

        return null;
    }

    private string? Field(string label)
    {
        if (Authorization is null)
        {
            return null;
        }

        var start = Authorization.IndexOf(label, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var value = Authorization[(start + label.Length)..];
        var end = value.IndexOf(',', StringComparison.Ordinal);
        return (end < 0 ? value : value[..end]).Trim();
    }
}

/// <summary>
/// A substituted AWS: an <see cref="HttpMessageHandler"/> that records every request and answers from a
/// caller-supplied routing function.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps the whole suite offline. Nothing here opens a socket, resolves a hostname, or
/// reads an environment variable, so the tests run identically on a bare CI runner with no internet, with no
/// AWS account, and with no IAM credential — and a test that expects <em>no</em> request can prove it by
/// asserting <see cref="Requests"/> is empty, which is a stronger claim than "the call failed".
/// </para>
/// <para>
/// It is the direct counterpart of <c>DigitalOceanApiDouble</c> and <c>AzureArmApiDouble</c>. The one addition
/// is itself the finding of this adapter: <see cref="RecordedRequest.Signature"/> and
/// <see cref="RecordedRequest.SignedHeaders"/> exist because AWS's credential does not travel, so there is no
/// <c>Authorization: Bearer &lt;secret&gt;</c> to assert against — what a test can check is that a signature
/// was computed, over which headers, and that nothing derived from the key pair leaked anywhere else.
/// </para>
/// </remarks>
internal sealed class AwsApiDouble : HttpMessageHandler
{
    /// <summary>Every request the adapter made, in order.</summary>
    internal List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// How each request is answered. Defaults to failing the test loudly: a route a test did not set up is a
    /// call the adapter was not supposed to make.
    /// </summary>
    internal Func<RecordedRequest, HttpResponseMessage> Responder { get; set; } =
        request => throw new InvalidOperationException(
            $"The adapter made an unexpected {request.Method} request to '{request.Uri}' "
            + $"(Action='{request.Action}', Target='{request.Target}').");

    /// <summary>An <see cref="HttpClient"/> wired to this double.</summary>
    internal HttpClient Client() => new(this, disposeHandler: false);

    /// <summary>Builds an XML response, which is the only shape the EC2 Query API answers with.</summary>
    internal static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
        new(status)
        {
            Content = new StringContent(xml, Encoding.UTF8, new MediaTypeHeaderValue("text/xml")),
        };

    /// <summary>Builds an empty response.</summary>
    internal static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    /// <summary>Builds a JSON response, which is the shape every Lightsail action answers with.</summary>
    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/x-amz-json-1.1")),
        };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? new Uri("about:blank"),
            Header(request, "Authorization"),
            Header(request, AwsSigV4.AmzDateHeader),
            body,
            Header(request, "X-Amz-Target"));

        Requests.Add(recorded);
        return Responder(recorded);
    }

    /// <summary>
    /// Reads a header without going through <see cref="HttpRequestHeaders.Authorization"/>'s parser.
    /// </summary>
    /// <remarks>
    /// The signer adds its headers with <c>TryAddWithoutValidation</c>, so reading them raw is reading exactly
    /// what would go on the wire — which is the whole point of a test double that exists to check a signature.
    /// </remarks>
    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
}

/// <summary>
/// The smallest honest <see cref="ISecretStore"/>: an in-memory map that hands out a real
/// <see cref="SecretLease"/> and records every URN resolution in order.
/// </summary>
/// <remarks>
/// Recording resolutions is not incidental — it is how the suite proves the plan path resolves nothing at all,
/// and how it proves the signer resolves the key pair afresh for every request rather than caching it. That
/// second assertion is stronger here than for either sibling adapter: DigitalOcean re-resolves a token it then
/// transmits, Azure caches a derived access token, and this adapter re-resolves a key it never transmits.
/// </remarks>
internal sealed class RecordingSecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    /// <summary>Every URN resolution that happened, in order.</summary>
    internal List<string> Resolved { get; } = [];

    internal void Put(SecretUrn urn, string value) => _values[urn.Value] = Encoding.UTF8.GetBytes(value);

    public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default) =>
        Task.FromResult(_values.ContainsKey(urn.Value));

    public Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
    {
        Resolved.Add(urn.Value);

        return Task.FromResult(_values.TryGetValue(urn.Value, out var value)
            ? new SecretLease((byte[])value.Clone())
            : null);
    }

    public Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
    {
        _values[urn.Value] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
    {
        _values.Remove(urn.Value);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default)
    {
        IReadOnlyList<SecretUrn> urns = _values.Keys
            .Select(k => SecretUrn.TryParse(k, out var urn) ? urn : default)
            .Where(u => string.Equals(u.Scope, scope, StringComparison.Ordinal)
                && string.Equals(u.ScopeId, scopeId, StringComparison.Ordinal))
            .ToList();

        return Task.FromResult(urns);
    }
}
