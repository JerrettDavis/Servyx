using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The only code in this assembly that talks to <c>ec2.&lt;region&gt;.amazonaws.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The EC2 Query API, not the JSON protocol — and the reason is that there is no JSON protocol.</strong>
/// Most modern AWS services speak <c>awsJson1_1</c> or <c>restJson1</c>, and if EC2 did, this file would use
/// it. EC2 does not: it is one of the original services and its only wire protocol is the <c>ec2</c> query
/// protocol — flat, indexed form parameters in, XML out. Every AWS SDK, including the official .NET one,
/// speaks that protocol to EC2 for exactly this reason. So the choice made here is not a preference; it is the
/// only option, and the cost of it is that this file parses XML where its two siblings parse JSON.
/// </para>
/// <para>
/// <strong>GET for reads, POST for writes, and both are load-bearing.</strong> Reads
/// (<c>DescribeInstances</c>, <c>DescribeVolumes</c>) go out as <c>GET</c> with their parameters in the query
/// string; writes (<c>RunInstances</c>, <c>TerminateInstances</c>, <c>DeleteVolume</c>) go out as <c>POST</c>
/// with a form-encoded body. AWS accepts either shape for either, so this is a choice, and it is made so that
/// <em>both</em> halves of the signing algorithm are exercised by real traffic rather than only by unit tests:
/// a GET has a non-empty canonical query and an empty payload hash, a POST has an empty canonical query and a
/// real payload hash. It also happens to be the right way round on the merits — a launch carries tag
/// specifications and base64 user-data that can run to kilobytes, which does not belong in a URL.
/// </para>
/// <para>
/// <strong>There is no <c>CreateTags</c> call here, deliberately.</strong> It is the obvious way to tag a new
/// instance and it is the wrong one. <c>RunInstances</c> accepts <c>TagSpecification</c> entries that apply
/// tags in the <em>same call</em> that creates the instance, so there is no window in which a billing instance
/// exists untagged and therefore invisible to an orphan sweep. A follow-up <c>CreateTags</c> would open exactly
/// that window — the failure mode <see cref="Servyx.Domain.Provisioning.ProvisioningCapabilities.TagQuery"/>'s
/// remarks describe as the difference between a strong and a weak negative sweep result. This client therefore
/// has no way to tag a resource after the fact, which is the point: the atomic path is the only path.
/// </para>
/// <para>
/// <strong>The credential is never here.</strong> Every request goes through <see cref="AwsRequestSigner"/>,
/// which resolves the key pair from the secret store, signs, and disposes the lease before the request is sent.
/// This type holds no key, no signature, and nothing derived from either; it holds a signer, which holds two
/// URNs.
/// </para>
/// <para>
/// <strong>Nothing here logs.</strong> This assembly references no logging package at all (see the .csproj), so
/// there is no reachable code path that could write a credential or a signed request. The exception messages
/// below are built from the HTTP status and EC2's own error XML, never from the request.
/// </para>
/// </remarks>
internal sealed class Ec2QueryApiClient
{
    /// <summary>The EC2 API version every request declares.</summary>
    internal const string ApiVersion = "2016-11-15";

    /// <summary>The service name in the SigV4 credential scope.</summary>
    internal const string ServiceName = "ec2";

    /// <summary>The largest page <c>DescribeInstances</c> accepts.</summary>
    internal const int InstancePageSize = 1000;

    /// <summary>The largest page <c>DescribeVolumes</c> accepts.</summary>
    internal const int VolumePageSize = 500;

    /// <summary>
    /// A hard ceiling on pages followed during one sweep, so a service paging bug cannot turn a sweep into an
    /// unbounded loop. Matches the DigitalOcean and Azure clients.
    /// </summary>
    private const int MaxSweepPages = 200;

