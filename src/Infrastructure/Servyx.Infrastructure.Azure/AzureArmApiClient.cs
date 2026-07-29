using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Azure;

/// <summary>
/// The only code in this assembly that talks to <c>login.microsoftonline.com</c> or
/// <c>management.azure.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-rolled on purpose, and the argument is different from DigitalOcean's.</strong> DigitalOcean
/// has no first-party .NET SDK, so hand-rolling was the only maintained option. Azure <em>does</em>, and it
/// is declined anyway — see the .csproj for the full argument. The short version: the SDK is a large
/// dependency closure, and <c>Azure.Identity</c>'s ambient credential-discovery chain directly contradicts
/// Servyx's rule that credentials come from <see cref="ISecretStore"/> by URN and from nowhere else.
/// </para>
/// <para>
/// <strong>The client secret is never a field.</strong> <see cref="ExchangeTokenAsync"/> resolves it from
/// <see cref="ISecretStore"/> on every exchange, holds the <see cref="SecretLease"/> only long enough to
/// build one form body, and disposes it — zeroing the buffer — <em>before</em> the request is sent. There is
/// deliberately no <c>string _clientSecret</c> anywhere, and no
/// <c>DefaultRequestHeaders.Authorization</c> on the shared <see cref="HttpClient"/>.
/// </para>
/// <para>
/// <strong>Where this genuinely diverges from the DigitalOcean adapter's secret discipline, stated
/// plainly.</strong> DigitalOcean holds nothing between calls: it re-resolves the personal access token from
/// the store for every single request, so revoking the stored secret takes effect on the very next call.
/// Azure cannot work that way — the credential the API accepts is not the stored secret but a short-lived
/// access token obtained by exchanging it, and re-exchanging per request would mean a second HTTP round trip
/// (and a second billed token-service call) before every ARM verb. So this client <em>does</em> cache one
/// derived value in memory: the access token, for the lifetime Entra ID itself states in
/// <c>expires_in</c>, minus a safety margin. That is a real, deliberate weakening of the
/// "nothing is cached" property, and its consequence is precise: revoking the client secret in the store
/// stops the <em>next exchange</em>, not the next request, so an already-issued access token keeps working
/// until it expires. The stored secret itself is still never cached.
/// </para>
/// <para>
/// <strong>Nothing here logs.</strong> This assembly references no logging package at all (see the .csproj),
/// so there is no reachable code path that could write the client secret, an access token, or a request
/// containing either. The exception messages below are built from the status code and the service's own
/// error body, never from the request headers or the token form body.
/// </para>
/// </remarks>
internal sealed partial class AzureArmApiClient
{
    /// <summary>The public ARM root, used when no override is supplied.</summary>
    internal const string DefaultArmBaseAddress = "https://management.azure.com/";

    /// <summary>The public Entra ID token-service root, used when no override is supplied.</summary>
    internal const string DefaultLoginBaseAddress = "https://login.microsoftonline.com/";

    /// <summary>The scope a client-credentials token must be requested for in order to call ARM.</summary>
    internal const string ArmScope = "https://management.azure.com/.default";

    /// <summary>The api-version used for subscription-level and resource-group calls.</summary>
    internal const string ResourcesApiVersion = "2021-04-01";

    /// <summary>The api-version used for every <c>Microsoft.Network</c> call.</summary>
    internal const string NetworkApiVersion = "2024-05-01";

    /// <summary>The api-version used for every <c>Microsoft.Compute</c> call.</summary>
    internal const string ComputeApiVersion = "2024-07-01";

