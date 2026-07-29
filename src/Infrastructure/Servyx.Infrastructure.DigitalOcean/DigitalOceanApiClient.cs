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
/// shared framework. The surface it needs is a handful of calls wide, which is well under the cost of a
/// dependency.
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

    /// <summary>The action status DigitalOcean reports once an action has finished successfully.</summary>
    private const string ActionCompleted = "completed";

    /// <summary>The action status DigitalOcean reports for an action that finished unsuccessfully.</summary>
    private const string ActionErrored = "errored";

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
    /// Submits a <em>CPU-and-memory-only</em> resize of one droplet and returns the action DigitalOcean
    /// created to track it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The returned action is a receipt, not an outcome.</strong> DigitalOcean answers this POST
    /// while the resize is still queued, so the action almost always comes back <c>in-progress</c>. Nothing
    /// may treat a successful return from this method as a completed resize; that is what
    /// <see cref="PollActionAsync"/> is for.
    /// </para>
    /// <para>
    /// <strong>The disk is never grown.</strong> The body is a <see cref="ResizeDropletActionRequest"/>,
    /// whose <c>disk</c> member is a get-only <see langword="false"/> — see that type's remarks. This method
    /// takes no flag that could change it, so the irreversible disk-inclusive resize is not reachable from
    /// here by any argument.
    /// </para>
    /// </remarks>
    internal async Task<DropletActionResource> ResizeDropletAsync(long dropletId, string sizeSlug, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sizeSlug);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"v2/droplets/{dropletId}/actions"))
        {
            Content = JsonContent.Create(
                new ResizeDropletActionRequest { Size = sizeSlug },
                options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "resize a droplet", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletActionEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Action
            ?? throw new DigitalOceanApiException(
                response.StatusCode,
                "DigitalOcean accepted the droplet resize request but returned no action object, so Servyx has no "
                + "action id to poll and cannot tell whether the resize ran. Re-read the droplet before retrying: a "
                + "second resize submitted against a droplet already being resized is a second mutation, not a retry.");
    }

    /// <summary>
    /// Submits a <em>rebuild</em> of one droplet — the action that reimages its boot disk — and returns the
    /// action DigitalOcean created to track it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This deletes everything on the droplet.</strong> A rebuild replaces the boot disk's contents
    /// with a fresh copy of the image: the installed game, its configuration and every save file are gone and
    /// cannot be recovered from the droplet afterwards. The droplet keeps its id and its address; nothing
    /// else about it survives. Nothing in this assembly calls this method without an approved plan hash and a
    /// separately-supplied acknowledgement of <c>DataImpact.Destroyed</c> having both been checked
    /// first — see <c>DigitalOceanDropletProvisioner.Rebuild.cs</c>.
    /// </para>
    /// <para>
    /// <strong>The returned action is a receipt, not an outcome.</strong> As with
    /// <see cref="ResizeDropletAsync"/>, DigitalOcean answers this POST while the rebuild is still queued —
    /// and a rebuild takes minutes. Nothing may treat a successful return from this method as a completed
    /// rebuild; that is what <see cref="PollActionAsync"/> is for.
    /// </para>
    /// <para>
    /// <strong>This method cannot resize.</strong> The body is a
    /// <see cref="RebuildDropletActionRequest"/>, whose <c>type</c> is a get-only <c>rebuild</c> and which
    /// carries no <c>size</c> member at all — just as <see cref="ResizeDropletAsync"/>'s body is a
    /// <see cref="ResizeDropletActionRequest"/> that cannot become a rebuild. Neither method takes an action
    /// type, so no argument at either call site can turn one operation into the other.
    /// </para>
    /// </remarks>
    internal async Task<DropletActionResource> RebuildDropletAsync(long dropletId, string imageRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRef);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"v2/droplets/{dropletId}/actions"))
        {
            Content = JsonContent.Create(
                new RebuildDropletActionRequest { Image = imageRef },
                options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "rebuild a droplet", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletActionEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Action
            ?? throw new DigitalOceanApiException(
                response.StatusCode,
                "DigitalOcean accepted the droplet rebuild request but returned no action object, so Servyx has no "
                + "action id to poll and cannot tell whether the rebuild ran. Do NOT resubmit: a rebuild that was "
                + "accepted is already erasing the boot disk, and a second one erases it again. Read the droplet and "
                + "the account's actions at DigitalOcean before doing anything else.");
    }

    /// <summary>Reads one action by id, or <see langword="null"/> if the provider does not report it.</summary>
    internal async Task<DropletActionResource?> GetActionAsync(long actionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"v2/actions/{actionId}"));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "read a droplet action", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletActionEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Action;
    }

    /// <summary>
    /// Re-reads action <paramref name="actionId"/> until DigitalOcean reports it <c>completed</c> or
    /// <c>errored</c>, or until <paramref name="attempts"/> reads have been made without either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three outcomes, and the third is not a failure.</strong> An action that is still
    /// <c>in-progress</c> when the last attempt is spent yields
    /// <see cref="DropletActionOutcome.StillInProgress"/> — distinct from
    /// <see cref="DropletActionOutcome.Errored"/> because the two demand opposite responses: an errored
    /// action is over and may be retried, whereas a running one is not over, and "retrying" it submits the
    /// same mutation a second time. Collapsing them into one failure answer is precisely the mistake this
    /// signature is shaped to prevent.
    /// </para>
    /// <para>
    /// An action the API does not return at all (404) is treated as "not yet observable" rather than as
    /// success or failure, so it keeps the poll running and, if it never resolves, ends as
    /// <see cref="DropletActionOutcome.StillInProgress"/>. That is the safe direction: the one thing this
    /// method must never do is report an unconfirmed mutation as finished.
    /// </para>
    /// </remarks>
    /// <param name="actionId">The action to watch.</param>
    /// <param name="interval">How long to wait between reads. <see cref="TimeSpan.Zero"/> polls back-to-back.</param>
    /// <param name="attempts">How many reads to make before giving up. At least one.</param>
    /// <param name="timeProvider">The clock the waits are taken against, so tests need not really wait.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<DropletActionPoll> PollActionAsync(
        long actionId,
        TimeSpan interval,
        int attempts,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);

        string? status = null;
        string? message = null;

        for (var poll = 1; poll <= attempts; poll++)
        {
            var action = await GetActionAsync(actionId, ct).ConfigureAwait(false);
            status = action?.Status;
            message = action?.Message;

            if (string.Equals(status, ActionCompleted, StringComparison.Ordinal))
            {
                return new DropletActionPoll(DropletActionOutcome.Completed, actionId, status, message, poll);
            }

            if (string.Equals(status, ActionErrored, StringComparison.Ordinal))
            {
                return new DropletActionPoll(DropletActionOutcome.Errored, actionId, status, message, poll);
            }

            if (poll < attempts && interval > TimeSpan.Zero)
            {
                await Task.Delay(interval, timeProvider, ct).ConfigureAwait(false);
            }
        }

        return new DropletActionPoll(DropletActionOutcome.StillInProgress, actionId, status, message, attempts);
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
/// How a polled DigitalOcean action ended, from this adapter's point of view.
/// </summary>
/// <remarks>
/// Three values, not two: <see cref="StillInProgress"/> is neither a success nor a failure, and giving it
/// its own name is what stops an unfinished mutation being reported as either.
/// </remarks>
internal enum DropletActionOutcome
{
    /// <summary>DigitalOcean reported the action <c>completed</c>. The only success.</summary>
    Completed = 1,

    /// <summary>DigitalOcean reported the action <c>errored</c>. The operation is over and it did not work.</summary>
    Errored = 2,

    /// <summary>
    /// The attempts were spent and DigitalOcean had still not reported the action finished. The operation
    /// may yet complete at the provider; nothing here knows either way.
    /// </summary>
    StillInProgress = 3,
}

/// <summary>The result of watching one DigitalOcean action to a conclusion — or to a timeout.</summary>
/// <param name="Outcome">Which of the three ends was reached.</param>
/// <param name="ActionId">The action that was watched, so an operator can look it up at the provider.</param>
/// <param name="Status">The last status DigitalOcean reported, or <see langword="null"/> if it reported none.</param>
/// <param name="Message">The provider's own explanatory message, when it supplied one.</param>
/// <param name="Polls">How many reads were made. Evidence that the answer came from an observation.</param>
internal sealed record DropletActionPoll(
    DropletActionOutcome Outcome,
    long ActionId,
    string? Status,
    string? Message,
    int Polls);

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