    private static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
    };

    private readonly HttpClient _http;
    private readonly AwsRequestSigner _signer;
    private readonly Uri _endpoint;
    private readonly string _region;

    internal Ec2QueryApiClient(HttpClient http, AwsRequestSigner signer, string region, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        _http = http;
        _signer = signer;
        _region = region;
        _endpoint = endpoint ?? new Uri(DefaultEndpointFor(region), UriKind.Absolute);
    }

    /// <summary>The regional EC2 endpoint for <paramref name="region"/>.</summary>
    /// <remarks>
    /// EC2's endpoint carries the region in its <em>hostname</em>, and the region also appears in the SigV4
    /// credential scope. That is why region is adapter state on this adapter rather than a per-request
    /// provisioning parameter as it is for DigitalOcean and Azure: changing it changes the host and invalidates
    /// every signature.
    /// </remarks>
    internal static string DefaultEndpointFor(string region) =>
        string.Create(CultureInfo.InvariantCulture, $"https://ec2.{region}.amazonaws.com/");

    /// <summary>The region every call from this client is scoped to.</summary>
    internal string Region => _region;

    /// <summary>
    /// Launches one instance, applying every Servyx tag in the same call.
    /// </summary>
    /// <remarks>
    /// The single billable call in this assembly, and the only one that creates anything.
    /// </remarks>
    internal async Task<Ec2Instance> RunInstancesAsync(
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        CancellationToken ct)
    {
        var response = await PostAsync("RunInstances", parameters, "launch an instance", ct).ConfigureAwait(false);

        var instance = Ec2Xml.Items(response, "instancesSet")
            .Select(Ec2Instance.From)
            .FirstOrDefault(i => i is not null);

        return instance
            ?? throw new AwsApiException(
                HttpStatusCode.OK,
                null,
                "EC2 accepted the RunInstances request but returned no instance, so Servyx has no id to record "
                + "or to compensate with. The instance may nonetheless exist and be billing; it carries Servyx's "
                + "tags, so reconcile by tag before assuming nothing was created.");
    }

    /// <summary>Reads one instance by id, or <see langword="null"/> if EC2 no longer knows it.</summary>
    /// <remarks>
    /// A terminated instance is <em>not</em> filtered out here — EC2 still reports it, and deciding that a
    /// terminated instance is "gone" is a provisioning judgement rather than a transport one, so it is made in
    /// <c>AwsEc2Provisioner.RefreshAsync</c> where it is visible.
    /// </remarks>
    internal async Task<Ec2Instance?> DescribeInstanceAsync(string instanceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var response = await GetAsync(
                "DescribeInstances",
                [new KeyValuePair<string, string>("InstanceId.1", instanceId)],
                "read an instance",
                "InvalidInstanceID.NotFound",
                ct)
            .ConfigureAwait(false);

        if (response is null)
        {
            return null;
        }

        return Ec2Xml.Items(response, "reservationSet")
            .SelectMany(reservation => Ec2Xml.Items(reservation, "instancesSet"))
            .Select(Ec2Instance.From)
            .FirstOrDefault(i => i is not null);
    }

    /// <summary>
    /// Lists every instance in the region carrying <paramref name="tagKey"/>=<paramref name="tagValue"/>,
    /// following EC2's <c>nextToken</c> pagination to the end.
    /// </summary>
    /// <remarks>
    /// Pagination is followed rather than truncated because this is the orphan sweep's only view of the
    /// provider: stopping at the first page would report "no orphans beyond page one" as "no orphans", which is
    /// the precise failure the <c>TagQuery</c> capability exists to prevent. EC2 makes that trap easier to fall
    /// into than DigitalOcean does — a <c>nextToken</c> is a bare opaque string in the response body rather
    /// than a ready-made next-page URL, so a caller has to know to feed it back.
    /// </remarks>
    internal Task<IReadOnlyList<Ec2Instance>> DescribeInstancesByTagAsync(
        string tagKey,
        string tagValue,
        CancellationToken ct) =>
        PaginateAsync(
            "DescribeInstances",
            TagFilter(tagKey, tagValue),
            InstancePageSize,
            "list instances by tag",
            response => Ec2Xml.Items(response, "reservationSet")
                .SelectMany(reservation => Ec2Xml.Items(reservation, "instancesSet"))
                .Select(Ec2Instance.From),
            ct);

    /// <summary>
    /// Lists every EBS volume in the region carrying <paramref name="tagKey"/>=<paramref name="tagValue"/>,
    /// following pagination to the end.
    /// </summary>
    /// <remarks>
    /// A separate action from <c>DescribeInstances</c>, and a separate sweep: a volume that outlived its
    /// instance is attached to nothing, so no amount of instance-listing would find it.
    /// </remarks>
    internal Task<IReadOnlyList<Ec2Volume>> DescribeVolumesByTagAsync(
        string tagKey,
        string tagValue,
        CancellationToken ct) =>
        PaginateAsync(
            "DescribeVolumes",
            TagFilter(tagKey, tagValue),
            VolumePageSize,
            "list volumes by tag",
            response => Ec2Xml.Items(response, "volumeSet").Select(Ec2Volume.From),
            ct);

    /// <summary>Terminates an instance. Returns <see langword="false"/> if EC2 no longer knows it.</summary>
    internal async Task<bool> TerminateInstanceAsync(string instanceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        try
        {
            await PostAsync(
                    "TerminateInstances",
                    [new KeyValuePair<string, string>("InstanceId.1", instanceId)],
                    "terminate an instance",
                    ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, "InvalidInstanceID.NotFound", StringComparison.Ordinal))
        {
            return false;
        }
    }

    /// <summary>Deletes an unattached EBS volume. Returns <see langword="false"/> if EC2 no longer knows it.</summary>
    internal async Task<bool> DeleteVolumeAsync(string volumeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);

        try
        {
            await PostAsync(
                    "DeleteVolume",
                    [new KeyValuePair<string, string>("VolumeId", volumeId)],
                    "delete a volume",
                    ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, "InvalidVolume.NotFound", StringComparison.Ordinal))
        {
            return false;
        }
    }

    /// <summary>The <c>Filter.1</c> parameters that select resources carrying one exact tag.</summary>
    /// <remarks>
    /// The tag key travels as <c>tag:&lt;key&gt;</c> — EC2's own spelling — with no encoding of the key itself.
    /// The whole parameter is percent-encoded on the way onto the wire, so the <c>:</c> becomes <c>%3A</c>, but
    /// the key EC2 matches on is the literal Servyx key. See <c>ServyxEc2Tags</c>.
    /// </remarks>
    private static List<KeyValuePair<string, string>> TagFilter(string tagKey, string tagValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagValue);

        return
        [
            new KeyValuePair<string, string>("Filter.1.Name", "tag:" + tagKey),
            new KeyValuePair<string, string>("Filter.1.Value.1", tagValue),
        ];
    }

    private async Task<IReadOnlyList<T>> PaginateAsync<T>(
        string action,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        int pageSize,
        string attempted,
        Func<XElement, IEnumerable<T?>> project,
        CancellationToken ct)
        where T : class
    {
        var results = new List<T>();
        string? nextToken = null;

        for (var page = 0; page < MaxSweepPages; page++)
        {
            var pageParameters = new List<KeyValuePair<string, string>>(parameters)
            {
                new("MaxResults", pageSize.ToString(CultureInfo.InvariantCulture)),
            };

            if (nextToken is not null)
            {
                pageParameters.Add(new KeyValuePair<string, string>("NextToken", nextToken));
            }

            var response = await GetAsync(action, pageParameters, attempted, notFoundCode: null, ct).ConfigureAwait(false);
            if (response is null)
            {
                break;
            }

            results.AddRange(project(response).Where(x => x is not null).Select(x => x!));

            nextToken = Ec2Xml.Text(response, "nextToken");
            if (nextToken is null)
            {
                break;
            }
        }

        return results;
    }

    private async Task<XElement?> GetAsync(
        string action,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        string attempted,
        string? notFoundCode,
        CancellationToken ct)
    {
        var query = Encode(WithAction(action, parameters));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "?" + query));

        try
        {
            return await SendAsync(request, attempted, ct).ConfigureAwait(false);
        }
        catch (AwsApiException e) when (notFoundCode is not null
            && string.Equals(e.ErrorCode, notFoundCode, StringComparison.Ordinal))
        {
            return null;
        }
    }

    private async Task<XElement> PostAsync(
        string action,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        string attempted,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(
                Encode(WithAction(action, parameters)),
                Encoding.UTF8,
                "application/x-www-form-urlencoded"),
        };

        return await SendAsync(request, attempted, ct).ConfigureAwait(false);
    }

    private async Task<XElement> SendAsync(HttpRequestMessage request, string attempted, CancellationToken ct)
    {
        await _signer.SignAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildFailure(response.StatusCode, response.ReasonPhrase, payload, attempted);
        }

        return Parse(payload, response.StatusCode, attempted);
    }

    /// <summary>Appends the two parameters every EC2 Query request carries.</summary>
    private static List<KeyValuePair<string, string>> WithAction(
        string action,
        IReadOnlyList<KeyValuePair<string, string>> parameters) =>
    [
        new("Action", action),
        new("Version", ApiVersion),
        .. parameters,
    ];

    /// <summary>
    /// Renders parameters as an <c>application/x-www-form-urlencoded</c> string using the signer's own
    /// RFC 3986 encoder.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> <see cref="FormUrlEncodedContent"/>, which encodes a space as <c>+</c>. SigV4
    /// requires <c>%20</c>, and a body that disagrees with what was hashed is a signature mismatch — so both
    /// the query string and the form body are built by <see cref="AwsSigV4.UriEncode"/>, the same function the
    /// canonicaliser uses.
    /// </remarks>
    private static string Encode(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join(
            '&',
            parameters.Select(p => $"{AwsSigV4.UriEncode(p.Key, true)}={AwsSigV4.UriEncode(p.Value, true)}"));

    private static XElement Parse(string payload, HttpStatusCode status, string attempted)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new AwsApiException(
                status,
                null,
                $"EC2 answered the attempt to {attempted} with HTTP {(int)status} and an empty body, so Servyx "
                + "cannot tell what happened. Treat this as a possible orphan and reconcile by tag.");
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(payload), SafeXmlSettings);
            return XDocument.Load(reader).Root
                ?? throw new AwsApiException(status, null, $"EC2's response to the attempt to {attempted} had no root element.");
        }
        catch (XmlException e)
        {
            throw new AwsApiException(
                status,
                null,
                $"EC2's response to the attempt to {attempted} was not well-formed XML.",
                e);
        }
    }

    /// <summary>
    /// Turns a non-success response into an <see cref="AwsApiException"/> carrying the status and EC2's own
    /// error code and text — and nothing from the request.
    /// </summary>
    private static AwsApiException BuildFailure(
        HttpStatusCode status,
        string? reasonPhrase,
        string payload,
        string attempted)
    {
        string? code = null;
        string? message = null;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(payload), SafeXmlSettings);
                var root = XDocument.Load(reader).Root;

                // EC2 answers <Response><Errors><Error>...; some AWS services answer <ErrorResponse><Error>...
                // Both shapes are read, because a caller reasoning about an error code should not have to know
                // which one a given endpoint produced.
                var error = Ec2Xml.Child(Ec2Xml.Child(root, "Errors"), "Error") ?? Ec2Xml.Child(root, "Error");

                code = Ec2Xml.Text(error, "Code");
                message = Ec2Xml.Text(error, "Message");
            }
            catch (XmlException)
            {
                // A non-XML error body (a load balancer's HTML, say) is reported by status alone rather than
                // being allowed to mask the failure it describes.
            }
        }

        return new AwsApiException(
            status,
            code,
            string.Create(
                CultureInfo.InvariantCulture,
                $"AWS refused the attempt to {attempted}: HTTP {(int)status} {reasonPhrase}. {code} {message}").Trim());
    }
}