    /// <summary>The api-version used for every <c>Microsoft.ContainerInstance</c> call.</summary>
    /// <remarks>
    /// A third entry in the mapping below rather than a second API client. ARM versions each resource
    /// provider independently, so a client that talks to a new provider must know that provider's version —
    /// there is no default that works, and sending <see cref="ResourcesApiVersion"/> to
    /// <c>Microsoft.ContainerInstance</c> is rejected rather than silently tolerated. This constant and the
    /// one line in <see cref="ApiVersionFor"/> that reads it are the <em>only</em> change the container
    /// adapter required in this file: the token exchange, the token cache, the provisioning-state wait, the
    /// delete-until-404 poll, the tag sweep and the error handling are all reused exactly as written.
    /// </remarks>
    internal const string ContainerInstanceApiVersion = "2023-05-01";

    /// <summary>
    /// How long before its stated expiry an access token is treated as already expired.
    /// </summary>
    /// <remarks>
    /// A token that expires mid-flight fails the ARM call that carried it, and in a multi-resource create
    /// sequence that failure lands between two PUTs — which is exactly the state that leaves orphans. Sixty
    /// seconds is cheap insurance against clock skew and against a long create sequence straddling the
    /// boundary.
    /// </remarks>
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    /// <summary>A hard ceiling on pages followed during one sweep, so a service paging bug cannot loop forever.</summary>
    private const int MaxSweepPages = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;
    private readonly ISecretStore _secretStore;
    private readonly SecretUrn _clientSecretUrn;
    private readonly Uri _armBaseAddress;
    private readonly Uri _loginBaseAddress;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _subscriptionId;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    // The one cached value in this type, and the documented divergence from the DigitalOcean adapter: a
    // derived, short-lived bearer token - never the client secret it was exchanged for.
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    internal AzureArmApiClient(
        HttpClient http,
        ISecretStore secretStore,
        AzureServicePrincipal servicePrincipal,
        string subscriptionId,
        TimeProvider timeProvider,
        TimeSpan pollInterval,
        int pollAttempts,
        Uri? armBaseAddress = null,
        Uri? loginBaseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(servicePrincipal);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(pollAttempts, 1);

        _http = http;
        _secretStore = secretStore;
        _clientSecretUrn = servicePrincipal.ClientSecretUrn;
        _tenantId = servicePrincipal.TenantId;
        _clientId = servicePrincipal.ClientId;
        _subscriptionId = subscriptionId;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval;
        _pollAttempts = pollAttempts;
        _armBaseAddress = armBaseAddress ?? new Uri(DefaultArmBaseAddress, UriKind.Absolute);
        _loginBaseAddress = loginBaseAddress ?? new Uri(DefaultLoginBaseAddress, UriKind.Absolute);
    }

    /// <summary>The subscription every resource id this client builds is rooted at.</summary>
    internal string SubscriptionId => _subscriptionId;

    /// <summary>Builds the ARM resource id of a resource group.</summary>
    internal string ResourceGroupId(string resourceGroup) =>
        string.Create(CultureInfo.InvariantCulture, $"/subscriptions/{_subscriptionId}/resourceGroups/{resourceGroup}");

