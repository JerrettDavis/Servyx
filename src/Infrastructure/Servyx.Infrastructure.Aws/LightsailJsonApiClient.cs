using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The only code in this assembly that talks to <c>lightsail.&lt;region&gt;.amazonaws.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>AWS JSON 1.1, not the EC2 Query protocol — the request shape genuinely differs, as the report asked
/// to have confirmed.</strong> Every request is a bodiless-looking <c>POST /</c> whose actual routing lives in
/// an <c>X-Amz-Target: Lightsail_20161128.&lt;Action&gt;</c> header, with the parameters as a JSON object body
/// (<c>Content-Type: application/x-amz-json-1.1</c>) rather than EC2's flat, indexed form parameters. There is
/// no GET anywhere in this file: every Lightsail action, reads included, is a POST, because the protocol has no
/// query-string parameter shape at all — confirmed against AWS's published <c>GetInstances</c> request example,
/// which is a <c>POST</c> carrying <c>{}</c> as its body. That is one genuine asymmetry with
/// <see cref="Ec2QueryApiClient"/>: this client never exercises the GET half of
/// <see cref="AwsRequestSigner"/>, so the canonical-query code path <see cref="AwsSigV4.CanonicalQuery"/> covers
/// is reached only by the EC2 client in this assembly, not by this one.
/// </para>
/// <para>
/// <strong>Signing is unchanged and unperturbed.</strong> Every request still goes through the same
/// <see cref="AwsRequestSigner"/> the EC2 client uses, with <c>service = "lightsail"</c> in place of
/// <c>"ec2"</c>. The signer already treats every <c>x-amz-*</c> header as signed, so <c>X-Amz-Target</c> is
/// covered automatically without a single line of change to <see cref="AwsRequestSigner"/> itself — the
/// allow-list it builds needed no new case for a JSON-protocol service. That is the whole reason a fourth
/// adapter under the same SigV4 machinery was worth doing: the algorithm was never EC2-specific.
/// </para>
/// <para>
/// <strong>There is no <c>TagResource</c> call here, deliberately — for exactly the reason there is no
/// <c>CreateTags</c> in <see cref="Ec2QueryApiClient"/>.</strong> <c>CreateInstances</c> accepts a <c>tags</c>
/// array applied to the instance in the same call that creates it, so a follow-up <c>TagResource</c> would open
/// the same untagged-billing window EC2's design avoids. Lightsail's own documentation states something EC2's
/// does not: "If tags cannot be applied during resource creation, Lightsail rolls back the resource creation
/// process" — the platform itself guarantees the create is all-or-nothing with respect to tagging, which is a
/// stronger guarantee than "no window observed", and this client has no code path that could weaken it by
/// tagging after the fact.
/// </para>
/// <para>
/// <strong>Identity is caller-chosen, not provider-generated — the one structural simplification worth naming
/// up front.</strong> An EC2 instance id and a DigitalOcean droplet id are both assigned by the provider and
/// only known after a successful create call, which is why both adapters' compensation logic must fall back to
/// a tag sweep when a create fails without ever reporting the id it minted. A Lightsail instance's identity
/// <em>is</em> the name the caller chose in <c>instanceNames</c>, known before the request is ever sent — so
/// this client's <c>GetInstance</c> and <c>DeleteInstance</c> can always be called by the exact name a failed
/// <c>CreateInstances</c> would have used, with no sweep required. See <c>AwsLightsailProvisioner</c>'s
/// compensation logic.
/// </para>
/// <para>
/// Nothing here logs, for the same reason nothing in <see cref="Ec2QueryApiClient"/> does: this assembly
/// references no logging package, so there is no reachable path that could write a credential, a derived
/// signing key, or a signature.
/// </para>
/// </remarks>
internal sealed class LightsailJsonApiClient
{
    /// <summary>The service name in the SigV4 credential scope.</summary>
    internal const string ServiceName = "lightsail";

    /// <summary>The AWS JSON 1.1 target prefix every action name is appended to for the <c>X-Amz-Target</c> header.</summary>
    internal const string TargetPrefix = "Lightsail_20161128.";

