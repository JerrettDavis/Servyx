using System.Diagnostics.CodeAnalysis;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// The universal Servyx tags every droplet this project creates must carry, together with the encoding that
/// squeezes Servyx's <c>key=value</c> vocabulary into DigitalOcean's flat, charset-restricted tag strings.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THE ENCODING — read this before touching anything in this file.</strong> Orphan-sweep correctness
/// depends on the mapping below being exact in both directions. A droplet whose managed tag does not match
/// byte-for-byte is, as far as <see cref="DigitalOceanDropletProvisioner.ReconcileAsync"/> can tell, somebody
/// else's machine — and it will bill forever with no local trace.
/// </para>
/// <para>
/// DigitalOcean tags are not key/value pairs at all: a droplet carries a flat <c>string[]</c>, and per
/// <see href="https://docs.digitalocean.com/reference/api/reference/tags/">the DigitalOcean API reference</see>
/// a tag "may contain letters, numbers, colons, dashes, and underscores", to a maximum of 255 characters.
/// That excludes both characters Servyx's vocabulary relies on: <c>.</c> (every key is
/// <c>servyx.&lt;something&gt;</c>) and <c>=</c> (the obvious pair separator, and the one the Docker adapter
/// uses in its engine filter). So <c>servyx.managed=true</c> is not directly expressible and has to be
/// encoded. The mapping is:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>.</c> in the <em>key</em> becomes <c>_</c>. Reversible because no key in the Servyx vocabulary contains
/// a literal <c>_</c> — the keys separate words with <c>-</c> (<c>servyx.instance-id</c>). That is not an
/// assumption: <see cref="Encode"/> rejects any key containing <c>_</c>, so the day someone adds
/// <c>servyx.some_key</c> this throws instead of silently producing a tag that decodes to a different key.
/// </description></item>
/// <item><description>
/// <c>=</c> between key and value becomes <c>:</c>. Decoding splits on the <em>first</em> <c>:</c>, so a value
/// may itself contain colons without ambiguity.
/// </description></item>
/// <item><description>
/// The value is <em>not</em> transformed. It must already consist only of DigitalOcean-legal characters, and
/// <see cref="Encode"/> refuses it otherwise. Escaping the value would need an escape character, and every
/// candidate (<c>_</c>, <c>-</c>, <c>:</c>) already occurs in real Servyx ids — so an escape scheme would be
/// exactly the kind of nearly-reversible mapping this file exists to avoid.
/// </description></item>
/// </list>
/// <para>
/// Worked example, and the single most load-bearing string in this assembly:
/// <c>servyx.managed=true</c> ⇄ <c>servyx_managed:true</c>.
/// </para>
/// <para>
/// <strong>The consequence, stated plainly.</strong> A Servyx instance/job/connector id containing a
/// <c>.</c> cannot be carried as a DigitalOcean tag. <see cref="For"/> therefore rejects it at construction,
/// rather than mangling it into something a later sweep would misattribute. This is a genuine asymmetry with
/// the other two adapters: Docker labels accept any id, and <c>ServyxProcessMarker</c> accepts <c>.</c>
/// because a dot is legal in a filename. It is the direct analogue of that adapter's filename-charset
/// constraint — an id that becomes part of a provider-side identifier inherits that identifier's charset —
/// and it is documented rather than smoothed over because a caller whose id is rejected needs to know why.
/// </para>
/// <para>
/// <strong>What is deliberately not tagged.</strong> Values carrying <c>/</c>, <c>+</c> or <c>.</c> — a POSIX
/// path, an ISO-8601 timestamp — are not expressible, so this adapter never attempts to store them as tags.
/// It does not need to: a droplet object already reports its own <c>created_at</c> and region, and Shape I
/// produces a <em>host</em>, whose descriptor root path is always <c>/</c> and so carries no per-server data
/// directory to record. Anything genuinely inexpressible makes <see cref="ToDropletTags"/> throw; nothing is
/// silently dropped.
/// </para>
/// </remarks>
public sealed class ServyxDropletTags
{
    /// <summary>The character DigitalOcean tags use in place of the <c>=</c> that separates a Servyx key from its value.</summary>
    public const char PairSeparator = ':';

    /// <summary>The character DigitalOcean tags use in place of the <c>.</c> in a Servyx key.</summary>
    public const char KeyDotReplacement = '_';

    /// <summary>The maximum length DigitalOcean accepts for a single tag.</summary>
    public const int MaxTagLength = 255;