    /// <summary>Builds the ARM resource id of a resource inside a resource group.</summary>
    internal string ResourceId(string resourceGroup, string provider, string type, string name) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"/subscriptions/{_subscriptionId}/resourceGroups/{resourceGroup}/providers/{provider}/{type}/{name}");

    /// <summary>
    /// The api-version appropriate to <paramref name="resourceId"/>'s provider.
    /// </summary>
    /// <remarks>
    /// ARM has no single api-version: each resource provider versions independently, so a client that talks
    /// to more than one provider has to carry a mapping. This is one of the small, real ways a second cloud
    /// adapter is not a copy of the first — a DigitalOcean droplet has one API version for the whole account.
    /// </remarks>
    internal static string ApiVersionFor(string resourceId) =>
        resourceId.Contains("/Microsoft.Compute/", StringComparison.OrdinalIgnoreCase) ? ComputeApiVersion
        : resourceId.Contains("/Microsoft.Network/", StringComparison.OrdinalIgnoreCase) ? NetworkApiVersion
        : resourceId.Contains("/Microsoft.ContainerInstance/", StringComparison.OrdinalIgnoreCase) ? ContainerInstanceApiVersion
        : ResourcesApiVersion;

    /// <summary>
    /// Writes a resource and waits until ARM reports it fully provisioned.
    /// </summary>
    /// <remarks>
    /// The wait is not optional politeness. An ARM PUT answers <c>201 Created</c> with
    /// <c>provisioningState: "Updating"</c> — the write is accepted, not finished — and the very next call in
    /// this adapter's create sequence (a NIC referencing a subnet, a VM referencing a NIC) will be rejected
    /// if it runs against a half-written dependency. A droplet create has no equivalent: one call, one
    /// resource, done.
    /// </remarks>
    /// <returns>The resource as ARM reports it, and whether the PUT created it (<c>201</c>) or updated an existing one (<c>200</c>).</returns>
    internal async Task<(T Resource, bool Created)> PutResourceAsync<T>(
        string resourceId,
        object body,
        CancellationToken ct)
        where T : class
    {
        var apiVersion = ApiVersionFor(resourceId);

        using var request = new HttpRequestMessage(HttpMethod.Put, Absolute(resourceId, apiVersion))
        {
            Content = JsonContent.Create(body, body.GetType(), options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"create or update '{resourceId}'", ct).ConfigureAwait(false);

        var created = response.StatusCode == HttpStatusCode.Created;
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var resource = Deserialize<T>(payload)
            ?? throw new AzureApiException(
                response.StatusCode,
                $"Azure accepted the write to '{resourceId}' but returned no resource object, so Servyx has no "
                + "record of what it created. Treat this as a possible orphan and reconcile by tag.");

        if (!IsSucceeded(Deserialize<ArmProvisioningProbe>(payload)?.Properties?.ProvisioningState))
        {
            resource = await WaitForProvisioningAsync<T>(resourceId, apiVersion, ct).ConfigureAwait(false);
        }

        return (resource, created);
    }

    /// <summary>Reads a resource by ARM id, or <see langword="null"/> if the service no longer has it.</summary>
    internal async Task<T?> GetResourceAsync<T>(string resourceId, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Absolute(resourceId, ApiVersionFor(resourceId)));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"read '{resourceId}'", ct).ConfigureAwait(false);

        return Deserialize<T>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Destroys a resource by ARM id and waits until it is actually gone.
    /// </summary>
    /// <remarks>
    /// ARM answers most deletes with <c>202 Accepted</c> and finishes the work asynchronously. Returning at
    /// that point would break this adapter's teardown outright, because ARM enforces dependency order: a NIC
    /// cannot be deleted while a VM references it, and a public IP cannot be deleted while a NIC references
    /// it. So an accepted delete is polled with GETs until the resource 404s. This is the whole reason
    /// destroying an Azure host is a sequence rather than a call.
    /// </remarks>
    /// <returns><see langword="true"/> if the resource was destroyed; <see langword="false"/> if it was already gone.</returns>
    internal async Task<bool> DeleteResourceAsync(string resourceId, CancellationToken ct)
    {
        var apiVersion = ApiVersionFor(resourceId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Absolute(resourceId, apiVersion));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"destroy '{resourceId}'", ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return true;
        }

        for (var attempt = 0; attempt < _pollAttempts; attempt++)
        {
            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);

            using var poll = new HttpRequestMessage(HttpMethod.Get, Absolute(resourceId, apiVersion));
            using var pollResponse = await SendAsync(poll, ct).ConfigureAwait(false);

            if (pollResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }
        }

        throw new AzureApiException(
            HttpStatusCode.Accepted,
            $"Azure accepted the deletion of '{resourceId}' but the resource was still present after "
            + $"{_pollAttempts} poll(s). It may still be billing, and any resource that depends on it cannot be "
            + "deleted until it is gone. Reconcile by tag before assuming the teardown finished.");
    }

    /// <summary>
    /// Lists every resource in the subscription carrying <paramref name="tagName"/>=<paramref name="tagValue"/>,
    /// following ARM's pagination to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This spans every resource <em>type</em>, which is the property the orphan sweep is built on: a NIC or a
    /// public IP left behind when a VM create failed halfway is returned here alongside the VMs, because all
    /// of them were tagged by the same create sequence.
    /// </para>
    /// <para>
    /// Note what it does not span: resource <em>groups</em>. ARM's <c>/resources</c> endpoint lists resources
    /// <em>within</em> groups and never the groups themselves, so a Servyx-created resource group is
    /// structurally invisible to this sweep. That is recorded rather than worked around — see the provisioner's
    /// remarks on what reconciliation does and does not clean up.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<ArmResourceSummary>> ListResourcesByTagAsync(
        string tagName,
        string tagValue,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagValue);

        var filter = Uri.EscapeDataString($"tagName eq '{tagName}' and tagValue eq '{tagValue}'");
        var next = new Uri(
            _armBaseAddress,
            string.Create(
                CultureInfo.InvariantCulture,
                $"/subscriptions/{_subscriptionId}/resources?api-version={ResourcesApiVersion}&$filter={filter}"));

        var resources = new List<ArmResourceSummary>();

        for (var page = 0; page < MaxSweepPages && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "list resources by tag", ct).ConfigureAwait(false);

            var envelope = Deserialize<ArmResourceListEnvelope>(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            resources.AddRange(envelope?.Value ?? []);

            next = string.IsNullOrWhiteSpace(envelope?.NextLink)
                ? null
                : new Uri(envelope.NextLink, UriKind.Absolute);
        }

        return resources;
    }

    // -----------------------------------------------------------------------------------------------
    // Authentication
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Stamps a valid bearer access token onto <paramref name="request"/> and sends it.
    /// </summary>
    /// <remarks>
    /// The token is the one obtained by <see cref="ExchangeTokenAsync"/>, reused while it remains valid. Unlike
    /// the DigitalOcean adapter, which stamps the stored secret itself, what travels here is a derived,
    /// time-boxed credential — the stored client secret never leaves the token exchange.
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(ct).ConfigureAwait(false));

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a currently-valid access token, exchanging the client secret for a new one only when the
    /// cached token is missing or has passed <see cref="ExpiryMargin"/> of its stated lifetime.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (TryReadCachedToken(out var cached))
        {
            return cached;
        }

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked under the gate: several ARM calls in one create sequence would otherwise each start
            // their own exchange, and every exchange is a request that carries the client secret.
            if (TryReadCachedToken(out cached))
            {
                return cached;
            }

            var token = await ExchangeTokenAsync(ct).ConfigureAwait(false);

            _accessToken = token.AccessToken;
            _accessTokenExpiresAt = _timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn);

            return token.AccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private bool TryReadCachedToken(out string token)
    {
        token = _accessToken ?? string.Empty;

        return _accessToken is not null
            && _timeProvider.GetUtcNow() + ExpiryMargin < _accessTokenExpiresAt;
    }

    /// <summary>
    /// Performs the OAuth2 client-credentials exchange against
    /// <c>login.microsoftonline.com/{tenant}/oauth2/v2.0/token</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The structural divergence from DigitalOcean.</strong> DigitalOcean's credential <em>is</em> the
    /// bearer token: resolve it, stamp it, send it. Azure's stored credential cannot be sent to ARM at all —
    /// it buys a token from a second service, on a second host, with its own failure modes (a wrong tenant is
    /// a 400 from the token service, not a 401 from ARM). That means this adapter has two authentication
    /// hosts where DigitalOcean has one, and one extra HTTP round trip that must succeed before any
    /// provisioning call can be attempted.
    /// </para>
    /// <para>
    /// The lease is opened, converted into the form body, and disposed inside the <c>using</c> below — the
    /// send happens after the buffer has already been zeroed. <see cref="SecretLease.ToUtf8String"/> is the
    /// one unavoidable materialisation (an <c>application/x-www-form-urlencoded</c> body is text), taken as
    /// late as possible exactly as that type's remarks require.
    /// </para>
    /// </remarks>
    private async Task<(string AccessToken, int ExpiresIn)> ExchangeTokenAsync(CancellationToken ct)
    {
        var tokenUri = new Uri(
            _loginBaseAddress,
            string.Create(CultureInfo.InvariantCulture, $"/{_tenantId}/oauth2/v2.0/token"));

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri);

        using (var lease = await _secretStore.GetAsync(_clientSecretUrn, ct).ConfigureAwait(false))
        {
            if (lease is null)
            {
                throw new InvalidOperationException(
                    $"No Azure client secret is stored at '{_clientSecretUrn}'. Store the service principal's "
                    + "client secret there before provisioning; the secret is never read from configuration or "
                    + "the environment.");
            }

            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", lease.ToUtf8String()),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", ArmScope),
            ]);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, "obtain an Azure access token", ct).ConfigureAwait(false);

        var token = Deserialize<AzureTokenResponse>(
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        if (token?.AccessToken is null || token.AccessToken.Length == 0)
        {
            throw new AzureApiException(
                response.StatusCode,
                "The Azure token endpoint answered successfully but returned no access_token, so no ARM call can "
                + "be authenticated. Nothing was created.");
        }

        if (token.ExpiresIn <= 0)
        {
            throw new AzureApiException(
                response.StatusCode,
                "The Azure token endpoint returned an access_token with no usable expires_in. Servyx will not "
                + "guess a lifetime for a credential, because a token cached past its expiry fails ARM calls "
                + "mid-sequence, which is precisely when orphans are created.");
        }

        return (token.AccessToken, token.ExpiresIn);
    }

    // -----------------------------------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------------------------------

    private Uri Absolute(string resourceId, string apiVersion) =>
        new(_armBaseAddress, string.Create(CultureInfo.InvariantCulture, $"{resourceId}?api-version={apiVersion}"));

    private static bool IsSucceeded(string? provisioningState) =>
        provisioningState is null || string.Equals(provisioningState, "Succeeded", StringComparison.Ordinal);

    private async Task<T> WaitForProvisioningAsync<T>(string resourceId, string apiVersion, CancellationToken ct)
        where T : class
    {
        for (var attempt = 0; attempt < _pollAttempts; attempt++)
        {
            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, Absolute(resourceId, apiVersion));
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, $"read '{resourceId}'", ct).ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var state = Deserialize<ArmProvisioningProbe>(payload)?.Properties?.ProvisioningState;

            if (IsSucceeded(state))
            {
                return Deserialize<T>(payload)
                    ?? throw new AzureApiException(
                        response.StatusCode,
                        $"Azure reports '{resourceId}' as provisioned but returned no resource object for it.");
            }

            if (string.Equals(state, "Failed", StringComparison.Ordinal)
                || string.Equals(state, "Canceled", StringComparison.Ordinal))
            {
                throw new AzureApiException(
                    response.StatusCode,
                    $"Azure reports '{resourceId}' as '{state}'. The resource exists in a failed state and may "
                    + "still be billing; it carries Servyx's tags, so an orphan sweep can find it.");
            }
        }

        throw new AzureApiException(
            HttpStatusCode.Accepted,
            $"'{resourceId}' did not reach a provisioned state within {_pollAttempts} poll(s). The resource "
            + "exists and may be billing; compensation will attempt to destroy it.");
    }

    private static T? Deserialize<T>(string payload)
        where T : class =>
        string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<T>(payload, SerializerOptions);

    /// <summary>
    /// Turns a non-success response into an <see cref="AzureApiException"/> carrying the status and the
    /// service's own error text — and nothing from the request, so neither the client secret nor an access
    /// token can ever reach a message.
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

        throw new AzureApiException(
            response.StatusCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Azure refused the attempt to {attempted}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}").Trim());
    }
}

