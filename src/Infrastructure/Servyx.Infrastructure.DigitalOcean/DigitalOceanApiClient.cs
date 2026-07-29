using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.DigitalOcean;

/// <summary>
/// The only code in this assembly that talks to <c>api.digitalocean.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-rolled on purpose.</strong> DigitalOcean publishes no first-party .NET SDK, and the community
/// clients are unmaintained. Rather than take an abandoned dependency, this type uses
/// <c>System.Net.Http</c> + <c>System.Net.Http.Json</c> + <c>System.Text.Json</c>, all of which ship in the
/// shared framework. The surface it needs is four calls wide, which is well under the cost of a dependency.
/// </para>
/// <para>
/// <strong>The token is never a field.</strong> <see cref="SendAsync"/> resolves the API token from
/// <see cref="ISecretStore"/> on every single request, holds the <see cref="SecretLease"/> only long enough to
/// stamp one <c>Authorization</c> header, and disposes it — zeroing the buffer — <em>before</em> the request is
/// sent. There is deliberately no cached token, no <c>string _token</c>, and no
/// <c>DefaultRequestHeaders.Authorization</c> on the shared <see cref="HttpClient"/>: a token parked on the
/// client would outlive every call and would be visible to anything else sharing that client.
/// </para>
/// <para>
/// <strong>Nothing here logs.</strong> This assembly references no logging package at all (see the .csproj),
/// so there is no reachable code path that could write the token — or a request containing it — anywhere. The
/// exception messages below are built from the status code and the API's own error body, never from the
/// request headers.
/// </para>
/// </remarks>
internal sealed class DigitalOceanApiClient
{
    /// <summary>The public API root, used when the supplied <see cref="HttpClient"/> has no base address.</summary>
    internal const string DefaultBaseAddress = "https://api.digitalocean.com/";

    /// <summary>The page size requested when sweeping. DigitalOcean caps <c>per_page</c> at 200.</summary>
    private const int SweepPageSize = 200;

    /// <summary>
    /// A hard ceiling on pages followed during one sweep, so a provider paging bug cannot turn a sweep into an
    /// unbounded loop. 200 pages × 200 droplets is far beyond any realistic Servyx account.
    /// </summary>
    private const int MaxSweepPages = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;
    private readonly ISecretStore _secretStore;
    private readonly SecretUrn _apiTokenUrn;

