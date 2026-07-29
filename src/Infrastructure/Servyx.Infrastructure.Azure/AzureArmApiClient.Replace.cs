using System.Net;
using System.Net.Http.Json;

namespace Servyx.Infrastructure.Azure;

/// <summary>
/// The two ARM verbs a virtual machine replacement is made of: the delete that removes the machine, and the
/// create that puts another one at the same ARM id.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate file, and nothing in the other two halves of this class changed.</strong> The token
/// exchange, the bearer-stamping <c>SendAsync</c>, the error translation, and — the point of this file —
/// <see cref="PollOperationAsync"/> are reused exactly as they are. Neither verb below carries a poll loop of
/// its own: both hand back the same <see cref="ArmOperationSubmission"/> receipt the resize submits, and the
/// same watcher takes each of them to a terminal state. There is one long-running-operation poller in this
/// assembly and there will not be two.
/// </para>
/// <para>
/// <strong>PUT, and here that is the honest verb rather than the dangerous one.</strong> The resize deliberately
/// uses PATCH so that the request <em>cannot</em> name an image; this is the mirror case, and it is a
/// full-resource write because a machine that does not exist has nothing to merge into. That is exactly why
/// these verbs live behind <see cref="Provisioning.AzureVirtualMachineProvisioner"/>'s destructive entry point
/// and not behind its update one — the two request shapes are different types, taken by different methods, and
/// no argument turns one into the other.
/// </para>
/// <para>
/// <strong>Neither verb touches the network interface, the public IP address, or the virtual network.</strong>
/// A VM delete removes the VM; the NIC survives it, because this adapter deliberately declares no
/// <c>deleteOption</c> on the VM's NIC reference (see <c>ArmNetworkInterfaceReference</c>). So the address the
/// host is reachable at is a resource neither of these calls names.
/// </para>
/// </remarks>
internal sealed partial class AzureArmApiClient
{
    /// <summary>
    /// Submits the deletion of one virtual machine and returns the receipt ARM answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The returned value is a receipt, not an outcome.</strong> ARM answers a compute delete with
    /// <c>202 Accepted</c> and finishes the work asynchronously, so a successful return from this method says
    /// only that ARM took the request. <see cref="PollOperationAsync"/> is the only thing that can say the
    /// machine is gone.
    /// </para>
    /// <para>
    /// <strong>Why this is not <see cref="DeleteResourceAsync"/>.</strong> That method answers the question a
    /// teardown asks — "is it gone yet?" — by re-reading the resource until it 404s, and it throws when the
    /// polls are spent. A replacement needs the three-way answer instead: an ARM operation that <em>failed</em>
    /// and an ARM operation still <em>running</em> demand opposite responses from an operator, and an exception
    /// collapses them into one. So the tracking header is carried out to the caller and the shared poller
    /// classifies it, exactly as it does for a resize.
    /// </para>
    /// </remarks>
    /// <param name="resourceId">The ARM id of the virtual machine to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The receipt, or <see langword="null"/> if ARM answered <c>404</c> — i.e. it has no such machine, so
    /// nothing was deleted by this call and nothing can have been.
    /// </returns>
    /// <exception cref="AzureApiException">ARM refused the request outright. Nothing was deleted.</exception>
    internal async Task<ArmOperationSubmission?> DeleteVirtualMachineAsync(string resourceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Absolute(resourceId, ApiVersionFor(resourceId)));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"delete '{resourceId}'", ct).ConfigureAwait(false);

        // A 200 or a 204 is ARM saying the delete is already over. That is an observation, not an assumption,
        // so it is reported as a terminal state with no tracker for the poller to follow. Only a 202 - the
        // answer that means "accepted, still working" - hands back somewhere to watch.
        return response.StatusCode == HttpStatusCode.Accepted
            ? new ArmOperationSubmission(resourceId, TrackerUriOf(response), ProvisioningState: null)
            : new ArmOperationSubmission(resourceId, TrackerUri: null, OperationSucceeded);
    }

    /// <summary>
    /// Submits the creation of a replacement virtual machine and returns the receipt ARM answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The returned value is a receipt, not an outcome</strong>, for the same reason the delete above
    /// is. An ARM PUT answers <c>201 Created</c> with <c>provisioningState: "Creating"</c>: the write is
    /// accepted, not finished. When ARM names no tracking operation, <see cref="PollOperationAsync"/> falls
    /// back to re-reading the resource, which reaches the same evidence by a second route.
    /// </para>
    /// <para>
    /// <strong>Why this is not <see cref="PutResourceAsync{T}"/>.</strong> That method throws when the wait is
    /// spent, which is right for a create sequence that will be compensated — but on a replace the machine has
    /// <em>already been deleted</em> by the time this runs, so "still creating" and "the create failed" are the
    /// difference between waiting and intervening, and an exception would flatten them.
    /// </para>
    /// </remarks>
    /// <param name="resourceId">The ARM id to create the machine at — the same id the deleted machine had.</param>
    /// <param name="body">The full-resource write. Typed, so no other resource shape can be sent here.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AzureApiException">ARM refused the request outright. Nothing was created.</exception>
    internal async Task<ArmOperationSubmission> CreateVirtualMachineAsync(
        string resourceId,
        ArmVirtualMachineRequest body,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(body);

        using var request = new HttpRequestMessage(HttpMethod.Put, Absolute(resourceId, ApiVersionFor(resourceId)))
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"create the replacement for '{resourceId}'", ct).ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new ArmOperationSubmission(
            resourceId,
            TrackerUriOf(response),
            Deserialize<ArmOperationStatus>(payload)?.Properties?.ProvisioningState);
    }
}
