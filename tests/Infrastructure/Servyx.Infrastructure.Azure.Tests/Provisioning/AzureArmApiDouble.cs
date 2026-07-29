using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>One request the adapter attempted, captured before any socket could exist.</summary>
/// <param name="Method">The HTTP verb.</param>
/// <param name="Uri">The absolute request URI, including query string.</param>
/// <param name="Authorization">The <c>Authorization</c> header as sent, or <see langword="null"/> if none was set.</param>
/// <param name="Body">The request body as text, or <see langword="null"/> for a bodiless request.</param>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
{
    /// <summary>Whether this request went to the Entra ID token service rather than to ARM.</summary>
    internal bool IsTokenExchange =>
        string.Equals(Uri.Host, "login.microsoftonline.com", StringComparison.Ordinal);

    /// <summary>Whether this request went to Azure Resource Manager.</summary>
    internal bool IsArm => string.Equals(Uri.Host, "management.azure.com", StringComparison.Ordinal);
}

/// <summary>
/// A substituted Azure: an <see cref="HttpMessageHandler"/> that records every request — to <em>both</em> the
/// token service and ARM — and answers from a caller-supplied routing function.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps the whole suite offline. Nothing here opens a socket, resolves a hostname, or
/// reads an environment variable, so the tests run identically on a bare CI runner with no internet, with no
/// Azure subscription, and with no service principal — and a test that expects <em>no</em> request can prove it
/// by asserting <see cref="Requests"/> is empty, which is a stronger claim than "the call failed".
/// </para>
/// <para>
/// It is the direct counterpart of <c>DigitalOceanApiDouble</c>, with one structural addition that is itself a
/// finding: the DigitalOcean double intercepts one host, this one intercepts two, because Azure's credential
/// has to be exchanged at a different service before ARM will speak to it at all. <see cref="TokenExchanges"/>
/// and <see cref="ArmRequests"/> exist so a test can assert on that split directly.
/// </para>
/// </remarks>
internal sealed class AzureArmApiDouble : HttpMessageHandler
{
    /// <summary>Every request the adapter made, in order, across both hosts.</summary>
    internal List<RecordedRequest> Requests { get; } = [];

    /// <summary>Only the requests that went to the Entra ID token endpoint.</summary>
    internal IReadOnlyList<RecordedRequest> TokenExchanges => Requests.Where(r => r.IsTokenExchange).ToList();

    /// <summary>Only the requests that went to Azure Resource Manager.</summary>
    internal IReadOnlyList<RecordedRequest> ArmRequests => Requests.Where(r => r.IsArm).ToList();

    /// <summary>
    /// How each request is answered. Defaults to failing the test loudly: a route a test did not set up is a
    /// call the adapter was not supposed to make.
    /// </summary>
    internal Func<RecordedRequest, HttpResponseMessage> Responder { get; set; } =
        request => throw new InvalidOperationException(
            $"The adapter made an unexpected {request.Method} request to '{request.Uri}'.");

    /// <summary>An <see cref="HttpClient"/> wired to this double.</summary>
    internal HttpClient Client() => new(this, disposeHandler: false);

    /// <summary>Builds a JSON response.</summary>
    internal static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };

    /// <summary>Builds an empty response, e.g. the 204 an ARM delete answers with.</summary>
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
        RejectImageBearingUpdate(recorded);
        return Responder(recorded);
    }

    /// <summary>
    /// The suite-wide guarantee that <strong>no update</strong> this assembly's adapter ever issues names an
    /// image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enforced here, in the one place every request in the suite passes through, rather than as an assertion
    /// each individual test remembers to make — a per-test assertion only covers the requests that test happens
    /// to trigger, and the claim being made is about all of them. Any <c>PATCH</c> body carrying an
    /// <c>imageReference</c> or a <c>storageProfile</c> member, at any depth, fails the test where it was
    /// issued.
    /// </para>
    /// <para>
    /// <strong>Why the check is scoped to PATCH, and why that is the honest scope rather than a weakened
    /// one.</strong> Azure differs from DigitalOcean here in a way worth stating: <c>disk: true</c> is a value
    /// DigitalOcean's adapter never has a legitimate reason to send, so its double can reject it on every
    /// request. An image reference is different — the <em>create</em> sequence must name the image the machine
    /// boots from, and it does so with a <c>PUT</c>. PATCH is ARM's merge verb and the only verb this adapter's
    /// update path uses, so "no PATCH names an image" is exactly the claim being made: an update that named the
    /// image would be a replacement wearing an update's clothes, since ARM cannot reimage a machine in place
    /// and a PATCH that appeared to ask for it would either be ignored or rejected — never honestly applied.
    /// The production code makes such a body unrepresentable (see <c>ArmVirtualMachineResizeRequest</c>, which
    /// has no member that could carry either); this is the independent check that the wire agrees, since a
    /// serializer setting is not a compiler guarantee.
    /// </para>
    /// <para>
    /// Only <em>request</em> bodies are inspected. A virtual machine <em>response</em> reports its image
    /// reference on every read, and never trips this.
    /// </para>
    /// </remarks>
    private static void RejectImageBearingUpdate(RecordedRequest recorded)
    {
        if (recorded.Method != HttpMethod.Patch
            || recorded.Body is not { Length: > 0 } body
            || !body.TrimStart().StartsWith('{'))
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
            // Not JSON at all, so it cannot carry a JSON member.
            return;
        }

        string? offender;
        using (document)
        {
            offender = ForbiddenUpdateMember(document.RootElement);
            if (offender is null)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"The adapter sent an update to '{recorded.Uri}' whose body carries a \"{offender}\" member. ARM has "
            + "no operation that changes an existing machine's image in place, so an update naming one is a "
            + "replacement - which deletes the machine's managed OS disk, and everything on it - rather than the "
            + $"resize it is dressed as. Servyx never issues it. Body: {body}");
    }

    /// <summary>
    /// The name of the first <c>imageReference</c> or <c>storageProfile</c> member found at any depth, or
    /// <see langword="null"/> if the body carries neither.
    /// </summary>
    /// <remarks>
    /// <c>storageProfile</c> is rejected as well as <c>imageReference</c> because it is the member an image
    /// reference would have to arrive inside: catching only the inner name would let a body that names the OS
    /// disk — the resource whose survival is the whole <c>Preserved</c> claim — through unremarked.
    /// </remarks>
    private static string? ForbiddenUpdateMember(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ForbiddenUpdateMemberNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        return property.Name;
                    }

                    var nested = ForbiddenUpdateMember(property.Value);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ForbiddenUpdateMember(item);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>The member names an update body may never carry.</summary>
    private static readonly string[] ForbiddenUpdateMemberNames = ["imageReference", "storageProfile"];
}

/// <summary>
/// The smallest honest <see cref="ISecretStore"/>: an in-memory map that hands out a real
/// <see cref="SecretLease"/> and records every URN resolution in order.
/// </summary>
/// <remarks>
/// Counting resolutions is not incidental — it is how the suite proves that the plan path resolves the client
/// secret zero times, and how it proves that a create sequence of six ARM calls resolves it exactly once
/// rather than once per call. That second assertion is the visible form of the adapter's one deliberate
/// divergence from the DigitalOcean adapter's "resolve on every request" discipline.
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