/// <summary>
/// The identity Servyx authenticates to Azure as: a service principal's tenant, client id, and the
/// <see cref="SecretUrn"/> its client secret is stored at.
/// </summary>
/// <remarks>
/// <para>
/// Only the <em>URN</em> of the client secret is carried, never the secret. That is the whole reason this is
/// a type rather than three loose constructor parameters: a value that is safe to hold, log, or put in a
/// descriptor, holding the address of a value that is none of those things.
/// </para>
/// <para>
/// The tenant id and client id are deliberately plain strings and not secrets. They are identifiers, they
/// appear in every Azure portal URL and audit log, and treating them as secrets would put two more values
/// into the secret store for no security gain while making the store's contents less legible.
/// </para>
/// </remarks>
public sealed class AzureServicePrincipal
{
    /// <summary>Creates a service principal identity.</summary>
    /// <param name="tenantId">The Entra ID tenant (directory) the principal lives in.</param>
    /// <param name="clientId">The application (client) id of the principal.</param>
    /// <param name="clientSecretUrn">The URN the principal's client secret is stored at. Never the secret itself.</param>
    /// <exception cref="ArgumentException">Any identifier is blank, or <paramref name="clientSecretUrn"/> is not a real URN.</exception>
    public AzureServicePrincipal(string tenantId, string clientId, SecretUrn clientSecretUrn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        if (string.IsNullOrEmpty(clientSecretUrn.Value))
        {
            throw new ArgumentException(
                "An Azure client secret URN is required. Build one with SecretUrn.Create, e.g. "
                + "SecretUrn.Create(\"global\", \"azure\", \"api\", \"client-secret\"); a default(SecretUrn) is not "
                + "a valid URN.",
                nameof(clientSecretUrn));
        }

        TenantId = tenantId;
        ClientId = clientId;
        ClientSecretUrn = clientSecretUrn;
    }