    /// <summary>The content type every Lightsail request and response carries.</summary>
    internal const string ContentType = "application/x-amz-json-1.1";

    /// <summary>
    /// A hard ceiling on pages followed during one sweep, so a service paging bug cannot turn a sweep into an
    /// unbounded loop. Matches the EC2, DigitalOcean and Azure clients.
    /// </summary>
    private const int MaxSweepPages = 200;

    private readonly HttpClient _http;
    private readonly AwsRequestSigner _signer;
    private readonly Uri _endpoint;
    private readonly string _region;

    internal LightsailJsonApiClient(HttpClient http, AwsRequestSigner signer, string region, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        _http = http;
        _signer = signer;
        _region = region;
        _endpoint = endpoint ?? new Uri(DefaultEndpointFor(region), UriKind.Absolute);
    }

    /// <summary>The regional Lightsail endpoint for <paramref name="region"/>.</summary>
    /// <remarks>
    /// Regional, exactly as EC2's is and for the same reason: the region names both the host and the SigV4
    /// credential scope, so it is adapter state fixed at construction rather than a per-request parameter. See
    /// <c>AwsLightsailProvisioner</c>'s remarks on what that means for <c>OrphanScope.ProviderWide</c>.
    /// </remarks>
    internal static string DefaultEndpointFor(string region) =>
        string.Create(CultureInfo.InvariantCulture, $"https://lightsail.{region}.amazonaws.com/");

    /// <summary>The region every call from this client is scoped to.</summary>
    internal string Region => _region;

    /// <summary>
    /// Creates one instance, applying every Servyx tag in the same call.
    /// </summary>
    /// <remarks>
    /// The single billable call in this assembly, and the only one that creates anything. Unlike
    /// <see cref="Ec2QueryApiClient.RunInstancesAsync"/>, the response carries no instance object — Lightsail's
    /// <c>CreateInstances</c> answers with an array of pending <c>Operation</c> records, not the resource itself
    /// — so this method returns nothing and the caller polls <see cref="GetInstanceAsync"/> by the name it
    /// already chose. That poll is therefore doing double duty a launch-and-poll against EC2 does not: EC2's
    /// <c>RunInstances</c> hands back a real (address-less) instance for free, so its first poll is only for an
    /// address; this client's first <c>GetInstance</c> call is for the instance's very existence as an object.
    /// </remarks>
    internal async Task CreateInstancesAsync(JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        await SendAsync("CreateInstances", body, "launch an instance", ct).ConfigureAwait(false);
    }

