using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// Translates an <see cref="AwsLightsailInstanceSpec"/> into the JSON body <c>CreateInstances</c> expects.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a separate file.</strong> Kept apart from the provisioner for the same reason
/// <c>AwsEc2Requests</c> is: it is the exact place a silent mistake (a missing required field, tags built in a
/// nondeterministic order) turns into a resource that exists but is untagged or unreproducible, so it is worth
/// reviewing on its own.
/// </para>
/// <para>
/// <strong>One <c>tags</c> array, not two — the whole orphan story is one call simpler than EC2's.</strong>
/// <c>AwsEc2Requests</c> emits two <c>TagSpecification</c> blocks because <c>RunInstances</c> creates two kinds
/// of taggable object. Lightsail's bundle price already includes the boot disk, so there is exactly one object
/// to tag and exactly one array here.
/// </para>
/// <para>
/// <strong><c>userData</c> travels as plain text, not base64.</strong> Confirmed against AWS's published
/// <c>CreateInstances</c> request syntax, which types <c>userData</c> as an ordinary string with no encoding
/// note — unlike <c>AwsEc2Requests.RunInstances</c>, which must base64-encode <see cref="Servyx.Domain.Provisioning.MachineSpec.CloudInit"/>
/// at the wire boundary because EC2's <c>UserData</c> parameter requires it. Nothing here authors user-data
/// either way; a caller's bytes are forwarded exactly as given.
/// </para>
/// </remarks>
internal static class AwsLightsailRequests
{
    /// <summary>Builds the full <c>CreateInstances</c> request body for <paramref name="spec"/>.</summary>
    /// <param name="spec">The instance to create.</param>
    /// <param name="tags">The tag set to stamp on the instance, applied inline in this same request.</param>
    internal static JsonObject CreateInstances(AwsLightsailInstanceSpec spec, IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tags);

        var body = new JsonObject
        {
            ["instanceNames"] = new JsonArray(spec.InstanceName),
            ["availabilityZone"] = spec.AvailabilityZone,
            ["blueprintId"] = spec.Machine.ImageRef,
            ["bundleId"] = spec.Machine.SizeRef,
        };

        if (!string.IsNullOrWhiteSpace(spec.KeyPairName))
        {
            body["keyPairName"] = spec.KeyPairName;
        }

        if (!string.IsNullOrEmpty(spec.Machine.CloudInit))
        {
            body["userData"] = spec.Machine.CloudInit;
        }

        var tagsArray = new JsonArray();

        // Sorted for the same reason AwsEc2Requests sorts its TagSpecification entries: the plan hash is
        // computed over the same tag set, and a request whose parameter order varied run to run would make two
        // identical launches sign two different payloads.
        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            tagsArray.Add(new JsonObject { ["key"] = tag.Key, ["value"] = tag.Value });
        }

        body["tags"] = tagsArray;

        return body;
    }

    /// <summary>
    /// Builds the <c>TagResource</c> request body that retags an instance which already exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every tag the instance is to carry, not merely the ones that changed.</strong> Lightsail's
    /// <c>TagResource</c> adds or overwrites the tags named in the request and leaves the rest alone, so a
    /// request carrying only the changed keys would still be correct — and would still be the wrong shape here.
    /// Sending the whole canonical set makes the write idempotent in the ownership marks: after this request the
    /// instance provably carries <c>servyx.managed</c>, its instance id, its job id and its connector id at their
    /// live values, whatever the plan asked for and whatever the instance looked like beforehand. The orphan
    /// sweep finds billing instances by exactly those keys, so "provably carries" is worth an extra few bytes.
    /// </para>
    /// <para>
    /// Sorted for the same reason <see cref="CreateInstances"/> sorts its tags: a request whose parameter order
    /// varied run to run would sign two different payloads for one logical change.
    /// </para>
    /// </remarks>
    /// <param name="resourceName">The Lightsail instance name — its identity, and what <c>TagResource</c> keys on.</param>
    /// <param name="tags">The full tag set the instance is to carry, canonical keys included.</param>
    internal static JsonObject TagResource(string resourceName, IReadOnlyDictionary<string, string> tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(tags);

        var tagsArray = new JsonArray();

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            tagsArray.Add(new JsonObject { ["key"] = tag.Key, ["value"] = tag.Value });
        }

        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["tags"] = tagsArray,
        };
    }
}
