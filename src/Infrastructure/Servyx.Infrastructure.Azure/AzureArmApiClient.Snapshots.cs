using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Servyx.Infrastructure.Azure;

/// <summary>
/// The ARM verbs a managed-disk snapshot backup is made of: read a machine's disks, write a snapshot, list the
/// snapshots in a resource group, read one back, and delete one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate file, and nothing in the other three halves of this class changed but the fact that it
/// was already <c>partial</c>.</strong> The OAuth2 client-credentials exchange, the bearer-stamping
/// <c>SendAsync</c>, the error translation that can never quote a request header, and — the point of this file
/// — <see cref="PollOperationAsync"/> are all reused exactly as they are. There is still one long-running
/// operation poller in this assembly.
/// </para>
/// <para>
/// <strong>Snapshots and disks version separately from virtual machines, and getting that wrong is a 400 rather
/// than a silent wrong answer.</strong> <see cref="ApiVersionFor"/> maps every <c>Microsoft.Compute</c>
/// resource onto <see cref="ComputeApiVersion"/>, which is a <em>virtualMachines</em> api-version; the disk
/// resource provider that owns <c>Microsoft.Compute/snapshots</c> publishes its own series, and
/// <c>incremental</c> and <c>completionPercent</c> only exist there. Rather than change a mapping the
/// provisioning path depends on, every request below names <see cref="DisksApiVersion"/> explicitly. That is
/// also why the delete and the read here are not <see cref="DeleteResourceAsync"/> and
/// <see cref="GetResourceAsync{T}"/>.
/// </para>
/// <para>
/// <strong>A snapshot write is a submission, never an outcome.</strong>
/// <see cref="CreateSnapshotAsync"/> hands back the same <see cref="ArmOperationSubmission"/> receipt a resize
/// or a replace does, and when ARM names no tracking operation this method <em>synthesises</em> one pointing at
/// the snapshot resource — so the shared poller always makes at least one observation after the write. There is
/// no path through this file on which a snapshot is reported to exist because ARM accepted the request to
/// create it.
/// </para>
/// </remarks>
internal sealed partial class AzureArmApiClient
{
    /// <summary>
    /// The api-version used for every <c>Microsoft.Compute/snapshots</c> and <c>Microsoft.Compute/disks</c> call.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ComputeApiVersion"/>. The disk resource provider versions independently of
    /// the compute one, and <c>properties.incremental</c> and <c>properties.completionPercent</c> — the two
    /// members that decide what a Servyx snapshot costs and whether it is finished — are members of this series.
    /// </remarks>
    internal const string DisksApiVersion = "2024-03-02";

    /// <summary>The ARM resource type a managed-disk snapshot is.</summary>
    internal const string SnapshotResourceType = "Microsoft.Compute/snapshots";

    /// <summary>Builds the ARM resource id of a snapshot in a resource group.</summary>
    internal string SnapshotResourceId(string resourceGroup, string name) =>
        ResourceId(resourceGroup, "Microsoft.Compute", "snapshots", name);