    /// <summary>Reads one instance by name, or <see langword="null"/> if Lightsail no longer knows it.</summary>
    /// <remarks>
    /// Unlike EC2, where a terminated instance stays visible to <c>DescribeInstances</c> for up to about an
    /// hour and has to be filtered out by state, Lightsail's documented behaviour for an unknown instance name
    /// is a <c>NotFoundException</c> — this adapter's best understanding, from AWS's published API reference,
    /// is that a deleted instance simply stops existing as far as <c>GetInstance</c> is concerned, with no
    /// lingering "gone but still reported" state to reason about. That could not be confirmed against a live
    /// account as part of this change; if it is ever found to be wrong, the fix belongs in
    /// <c>AwsLightsailProvisioner.RefreshAsync</c>, mirroring <c>Ec2Instance.GoneStates</c>.
    /// </remarks>
    internal async Task<LightsailInstance?> GetInstanceAsync(string instanceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var body = new JsonObject { ["instanceName"] = instanceName };

        try
        {
            var response = await SendAsync("GetInstance", body, "read an instance", ct).ConfigureAwait(false);
            return LightsailInstance.From(response?["instance"] as JsonObject);
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, LightsailErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>
    /// Lists every instance in the region carrying <paramref name="tagKey"/>=<paramref name="tagValue"/>,
    /// following <c>nextPageToken</c> pagination to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pagination is followed rather than truncated for the same reason the EC2 sweep follows
    /// <c>nextToken</c>: stopping at the first page would report "no orphans beyond page one" as "no orphans".
    /// </para>
    /// <para>
    /// <strong>The genuine EC2 divergence: there is no server-side tag filter at all.</strong> EC2's
    /// <c>DescribeInstances</c> accepts a <c>Filter.1.Name=tag:&lt;key&gt;</c> parameter, so the service does
    /// some of the narrowing before the response ever reaches this process. Lightsail's <c>GetInstances</c>
    /// request accepts only <c>pageToken</c> — confirmed against AWS's published request syntax, which lists no
    /// filter parameter of any kind — so every instance in the region crosses the wire on every sweep, tagged or
    /// not, and the filtering by <paramref name="tagKey"/>/<paramref name="tagValue"/> below is entirely this
    /// process's own work. A large, mostly-unmanaged Lightsail account therefore pays more per sweep than the
    /// equivalent EC2 account would, in bytes transferred if not in API calls.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<LightsailInstance>> GetInstancesByTagAsync(
        string tagKey,
        string tagValue,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagValue);

        var results = new List<LightsailInstance>();
        string? pageToken = null;

        for (var page = 0; page < MaxSweepPages; page++)
        {
            var body = new JsonObject();
            if (pageToken is not null)
            {
                body["pageToken"] = pageToken;
            }

            var response = await SendAsync("GetInstances", body, "list instances by tag", ct).ConfigureAwait(false);
            var instances = response?["instances"] as JsonArray;

            if (instances is not null)
            {
                foreach (var node in instances)
                {
                    var instance = LightsailInstance.From(node as JsonObject);
                    if (instance is not null
                        && instance.Tags.TryGetValue(tagKey, out var value)
                        && string.Equals(value, tagValue, StringComparison.Ordinal))
                    {
                        results.Add(instance);
                    }
                }
            }

            pageToken = LightsailJson.Text(response, "nextPageToken");
            if (pageToken is null)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>Deletes an instance by name. Returns <see langword="false"/> if Lightsail no longer knows it.</summary>
    internal async Task<bool> DeleteInstanceAsync(string instanceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var body = new JsonObject { ["instanceName"] = instanceName };

        try
        {
            await SendAsync("DeleteInstance", body, "delete an instance", ct).ConfigureAwait(false);
            return true;
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, LightsailErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return false;
        }
    }

    private async Task<JsonObject?> SendAsync(string action, JsonObject body, string attempted, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, ContentType),
        };

        // The whole of Lightsail's operation routing: one header, not a URL path segment and not a form field.
        // AwsRequestSigner signs every x-amz-* header already present on the message, so this needs no change
        // to the signer to be covered by the signature.
        request.Headers.TryAddWithoutValidation("X-Amz-Target", TargetPrefix + action);

        await _signer.SignAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildFailure(response.StatusCode, payload, attempted);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new AwsApiException(
                response.StatusCode,
                null,
                $"Lightsail's response to the attempt to {attempted} was not well-formed JSON.",
                e);
        }
    }

    /// <summary>
    /// Turns a non-success response into an <see cref="AwsApiException"/> carrying the status and Lightsail's own
    /// error type and message — and nothing from the request.
    /// </summary>
    /// <remarks>
    /// AWS JSON 1.1 errors carry their type in a <c>__type</c> body field (sometimes namespaced as
    /// <c>com.amazon.coral.service#SomeException</c>) rather than in EC2's <c>&lt;Errors&gt;&lt;Error&gt;</c>
    /// XML shape; the namespace prefix, when present, is stripped so callers can match on the short name (e.g.
    /// <see cref="LightsailErrorCodes.NotFound"/>) the way they already do for EC2's error codes.
    /// </remarks>
    private static AwsApiException BuildFailure(HttpStatusCode status, string payload, string attempted)
    {
        string? code = null;
        string? message = null;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var error = JsonNode.Parse(payload) as JsonObject;
                var type = LightsailJson.Text(error, "__type");
                code = type is null ? null : type[(type.IndexOf('#') + 1)..];
                message = LightsailJson.Text(error, "message");
            }
            catch (JsonException)
            {
                // A non-JSON error body (a load balancer's HTML, say) is reported by status alone rather than
                // being allowed to mask the failure it describes.
            }
        }

        return new AwsApiException(
            status,
            code,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Lightsail refused the attempt to {attempted}: HTTP {(int)status}. {code} {message}").Trim());
    }
}
