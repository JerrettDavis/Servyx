using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>One request the adapter attempted, captured before any socket could exist.</summary>
/// <param name="Method">The HTTP verb.</param>
/// <param name="Uri">The absolute request URI, including query string.</param>
/// <param name="Authorization">The <c>Authorization</c> header as sent, or <see langword="null"/> if none was set.</param>
/// <param name="Body">The request body as text, or <see langword="null"/> for a bodiless request.</param>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>
/// A substituted DigitalOcean API: an <see cref="HttpMessageHandler"/> that records every request and answers
/// from a caller-supplied routing function.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps the whole suite offline. Nothing here opens a socket, resolves a hostname, or
/// reads an environment variable, so the tests run identically on a bare CI runner with no internet — and a
/// test that expects <em>no</em> request can prove it by asserting <see cref="Requests"/> is empty, which is a
/// stronger claim than "the call failed".
/// </para>
/// <para>
/// It is the direct counterpart of the Docker suite's substituted <c>IDockerClient</c> and the SSH suite's
/// substituted <c>ITransport</c>: the adapter's only outbound seam, replaced at its narrowest point, with no
/// production code path stubbed out that a real run would otherwise exercise.
/// </para>
/// </remarks>
internal sealed class DigitalOceanApiDouble : HttpMessageHandler
{
    /// <summary>Every request the adapter made, in order.</summary>
    internal List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// How each request is answered. Defaults to failing the test loudly: a route a test did not set up is a
    /// call the adapter was not supposed to make.
    /// </summary>
    internal Func<RecordedRequest, HttpResponseMessage> Responder { get; set; } =
        request => throw new InvalidOperationException(
            $"The adapter made an unexpected {request.Method} request to '{request.Uri}'.");

    /// <summary>An <see cref="HttpClient"/> wired to this double, with the real default base address.</summary>
    internal HttpClient Client() => new(this, disposeHandler: false);

    /// <summary>Builds a JSON response.</summary>
    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };

    /// <summary>Builds an empty response, e.g. the 204 a droplet delete answers with.</summary>
    internal static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? new Uri("about:blank"),
            request.Headers.Authorization?.ToString(),
            body);

        Requests.Add(recorded);
        RejectDiskInclusiveRequest(recorded);
        return Responder(recorded);
    }

    /// <summary>
    /// The suite-wide guarantee that <strong>no</strong> request this assembly's adapter ever builds asks
    /// DigitalOcean to grow a droplet's boot disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enforced here, in the one place every request in the suite passes through, rather than as an
    /// assertion each individual test remembers to make — a per-test assertion only covers the requests that
    /// test happens to trigger, and the claim being made is about all of them. Any request body that sets a
    /// <c>disk</c> member to <see langword="true"/>, at any depth, fails the test where it was issued.
    /// </para>
    /// <para>
    /// <c>disk: true</c> is DigitalOcean's irreversible resize: it grows the boot disk permanently and
    /// permanently prevents the droplet from ever being resized down again. The production code makes it
    /// unrepresentable (see <c>ResizeDropletActionRequest.Disk</c>, a property with no setter); this is the
    /// independent check that the wire agrees, since a serializer setting is not a compiler guarantee.
    /// </para>
    /// <para>
    /// Only <em>request</em> bodies are inspected. A droplet <em>response</em> reports its disk size as a
    /// number, which is not a boolean and never trips this.
    /// </para>
    /// </remarks>
    private static void RejectDiskInclusiveRequest(RecordedRequest recorded)
    {
        if (recorded.Body is not { Length: > 0 } body || !body.TrimStart().StartsWith('{'))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Not JSON at all, so it cannot be a JSON member set to true.
            return;
        }

        using (document)
        {
            if (!SetsDiskTrue(document.RootElement))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"The adapter sent a request to '{recorded.Uri}' whose body sets \"disk\": true. That is "
            + "DigitalOcean's irreversible disk-growing resize, which Servyx never issues: it cannot be undone "
            + $"and permanently prevents the droplet from being resized down again. Body: {body}");
    }

    /// <summary>Whether <paramref name="element"/> contains a <c>disk</c> member set to <c>true</c>, at any depth.</summary>
    private static bool SetsDiskTrue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "disk", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }

                    if (SetsDiskTrue(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (SetsDiskTrue(item))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }
}

/// <summary>
/// The smallest honest <see cref="ISecretStore"/>: an in-memory map that hands out a real
/// <see cref="SecretLease"/> and counts how often each URN was resolved.
/// </summary>
/// <remarks>
/// Counting resolutions is not incidental — it is how the suite proves the adapter resolves the token per
/// request rather than caching it, and how the plan-is-pure test proves the token was never resolved at all.
/// </remarks>
internal sealed class RecordingSecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    /// <summary>How many times each URN was resolved, in order of first resolution.</summary>
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