    /// <summary>Marks a droplet as created and owned by Servyx.</summary>
    public const string ManagedTag = ServyxTagKeys.Managed;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxTagKeys.ManagedValue;

    /// <summary>Identifies the Servyx server/instance the droplet backs.</summary>
    public const string InstanceIdTag = ServyxTagKeys.InstanceId;

    /// <summary>Identifies the provisioning job that asked for the droplet.</summary>
    public const string JobIdTag = ServyxTagKeys.JobId;

    /// <summary>Identifies the connector the droplet is reachable through, so a refresh can rebuild it.</summary>
    public const string ConnectorIdTag = ServyxTagKeys.ConnectorId;

    /// <summary>
    /// The exact DigitalOcean tag string that selects every Servyx-managed droplet — the value sent as
    /// <c>GET /v2/droplets?tag_name=</c> and the value re-checked on every droplet in the response.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="ServyxTagKeys"/> through <see cref="Encode"/> rather than written out as a
    /// literal, so the sweep filter cannot drift away from the vocabulary the other adapters stamp. Its
    /// literal value (<c>servyx_managed:true</c>) is pinned by a test, because that literal is what a human
    /// will type into the DigitalOcean console when auditing what Servyx owns.
    /// </remarks>
    public static string ManagedFilter { get; } = Encode(ManagedTag, ManagedTagValue);

    private ServyxDropletTags(string instanceId, string jobId, string connectorId)
    {
        InstanceId = instanceId;
        JobId = jobId;
        ConnectorId = connectorId;
    }

    /// <summary>The Servyx server/instance the droplet backs.</summary>
    public string InstanceId { get; }

    /// <summary>The provisioning job that asked for the droplet.</summary>
    public string JobId { get; }

    /// <summary>The connector the droplet is reachable through.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The only way to obtain a <see cref="ServyxDropletTags"/>. Every parameter is required, and every one
    /// is additionally constrained to the DigitalOcean tag charset — see the type remarks for why an id
    /// containing <c>.</c> is refused here rather than transformed.
    /// </summary>
    /// <exception cref="ArgumentException">Any argument is blank or is not expressible as a DigitalOcean tag value.</exception>
    public static ServyxDropletTags For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        RequireTaggableValue(instanceId, nameof(instanceId));
        RequireTaggableValue(jobId, nameof(jobId));
        RequireTaggableValue(connectorId, nameof(connectorId));