    /// <summary>The Entra ID tenant (directory) the principal lives in.</summary>
    public string TenantId { get; }

    /// <summary>The application (client) id of the principal.</summary>
    public string ClientId { get; }

    /// <summary>Where the principal's client secret is stored. Only the address is held here.</summary>
    public SecretUrn ClientSecretUrn { get; }
}

/// <summary>
/// An Azure API call — to ARM or to the Entra ID token endpoint — that did not succeed.
/// </summary>
/// <remarks>
/// Carries the status code so a caller can distinguish a throttle (429) or an authorisation failure (401/403)
/// from a genuine service error, and never carries any part of the request — in particular not its
/// <c>Authorization</c> header and not the token exchange's form body.
/// </remarks>
public sealed class AzureApiException : Exception
{
    /// <summary>Creates an exception for a failed Azure API call.</summary>
    public AzureApiException(HttpStatusCode statusCode, string message)
        : base(message) => StatusCode = statusCode;

    /// <summary>Creates an exception for a failed Azure API call.</summary>
    public AzureApiException(HttpStatusCode statusCode, string message, Exception innerException)
        : base(message, innerException) => StatusCode = statusCode;

    /// <summary>Creates an exception with no status context.</summary>
    public AzureApiException()
        : this(HttpStatusCode.InternalServerError, "An Azure API call failed.")
    {
    }

    /// <summary>Creates an exception with no status context.</summary>
    public AzureApiException(string message)
        : this(HttpStatusCode.InternalServerError, message)
    {
    }

    /// <summary>Creates an exception with no status context.</summary>
    public AzureApiException(string message, Exception innerException)
        : this(HttpStatusCode.InternalServerError, message, innerException)
    {
    }

    /// <summary>The HTTP status Azure returned.</summary>
    public HttpStatusCode StatusCode { get; }
}
