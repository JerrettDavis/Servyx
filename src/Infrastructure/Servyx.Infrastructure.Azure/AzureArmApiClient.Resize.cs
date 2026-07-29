using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Azure;

/// <summary>
/// The one mutating ARM verb this client offers for a resource that already exists: a resize.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate file, and nothing in <c>AzureArmApiClient.cs</c> changed but the word
/// <c>partial</c>.</strong> The token exchange, the bearer-stamping <c>SendAsync</c>, the error translation and
/// the create/read/delete verbs are reused exactly as they were: this file adds members and rewrites none. That
/// keeps the secret discipline argued for at length on the other half of the class a property of this half too,
/// without this change having to re-argue it.
/// </para>
/// <para>
/// <strong>PATCH, not PUT, and the choice is a safety property rather than a preference.</strong> ARM's write
/// verb for <c>Microsoft.Compute/virtualMachines</c> is a full-resource PUT: the body ARM stores is the body
/// sent, so a PUT that carried only a hardware profile would be asking ARM to store a machine with no storage
/// profile and no OS profile. Reaching a size change through PUT would therefore mean <em>reading the whole
/// machine and sending it back with one field edited</em> — which puts the image reference into the request
/// body on the resize path, and makes "this request cannot become a replace" a property of how carefully the
/// round-trip was assembled. PATCH (the <c>VirtualMachineUpdate</c> body, supported by
/// <see cref="ComputeApiVersion"/>) merges: fields the body does not mention are left exactly as they are. So
/// the resize body can name <em>only</em> <c>properties.hardwareProfile.vmSize</c>, and
/// <see cref="ArmVirtualMachineResizeRequest"/> is shaped so that it can name nothing else.
/// </para>
/// <para>
/// <strong>Submission is not success.</strong> ARM answers a resize with <c>200</c> and a provisioning state of
/// <c>Updating</c>, or with <c>202 Accepted</c> and an <c>Azure-AsyncOperation</c> (or <c>Location</c>) header
/// naming a long-running operation. Neither is a finished resize.
/// <see cref="ResizeVirtualMachineAsync"/> therefore returns a <em>receipt</em>, and only
/// <see cref="PollOperationAsync"/> can produce <see cref="ArmOperationOutcome.Succeeded"/> — from an observed
/// terminal state and from nothing else.
/// </para>
/// </remarks>
internal sealed partial class AzureArmApiClient
{
    /// <summary>The ARM long-running-operation state that means the operation finished and worked.</summary>
    private const string OperationSucceeded = "Succeeded";

    /// <summary>The ARM states that mean the operation finished and did not work.</summary>
    /// <remarks>
    /// Both spellings of the cancelled state are listed. ARM's own documentation uses <c>Canceled</c> for the
    /// async-operation status, and a state this client failed to recognise would be classified as still running
    /// — which would turn a finished failure into a timeout, the one direction that costs an operator a retry
    /// they should not make.
    /// </remarks>
    private static readonly string[] OperationFailedStates = ["Failed", "Canceled", "Cancelled"];

    /// <summary>
    /// Submits a resize of one virtual machine and returns the receipt ARM answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The returned value is a receipt, not an outcome.</strong> Nothing may treat a successful return
    /// from this method as a completed resize; that is what <see cref="PollOperationAsync"/> is for.
    /// </para>
    /// <para>
    /// <strong>The body cannot describe an image.</strong> It is an
    /// <see cref="ArmVirtualMachineResizeRequest"/>, which has exactly one member, whose type has exactly one
    /// member, whose type has exactly one member — a string size. There is no <c>storageProfile</c> member and
    /// no <c>imageReference</c> member anywhere in that shape, so no argument to this method and no expression
    /// in this assembly can produce a resize body that names either. A replace is not reachable from here by
    /// supplying a different value, because there is no value to supply.
    /// </para>
    /// </remarks>
    /// <param name="resourceId">The ARM id of the virtual machine to resize.</param>
    /// <param name="vmSize">The size to write to <c>properties.hardwareProfile.vmSize</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AzureApiException">ARM refused the request outright. Nothing was changed.</exception>
    internal async Task<ArmOperationSubmission> ResizeVirtualMachineAsync(
        string resourceId,
        string vmSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmSize);

