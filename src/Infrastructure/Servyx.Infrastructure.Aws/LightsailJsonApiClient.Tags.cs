using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

// The one mutating Lightsail action that does not touch the machine, kept in its own file so the provisioning
// client in LightsailJsonApiClient.cs stays exactly the four actions it was. (A plain comment rather than an
// XML one: the type's documentation lives on the first part, and a partial type carries its doc comment once.)
//
// WHY THIS IS THE WHOLE OF LIGHTSAIL'S IN-PLACE UPDATE. Every sibling adapter's in-place update changes the
// machine - a droplet resize, an Azure vmSize write, an EC2 ModifyInstanceAttribute. Lightsail has none of
// those for an instance: AWS publishes no operation that changes an existing instance's bundle, and the
// blueprint is fixed by CreateInstances. TagResource is therefore not one of several in-place operations, it is
// the only one, and this file is the entirety of the backing behind the adapter's UpdateInPlace capability bit.
//
// THERE IS NO UntagResource METHOD, AND THAT IS THE STRUCTURAL HALF OF THE OWNERSHIP GUARANTEE. Lightsail's
// UntagResource is the only action that can REMOVE a tag from an instance. It is not implemented here and is
// not reachable from anywhere in this assembly, so no plan, no argument and no caller can cause a Servyx
// ownership tag to be deleted from a live instance - the orphan sweep in AwsLightsailProvisioner.ReconcileAsync
// finds billing instances by exactly those tags, and an instance that lost one becomes undiscoverable while it
// keeps costing money. TagResource can only add or overwrite; overwriting is closed off separately, in
// AwsLightsailProvisioner.Tags.cs, by building the request through ServyxTagKeys.Build so the canonical keys
// are written last from the LIVE instance's own identity.
//
// SUBMISSION IS NOT SUCCESS. TagResource answers with an `operations` array of Lightsail Operation records
// carrying a `status` and an `isTerminal` flag, not with the retagged instance - the same asynchronous shape
// CreateInstances has. So this method hands the operations back rather than swallowing them, and the caller
// both inspects them for an outright failure and then polls GetInstance until the tags are observed on the
// live instance. Reading the effect back is a stronger confirmation than an operation status, and it is what
// lets the completed message state that the ownership tags survived as an observation rather than a promise.
internal sealed partial class LightsailJsonApiClient
{
    /// <summary>The Lightsail action name this file issues, and the only mutating one outside the create path.</summary>
    internal const string TagResourceAction = "TagResource";

    /// <summary>
    /// Adds or overwrites tags on a resource that already exists. Never removes one — see the file header.
    /// </summary>
    /// <param name="body">The request body, built by <c>AwsLightsailRequests.TagResource</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The pending <c>Operation</c> records Lightsail answers with. Deliberately returned rather than discarded:
    /// a caller that ignored them would be treating an accepted submission as a finished change.
    /// </returns>
    internal async Task<IReadOnlyList<LightsailOperation>> TagResourceAsync(JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var response = await SendAsync(TagResourceAction, body, "change an instance's tags", ct)
            .ConfigureAwait(false);

        return LightsailOperation.AllFrom(response?["operations"] as JsonArray);
    }
}