    internal DigitalOceanApiClient(HttpClient http, ISecretStore secretStore, SecretUrn apiTokenUrn)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secretStore);

        if (string.IsNullOrEmpty(apiTokenUrn.Value))
        {
            throw new ArgumentException(
                "A DigitalOcean API token URN is required. Build one with SecretUrn.Create, e.g. "
                + "SecretUrn.Create(\"global\", \"digitalocean\", \"api\", \"token\"); a default(SecretUrn) is not a valid URN.",
                nameof(apiTokenUrn));
        }

        http.BaseAddress ??= new Uri(DefaultBaseAddress, UriKind.Absolute);

        _http = http;
        _secretStore = secretStore;
        _apiTokenUrn = apiTokenUrn;
    }

    /// <summary>Creates a droplet. The single billable call in this assembly.</summary>
    internal async Task<DropletResource> CreateDropletAsync(CreateDropletRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/droplets")
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create a droplet", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Droplet
            ?? throw new DigitalOceanApiException(
                response.StatusCode,
                "DigitalOcean accepted the droplet creation request but returned no droplet object, so Servyx has no "
                + "id to record or to compensate with. Treat this as a possible orphan and reconcile by tag.");
    }

    /// <summary>Reads a droplet by id, or <see langword="null"/> if the provider no longer has it.</summary>
    internal async Task<DropletResource?> GetDropletAsync(long dropletId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"v2/droplets/{dropletId}"));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "read a droplet", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Droplet;
    }

    /// <summary>Destroys a droplet. Returns <see langword="false"/> if it was already gone.</summary>
    internal async Task<bool> DeleteDropletAsync(long dropletId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            string.Create(CultureInfo.InvariantCulture, $"v2/droplets/{dropletId}"));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "destroy a droplet", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Lists every droplet carrying <paramref name="tagName"/>, following DigitalOcean's pagination to the end.
    /// </summary>
    /// <remarks>
    /// Pagination is followed rather than truncated because this is the orphan sweep's only view of the
    /// provider: stopping at the first page would report "no orphans beyond page one" as "no orphans", which is
    /// the precise failure this capability exists to prevent.
    /// </remarks>
    internal async Task<IReadOnlyList<DropletResource>> ListDropletsByTagAsync(string tagName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);

        var droplets = new List<DropletResource>();
        var next = string.Create(
            CultureInfo.InvariantCulture,
            $"v2/droplets?per_page={SweepPageSize}&tag_name={Uri.EscapeDataString(tagName)}");

        for (var page = 0; page < MaxSweepPages && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "list droplets by tag", ct).ConfigureAwait(false);

            var envelope = await ReadAsync<DropletListEnvelope>(response, ct).ConfigureAwait(false);
            droplets.AddRange(envelope?.Droplets ?? []);

            next = string.IsNullOrWhiteSpace(envelope?.Links?.Pages?.Next) ? null : envelope.Links.Pages.Next;
        }

        return droplets;
    }

    /// <summary>
    /// Stamps a freshly-resolved bearer token onto <paramref name="request"/> and sends it.
    /// </summary>
    /// <remarks>
    /// The lease is opened, converted to a header value, and disposed inside the <c>using</c> below — the send
    /// happens after the buffer has already been zeroed. <see cref="SecretLease.ToUtf8String"/> is the one
    /// unavoidable materialisation (HTTP headers are text), taken as late as possible exactly as that type's
    /// remarks require.
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (var lease = await _secretStore.GetAsync(_apiTokenUrn, ct).ConfigureAwait(false))
        {
            if (lease is null)
            {
                throw new InvalidOperationException(
                    $"No DigitalOcean API token is stored at '{_apiTokenUrn}'. Store the account's personal access "
                    + "token there before provisioning; the token is never read from configuration or the environment.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ToUtf8String());
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class =>
        await response.Content.ReadFromJsonAsync<T>(SerializerOptions, ct).ConfigureAwait(false);

    /// <summary>
    /// Turns a non-success response into a <see cref="DigitalOceanApiException"/> carrying the status and the
    /// provider's own error text — and nothing from the request, so a token can never reach a message.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string attempted, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail;
        try
        {
            detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            detail = string.Empty;
        }

        throw new DigitalOceanApiException(
            response.StatusCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"DigitalOcean refused the attempt to {attempted}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}").Trim());
    }
}

/// <summary>
/// A DigitalOcean API call that did not succeed.
/// </summary>
/// <remarks>
/// Carries the status code so a caller can distinguish a rate limit (429) or an authorisation failure (401)
/// from a genuine provider error, and never carries any part of the request — in particular not its
/// <c>Authorization</c> header.
/// </remarks>
public sealed class DigitalOceanApiException : Exception
{
    /// <summary>Creates an exception for a failed DigitalOcean API call.</summary>
    public DigitalOceanApiException(HttpStatusCode statusCode, string message)
        : base(message) => StatusCode = statusCode;

    /// <summary>Creates an exception for a failed DigitalOcean API call.</summary>
    public DigitalOceanApiException(HttpStatusCode statusCode, string message, Exception innerException)
        : base(message, innerException) => StatusCode = statusCode;

    /// <summary>Creates an exception with no status context.</summary>
    public DigitalOceanApiException()
        : this(HttpStatusCode.InternalServerError, "A DigitalOcean API call failed.")
    {
    }

    /// <summary>Creates an exception with no status context.</summary>
    public DigitalOceanApiException(string message)
        : this(HttpStatusCode.InternalServerError, message)
    {
    }

    /// <summary>Creates an exception with no status context.</summary>
    public DigitalOceanApiException(string message, Exception innerException)
        : this(HttpStatusCode.InternalServerError, message, innerException)
    {
    }

    /// <summary>The HTTP status DigitalOcean returned.</summary>
    public HttpStatusCode StatusCode { get; }
}
