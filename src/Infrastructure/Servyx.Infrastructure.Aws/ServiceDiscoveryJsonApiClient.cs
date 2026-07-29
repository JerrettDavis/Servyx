using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The only code in this assembly that talks to <c>servicediscovery.&lt;region&gt;.amazonaws.com</c> — AWS Cloud
/// Map.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Five actions, and none of them registers an instance.</strong> That absence is the point rather than a
/// gap. When an ECS service is created with a <c>serviceRegistries</c> entry, ECS itself registers the task's
/// network interface into the Cloud Map service and deregisters it when the task stops — including on every
/// routine replacement. Servyx therefore creates the <em>service</em> and never touches an <em>instance</em>:
/// there is no <c>RegisterInstance</c> call here, no <c>DeregisterInstance</c>, and consequently no window in
/// which a running task is unregistered because Servyx had not got round to it yet. See
/// <c>AwsEcsFargateProvisioner</c>'s service-discovery remarks.
/// </para>
/// <para>
/// <strong>There is deliberately no namespace-creating call either.</strong> <c>CreatePrivateDnsNamespace</c>
/// exists and is not used: it creates a Route 53 private hosted zone that bills monthly, outlives every server in
/// it, and would be invisible to a sweep that enumerates ECS services in one cluster. See
/// <see cref="CloudMapNamespace"/>.
/// </para>
/// <para>
/// <strong>Absence is a value; <c>ResourceInUse</c> is not.</strong> Cloud Map reports a missing service or
/// namespace as an HTTP 400 with a typed error, which the reads below turn into <see langword="null"/> and the
/// delete turns into <see langword="false"/> — a caller asking "is it gone" wants "yes", not an exception. A
/// service that still has instances registered is the opposite case and is allowed to surface: it means the
/// cleanup did <em>not</em> happen, and a caller that swallowed it would report a completed destroy over a
/// resource that still exists.
/// </para>
/// <para>
/// Nothing here logs, for the same reason nothing in <see cref="EcsJsonApiClient"/> does: this assembly
/// references no logging package, so there is no reachable path that could write a credential, a derived signing
/// key, or a signature.
/// </para>
/// </remarks>
internal sealed class ServiceDiscoveryJsonApiClient
{
    private readonly HttpClient _http;
    private readonly AwsRequestSigner _signer;
    private readonly Uri _endpoint;
    private readonly string _region;

    internal ServiceDiscoveryJsonApiClient(
        HttpClient http,
        AwsRequestSigner signer,
        string region,
        Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        _http = http;
        _signer = signer;
        _region = region;
        _endpoint = endpoint ?? new Uri(DefaultEndpointFor(region), UriKind.Absolute);
    }

    /// <summary>The regional Cloud Map endpoint for <paramref name="region"/>.</summary>
    internal static string DefaultEndpointFor(string region) =>
        string.Create(CultureInfo.InvariantCulture, $"https://servicediscovery.{region}.amazonaws.com/");

    /// <summary>The region every call from this client is scoped to.</summary>
    internal string Region => _region;

    /// <summary>
    /// Creates one Cloud Map service, applying every Servyx tag in the same call.
    /// </summary>
    /// <remarks>
    /// Tags travel inline in this request, so the service never exists untagged — the same guarantee ECS's
    /// <c>CreateService</c> gives, and needed for the same reason: a resource that cannot be attributed back to
    /// Servyx is a resource a sweep must not delete.
    /// </remarks>
    /// <exception cref="AwsApiException">Cloud Map refused the create, or answered without a service object.</exception>
    internal async Task<CloudMapService> CreateServiceAsync(JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var response = await SendAsync("CreateService", body, "create a Cloud Map service", ct).ConfigureAwait(false);

        return CloudMapService.From(response?["Service"] as JsonObject)
            ?? throw new AwsApiException(
                HttpStatusCode.OK,
                null,
                "AWS Cloud Map accepted the CreateService call but its response carried no Service object, so "
                + "Servyx has no registry ARN to attach the ECS service to and no ARN for a resource that may "
                + "now exist. The Cloud Map service registers no instance until an ECS service points at it, so "
                + "it bills nothing meanwhile; it is nonetheless a real object, and it is not attributable from "
                + "an ECS sweep.");
    }