        var body = new ArmVirtualMachineResizeRequest
        {
            Properties = new ArmVirtualMachineResizeProperties
            {
                HardwareProfile = new ArmResizeHardwareProfile { VmSize = vmSize },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Patch, Absolute(resourceId, ApiVersionFor(resourceId)))
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"resize '{resourceId}'", ct).ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new ArmOperationSubmission(
            resourceId,
            TrackerUriOf(response),
            Deserialize<ArmOperationStatus>(payload)?.Properties?.ProvisioningState);
    }

    /// <summary>
    /// Watches a submitted operation until ARM reports it succeeded or failed, or until the polls are spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three outcomes, and the third is not a failure.</strong> An operation still running when the
    /// last attempt is spent yields <see cref="ArmOperationOutcome.StillRunning"/> — deliberately distinct from
    /// <see cref="ArmOperationOutcome.Failed"/>, because a failed resize is over and may be retried, whereas a
    /// running one is not over and "retrying" it submits the same mutation against a live machine a second
    /// time.
    /// </para>
    /// <para>
    /// <strong>Both of ARM's tracking protocols are followed by the same loop.</strong> An
    /// <c>Azure-AsyncOperation</c> URL answers <c>200</c> with a top-level <c>status</c>; a <c>Location</c> URL
    /// answers <c>202</c> while the work runs and then <c>200</c> with the resource, whose
    /// <c>properties.provisioningState</c> is read instead. A submission ARM gave no tracking header for is
    /// followed by re-reading the resource itself, which is the same evidence by a third route. In every case
    /// the answer comes from an observation made <em>after</em> the submission, never from the submission.
    /// </para>
    /// </remarks>
    /// <param name="submission">The receipt <see cref="ResizeVirtualMachineAsync"/> returned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AzureApiException">A status read failed. The operation's outcome is unknown.</exception>
    internal async Task<ArmOperationPoll> PollOperationAsync(ArmOperationSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // A submission ARM already described as terminal is not re-read: the observation has been made.
        var immediate = Classify(submission.ProvisioningState);
        if (submission.TrackerUri is null && immediate != ArmOperationOutcome.StillRunning)
        {
            return new ArmOperationPoll(immediate, submission.ProvisioningState, null, Polls: 0);
        }

        var target = submission.TrackerUri
            ?? Absolute(submission.ResourceId, ApiVersionFor(submission.ResourceId));

        var status = submission.ProvisioningState;
        string? message = null;

        for (var poll = 1; poll <= _pollAttempts; poll++)
        {
            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                // The Location protocol's "still working" answer, and the reason this loop cannot mistake an
                // accepted operation for a finished one.
                status = "InProgress";
                continue;
            }

            await EnsureSuccessAsync(
                response,
                $"read the status of the update to '{submission.ResourceId}'",
                ct).ConfigureAwait(false);

            var report = Deserialize<ArmOperationStatus>(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            status = NullIfBlank(report?.Status) ?? NullIfBlank(report?.Properties?.ProvisioningState);
            message = NullIfBlank(report?.Error?.Message) ?? NullIfBlank(report?.Properties?.Error?.Message);

            // A success answer carrying no state at all is the 200 that ends a Location poll: the operation is
            // over, and ARM is answering with the resource rather than with a status document.
            var outcome = status is null ? ArmOperationOutcome.Succeeded : Classify(status);
            if (outcome != ArmOperationOutcome.StillRunning)
            {
                return new ArmOperationPoll(outcome, status ?? OperationSucceeded, message, poll);
            }
        }

        return new ArmOperationPoll(ArmOperationOutcome.StillRunning, status, message, _pollAttempts);
    }

    /// <summary>Where ARM says the operation's progress may be read, or <see langword="null"/> if it said nowhere.</summary>
    /// <remarks>
    /// <c>Azure-AsyncOperation</c> is preferred over <c>Location</c> because it is the protocol that can report
    /// a <em>failure</em>: a Location poll distinguishes "still running" from "over", but an async-operation
    /// poll distinguishes "succeeded" from "failed" and carries ARM's error with it.
    /// </remarks>
    private static Uri? TrackerUriOf(HttpResponseMessage response) =>
        HeaderUri(response, "Azure-AsyncOperation") ?? HeaderUri(response, "Location");

    private static Uri? HeaderUri(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out var uri)
            ? uri
            : null;

    /// <summary>
    /// Which of the three outcomes an ARM state names.
    /// </summary>
    /// <remarks>
    /// An unrecognised state is <see cref="ArmOperationOutcome.StillRunning"/> rather than either terminal
    /// answer. That is the safe direction in both senses: it never reports an unconfirmed mutation as finished,
    /// and it never reports a machine that is fine as broken.
    /// </remarks>
    private static ArmOperationOutcome Classify(string? state) =>
        state is null ? ArmOperationOutcome.StillRunning
        : string.Equals(state, OperationSucceeded, StringComparison.OrdinalIgnoreCase) ? ArmOperationOutcome.Succeeded
        : OperationFailedStates.Contains(state, StringComparer.OrdinalIgnoreCase) ? ArmOperationOutcome.Failed
        : ArmOperationOutcome.StillRunning;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>What ARM answered a mutating request with, before anything is known about the outcome.</summary>
/// <param name="ResourceId">The ARM id of the resource the request was sent to.</param>
/// <param name="TrackerUri">Where ARM says progress may be read, or <see langword="null"/> if it named nowhere.</param>
/// <param name="ProvisioningState">The state carried in the response body, if it carried one.</param>
/// <remarks>
/// Named a submission rather than a result on purpose: there is no member on this type that means the operation
/// finished, because at the moment one of these exists nothing is known about whether it did.
/// </remarks>
internal sealed record ArmOperationSubmission(string ResourceId, Uri? TrackerUri, string? ProvisioningState);