        return new ServyxDropletTags(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the canonical Servyx tag dictionary. Any <paramref name="additional"/> tags are applied first
    /// and the mandatory ones last, so an extra can never override one.
    /// </summary>
    /// <remarks>
    /// The ordering rule is <see cref="ServyxTagKeys.Build"/>'s, applied by calling it — the same single
    /// implementation the Docker and SSH adapters call. What differs is only where the resulting dictionary
    /// is stored: here it is encoded by <see cref="ToDropletTags"/> and sent as a droplet's <c>tags</c> array.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>The wire form of <see cref="ToTags"/>: the DigitalOcean <c>tags</c> array for a droplet.</summary>
    /// <exception cref="ArgumentException">Any tag is not faithfully expressible as a DigitalOcean tag.</exception>
    public IReadOnlyList<string> ToDropletTagArray(IReadOnlyDictionary<string, string>? additional = null) =>
        ToDropletTags(ToTags(additional));

    /// <summary>
    /// Reconstructs tags from a live droplet's tag array, or returns <see langword="null"/> if the droplet is
    /// not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    public static ServyxDropletTags? FromDropletTags(IEnumerable<string>? dropletTags) =>
        FromTags(FromDropletTagsToDictionary(dropletTags));

    /// <summary>Reconstructs tags from an already-decoded dictionary. The dictionary counterpart of <see cref="FromDropletTags"/>.</summary>
    public static ServyxDropletTags? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
        && IsTaggableValue(instanceId)
        && IsTaggableValue(jobId)
        && IsTaggableValue(connectorId)
            ? new ServyxDropletTags(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a droplet's tag array marks it as Servyx-managed.</summary>
    /// <remarks>
    /// Deliberately an exact ordinal match against <see cref="ManagedFilter"/>, matching
    /// <see cref="ServyxTagKeys.IsManaged"/>'s refusal to treat <c>"TRUE"</c> or <c>"1"</c> as ownership: a
    /// sweep that guesses wrong here destroys someone else's droplet.
    /// </remarks>
    public static bool IsManaged(IEnumerable<string>? dropletTags) =>
        dropletTags is not null && dropletTags.Any(t => string.Equals(t, ManagedFilter, StringComparison.Ordinal));

    /// <summary>Encodes one Servyx <c>key</c>/<c>value</c> pair as a single DigitalOcean tag.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is blank, contains <c>_</c>, or contains a character outside
    /// <c>[A-Za-z0-9.-]</c>; <paramref name="value"/> is blank or contains a character outside
    /// <c>[A-Za-z0-9:_-]</c>; or the encoded tag exceeds <see cref="MaxTagLength"/>.
    /// </exception>
    public static string Encode(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!IsEncodableKey(key))
        {
            throw new ArgumentException(
                $"Tag key '{key}' cannot be encoded as a DigitalOcean tag. A key may contain only letters, digits, "
                + $"'.', and '-'; in particular it must not contain '{KeyDotReplacement}', which the encoding "
                + "reserves as the substitute for '.' and could not otherwise decode unambiguously.",
                nameof(key));
        }

        if (!IsTaggableValue(value))
        {
            throw new ArgumentException(
                $"Tag value '{value}' cannot be encoded as a DigitalOcean tag. DigitalOcean tags may contain only "
                + "letters, digits, ':', '-', and '_' - notably not '.', '=', '/', or whitespace - and this "
                + "encoding deliberately does not escape values, because every available escape character already "
                + "occurs in real Servyx identifiers.",
                nameof(value));
        }

        var encoded = string.Concat(key.Replace('.', KeyDotReplacement), PairSeparator.ToString(), value);
        return encoded.Length <= MaxTagLength
            ? encoded
            : throw new ArgumentException(
                $"The encoded tag for key '{key}' is {encoded.Length} characters, over DigitalOcean's {MaxTagLength}-character limit.",
                nameof(value));
    }

    /// <summary>Decodes a single DigitalOcean tag back into the Servyx <c>key</c>/<c>value</c> pair it was encoded from.</summary>
    /// <remarks>
    /// Returns <see langword="false"/> rather than throwing for a tag this encoding did not produce: a
    /// DigitalOcean account holds tags applied by humans and by other tools, and a sweep must be able to walk
    /// past them without erroring.
    /// </remarks>
    public static bool TryDecode(string? dropletTag, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out string? value)
    {
        key = null;
        value = null;

        if (string.IsNullOrEmpty(dropletTag))
        {
            return false;
        }

        var separator = dropletTag.IndexOf(PairSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator == dropletTag.Length - 1)
        {
            return false;
        }

        var decodedKey = dropletTag[..separator].Replace(KeyDotReplacement, '.');
        var decodedValue = dropletTag[(separator + 1)..];

        if (!IsEncodableKey(decodedKey) || !IsTaggableValue(decodedValue))
        {
            return false;
        }

        key = decodedKey;
        value = decodedValue;
        return true;
    }

    /// <summary>Encodes a whole Servyx tag dictionary as a DigitalOcean <c>tags</c> array, key-sorted for determinism.</summary>
    /// <exception cref="ArgumentException">Any entry is not faithfully expressible as a DigitalOcean tag.</exception>
    public static IReadOnlyList<string> ToDropletTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return tags
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => Encode(t.Key, t.Value))
            .ToList();
    }

    /// <summary>
    /// Decodes a droplet's tag array into a Servyx tag dictionary, skipping any tag this encoding did not
    /// produce (a human-applied <c>production</c> tag, another tool's tag, and so on).
    /// </summary>
    public static IReadOnlyDictionary<string, string> FromDropletTagsToDictionary(IEnumerable<string>? dropletTags)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var dropletTag in dropletTags ?? [])
        {
            if (TryDecode(dropletTag, out var key, out var value))
            {
                tags[key] = value;
            }
        }

        return tags;
    }

    /// <summary>Whether <paramref name="value"/> consists only of characters DigitalOcean accepts in a tag.</summary>
    public static bool IsTaggableValue([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTagLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not (':' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="key"/> is a Servyx key this encoding can carry reversibly.</summary>
    public static bool IsEncodableKey([NotNullWhen(true)] string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxTagLength)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireTaggableValue(string value, string paramName)
    {
        if (!IsTaggableValue(value))
        {
            throw new ArgumentException(
                $"'{value}' cannot be carried as a DigitalOcean tag value, so a droplet created for it could not be "
                + "attributed back to Servyx by an orphan sweep. DigitalOcean tags may contain only letters, digits, "
                + "':', '-', and '_' - notably not '.', which is legal in a Servyx marker-file instance id but not here.",
                paramName);
        }
    }
}