    /// <summary>
    /// Reads a virtual machine's disk attachments, or <see langword="null"/> if ARM no longer has the machine.
    /// </summary>
    /// <remarks>
    /// The same GET the provisioning path makes, deserialised into <see cref="ArmVirtualMachineDisks"/> instead
    /// of <see cref="ArmVirtualMachine"/> — see that type's remarks for why a second read model rather than a
    /// wider shared one.
    /// </remarks>
    internal Task<ArmVirtualMachineDisks?> GetVirtualMachineDisksAsync(string vmResourceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmResourceId);
        return GetResourceAsync<ArmVirtualMachineDisks>(vmResourceId, ct);
    }

    /// <summary>
    /// Lists every snapshot in one resource group, following ARM's pagination to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The whole group, not only Servyx's own.</strong> A listing filtered on Servyx's tags would be
    /// cheaper and would let the backup adapter report <c>SkippedForeign: 0</c> for a group full of hand-taken
    /// snapshots — technically true and substantively a lie, exactly as the EBS adapter says of the same
    /// shortcut. Classification happens after the read, never in the filter.
    /// </para>
    /// <para>
    /// Scoped to a resource group rather than to the whole subscription because that is where a Servyx snapshot
    /// is written — the same group as the machine it backs — and a subscription-wide listing would drag in
    /// every other tenant of the subscription for no gain.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<ArmSnapshot>> ListSnapshotsAsync(string resourceGroup, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);

        var next = new Uri(
            _armBaseAddress,
            string.Create(
                CultureInfo.InvariantCulture,
                $"/subscriptions/{_subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute"
                + $"/snapshots?api-version={DisksApiVersion}"));

        var snapshots = new List<ArmSnapshot>();

        for (var page = 0; page < MaxSweepPages && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The resource group itself is gone. Snapshots that were in it are gone with it; an empty
                // listing is the honest answer and is not an error on a read path.
                return snapshots;
            }

            await EnsureSuccessAsync(response, $"list the snapshots in '{resourceGroup}'", ct).ConfigureAwait(false);

            var envelope = Deserialize<ArmSnapshotListEnvelope>(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            snapshots.AddRange(envelope?.Value ?? []);

            next = string.IsNullOrWhiteSpace(envelope?.NextLink)
                ? null
                : new Uri(envelope.NextLink, UriKind.Absolute);
        }

        return snapshots;
    }

    /// <summary>Reads one snapshot by ARM id, or <see langword="null"/> if ARM no longer has it.</summary>
    /// <remarks>
    /// A snapshot can vanish between two Servyx calls — deleted in the portal, by another tool, or by an Azure
    /// Backup policy — so <see langword="null"/> is a real answer and not a defensive branch.
    /// </remarks>
    internal async Task<ArmSnapshot?> GetSnapshotAsync(string snapshotResourceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotResourceId);

        using var request = new HttpRequestMessage(HttpMethod.Get, Absolute(snapshotResourceId, DisksApiVersion));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"read '{snapshotResourceId}'", ct).ConfigureAwait(false);

        return Deserialize<ArmSnapshot>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Submits the creation of one snapshot and returns the receipt ARM answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The returned value is a receipt, not an outcome.</strong> An ARM snapshot PUT answers
    /// <c>202 Accepted</c>, or <c>200</c>/<c>201</c> with <c>provisioningState: "Updating"</c>. None of those
    /// is a snapshot that exists and contains data.
    /// </para>
    /// <para>
    /// <strong>A tracker is synthesised when ARM names none</strong>, pointing at the snapshot resource itself
    /// at <see cref="DisksApiVersion"/>. Without it, <see cref="PollOperationAsync"/> would fall back to
    /// <see cref="ApiVersionFor"/> — the virtual-machine api-version, which the disk provider rejects — and, on
    /// a submission ARM already described as <c>Succeeded</c>, would return without reading anything at all.
    /// Forcing at least one post-submission read is the cheap half of "submission is not success"; the
    /// expensive half is the copy-progress check the backup provider makes afterwards.
    /// </para>
    /// </remarks>
    /// <param name="snapshotResourceId">The ARM id to create the snapshot at.</param>
    /// <param name="body">The typed snapshot write. No other resource shape can be sent here.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AzureApiException">ARM refused the request outright. Nothing was created.</exception>
    internal async Task<ArmOperationSubmission> CreateSnapshotAsync(
        string snapshotResourceId,
        ArmSnapshotRequest body,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotResourceId);
        ArgumentNullException.ThrowIfNull(body);

        using var request = new HttpRequestMessage(HttpMethod.Put, Absolute(snapshotResourceId, DisksApiVersion))
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"create the snapshot '{snapshotResourceId}'", ct).ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new ArmOperationSubmission(
            snapshotResourceId,
            TrackerUriOf(response) ?? Absolute(snapshotResourceId, DisksApiVersion),
            Deserialize<ArmOperationStatus>(payload)?.Properties?.ProvisioningState);
    }

    /// <summary>
    /// Destroys one snapshot and waits until ARM stops reporting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modelled on <see cref="DeleteResourceAsync"/> — an accepted delete is polled with GETs until the
    /// resource 404s, because <c>202 Accepted</c> is ARM saying it took the request, not that the resource is
    /// gone and has stopped billing.
    /// </para>
    /// <para>
    /// <strong>A snapshot ARM already does not have is not an error.</strong> Something outside Servyx may have
    /// deleted it since the listing that named it; the outcome retention asked for has happened, and reporting
    /// a failure would leave the caller expecting a charge that has already stopped.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> if this call destroyed it; <see langword="false"/> if it was already gone.</returns>
    internal async Task<bool> DeleteSnapshotAsync(string snapshotResourceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotResourceId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Absolute(snapshotResourceId, DisksApiVersion));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"destroy the snapshot '{snapshotResourceId}'", ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return true;
        }

        for (var attempt = 0; attempt < _pollAttempts; attempt++)
        {
            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);

            using var poll = new HttpRequestMessage(HttpMethod.Get, Absolute(snapshotResourceId, DisksApiVersion));
            using var pollResponse = await SendAsync(poll, ct).ConfigureAwait(false);

            if (pollResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }
        }

        throw new AzureApiException(
            HttpStatusCode.Accepted,
            $"Azure accepted the deletion of snapshot '{snapshotResourceId}' but it was still present after "
            + $"{_pollAttempts} poll(s). It may still be billing per GB-month. Reconcile before assuming the "
            + "prune finished.");
    }
}