    /// <summary>Reads one Cloud Map service by id or ARN, or <see langword="null"/> if it is gone.</summary>
    internal async Task<CloudMapService?> GetServiceAsync(string service, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        try
        {
            var response = await SendAsync(
                    "GetService",
                    new JsonObject { ["Id"] = service },
                    "read a Cloud Map service",
                    ct)
                .ConfigureAwait(false);

            return CloudMapService.From(response?["Service"] as JsonObject);
        }
        catch (AwsApiException e) when (
            string.Equals(e.ErrorCode, ServiceDiscoveryErrorCodes.ServiceNotFound, StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Reads one Cloud Map namespace by id or ARN, or <see langword="null"/> if it is gone.</summary>
    /// <remarks>
    /// Read for two facts a control address depends on and neither of which Servyx may assume: the namespace's
    /// <em>name</em>, which is the DNS suffix the service's name is completed by, and its <em>type</em>, which
    /// decides whether there are DNS records at all. Both come from the provider so that the address Servyx
    /// reports is the address AWS actually publishes rather than one this adapter composed from configuration.
    /// </remarks>
    internal async Task<CloudMapNamespace?> GetNamespaceAsync(string namespaceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceId);

        try
        {
            var response = await SendAsync(
                    "GetNamespace",
                    new JsonObject { ["Id"] = namespaceId },
                    "read a Cloud Map namespace",
                    ct)
                .ConfigureAwait(false);

            return CloudMapNamespace.From(response?["Namespace"] as JsonObject);
        }
        catch (AwsApiException e) when (
            string.Equals(e.ErrorCode, ServiceDiscoveryErrorCodes.NamespaceNotFound, StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Reads a Cloud Map resource's tags, so a delete can prove the resource is Servyx's before making it.</summary>
    /// <remarks>
    /// Cloud Map has no <c>include: ["TAGS"]</c> on <c>GetService</c> as ECS has on <c>DescribeServices</c>, so
    /// this is a separate round trip rather than a flag. It is made anyway, on the destroy path only, for the
    /// reason every adapter here gives: a delete list acted on with a false positive destroys somebody else's
    /// infrastructure.
    /// </remarks>
    internal async Task<IReadOnlyDictionary<string, string>> ListTagsAsync(string resourceArn, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceArn);

        try
        {
            var response = await SendAsync(
                    "ListTagsForResource",
                    new JsonObject { ["ResourceARN"] = resourceArn },
                    "read a Cloud Map resource's tags",
                    ct)
                .ConfigureAwait(false);

            return ServiceDiscoveryJson.Tags(response?["Tags"] as JsonArray);
        }
        catch (AwsApiException e) when (
            string.Equals(e.ErrorCode, ServiceDiscoveryErrorCodes.ServiceNotFound, StringComparison.Ordinal))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Deletes a Cloud Map service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There is no force flag and nothing here invents one.</strong> Cloud Map refuses to delete a
    /// service that still has registered instances, and the correct response to that refusal is to wait for ECS
    /// to finish deregistering rather than to tear the registration out from under a draining task. The refusal
    /// therefore surfaces as an <see cref="AwsApiException"/> carrying
    /// <see cref="ServiceDiscoveryErrorCodes.ResourceInUse"/> and the caller decides.
    /// </para>
    /// <para>
    /// A service Cloud Map never knew is <see langword="false"/> rather than an exception: "already gone" is the
    /// answer a destroy wanted.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> if Cloud Map deleted it; <see langword="false"/> if it was already gone.</returns>
    /// <exception cref="AwsApiException">Cloud Map refused the delete — most importantly because instances are still registered.</exception>
    internal async Task<bool> DeleteServiceAsync(string service, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        try
        {
            await SendAsync(
                    "DeleteService",
                    new JsonObject { ["Id"] = service },
                    "delete a Cloud Map service",
                    ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (AwsApiException e) when (
            string.Equals(e.ErrorCode, ServiceDiscoveryErrorCodes.ServiceNotFound, StringComparison.Ordinal))
        {
            return false;
        }
    }

    private async Task<JsonObject?> SendAsync(string action, JsonObject body, string attempted, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, ServiceDiscoveryProtocol.ContentType),
        };

        request.Headers.TryAddWithoutValidation("X-Amz-Target", ServiceDiscoveryProtocol.TargetPrefix + action);

        await _signer.SignAsync(request, ct).ConfigureAwait(false);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

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
                $"AWS Cloud Map's response to the attempt to {attempted} was not well-formed JSON.",
                e);
        }
    }

    /// <summary>
    /// Turns a non-success response into an <see cref="AwsApiException"/> carrying the status and Cloud Map's own
    /// error type and message — and nothing from the request.
    /// </summary>
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
                message = LightsailJson.Text(error, "Message") ?? LightsailJson.Text(error, "message");
            }
            catch (JsonException)
            {
                // A non-JSON error body is reported by status alone rather than masking the failure it describes.
            }
        }

        return new AwsApiException(
            status,
            code,
            string.Create(
                CultureInfo.InvariantCulture,
                $"AWS Cloud Map refused the attempt to {attempted}: HTTP {(int)status}. {code} {message}").Trim());
    }
}