/// <summary>How a watched ARM operation ended, or that it had not ended.</summary>
internal enum ArmOperationOutcome
{
    /// <summary>ARM was observed reporting the operation finished successfully.</summary>
    Succeeded,

    /// <summary>ARM was observed reporting the operation finished unsuccessfully. It is over.</summary>
    Failed,

    /// <summary>
    /// The polls were spent with ARM still reporting the operation in progress. It is <em>not</em> over, and
    /// nothing is known to have succeeded or failed.
    /// </summary>
    StillRunning,
}

/// <summary>The end of a watch on an ARM operation.</summary>
/// <param name="Outcome">Which of the three ends was reached.</param>
/// <param name="Status">The last state ARM reported, verbatim, or <see langword="null"/> if it reported none.</param>
/// <param name="Message">ARM's own error text, when the operation failed and ARM supplied any.</param>
/// <param name="Polls">How many status reads were made.</param>
internal sealed record ArmOperationPoll(ArmOperationOutcome Outcome, string? Status, string? Message, int Polls)
{
    /// <summary>The state ARM last reported, in a form safe to put in a message.</summary>
    internal string StatusText => Status ?? "(no status)";

    /// <summary>ARM's own words about the failure, or a plain statement that it supplied none.</summary>
    internal string FailureText => Message is { Length: > 0 } message
        ? string.Create(CultureInfo.InvariantCulture, $"Azure's message: {message}")
        : "Azure supplied no explanation with the operation.";
}

/// <summary>
/// The body sent to <c>PATCH</c> a virtual machine's size.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This shape is the guarantee that a resize cannot become a replace, and it is structural rather than
/// procedural.</strong> The only difference between the ARM request that changes a machine's CPU and memory
/// allocation and the request that would set it booting from a different image is which members the body
/// carries. This type carries one member; its property type carries one member; that type carries one string.
/// There is no <c>storageProfile</c> member and no <c>imageReference</c> member to assign anywhere in the
/// chain, so there is no expression in this assembly that produces a resize body naming either — in the same
/// way, and for the same reason, that <c>ResizeDropletActionRequest</c>'s <c>disk</c> member has no setter.
/// </para>
/// <para>
/// A <c>required</c> <c>init</c> member rather than a settable one, so a body cannot be constructed empty and
/// filled in later by code that has forgotten what it is for. And note what a PATCH body's <em>absence</em> of
/// a member means, which is the property this whole design rests on: ARM merges, so the members not named here
/// are left exactly as they are on the live machine. The image reference is not "sent unchanged" — it is not
/// sent.
/// </para>
/// </remarks>
internal sealed class ArmVirtualMachineResizeRequest
{
    /// <summary>The one property group this request writes.</summary>
    [JsonPropertyName("properties")]
    public required ArmVirtualMachineResizeProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> object of a resize PATCH. Carries the hardware profile and nothing else.</summary>
internal sealed class ArmVirtualMachineResizeProperties
{
    /// <summary>The hardware profile. There is deliberately no sibling member for a storage profile.</summary>
    [JsonPropertyName("hardwareProfile")]
    public required ArmResizeHardwareProfile HardwareProfile { get; init; }
}

/// <summary>The <c>hardwareProfile</c> object of a resize PATCH: one size, and nothing else.</summary>
internal sealed class ArmResizeHardwareProfile
{
    /// <summary>The size to move the machine to.</summary>
    [JsonPropertyName("vmSize")]
    public required string VmSize { get; init; }
}

/// <summary>
/// What ARM reports when asked about an operation, in a shape that reads both of its protocols.
/// </summary>
/// <remarks>
/// An <c>Azure-AsyncOperation</c> document carries a top-level <c>status</c> and, on failure, a top-level
/// <c>error</c>. A resource read carries <c>properties.provisioningState</c> instead. Both are modelled here so
/// one poll loop can consume either without having to know which URL it was handed.
/// </remarks>
internal sealed class ArmOperationStatus
{
    /// <summary>The async-operation status: <c>InProgress</c>, <c>Succeeded</c>, <c>Failed</c> or <c>Canceled</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>ARM's error, present when the operation failed.</summary>
    [JsonPropertyName("error")]
    public ArmOperationError? Error { get; init; }

    /// <summary>The resource's own properties, when the poll landed on a resource rather than on a status document.</summary>
    [JsonPropertyName("properties")]
    public ArmOperationStatusProperties? Properties { get; init; }
}

/// <summary>The <c>properties</c> of a resource read during a poll.</summary>
internal sealed class ArmOperationStatusProperties
{
    /// <summary>The resource's provisioning state.</summary>
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    /// <summary>The error some ARM providers nest under the resource's properties rather than at the top level.</summary>
    [JsonPropertyName("error")]
    public ArmOperationError? Error { get; init; }
}

/// <summary>ARM's own account of why an operation failed.</summary>
/// <remarks>
/// Carried through to the caller verbatim. An operator needs to read Azure's reason, not this adapter's
/// paraphrase of it.
/// </remarks>
internal sealed class ArmOperationError
{
    /// <summary>ARM's machine-readable error code.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>ARM's human-readable message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
