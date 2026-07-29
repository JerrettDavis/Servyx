using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that creates DigitalOcean droplets — and nothing else. The first
/// implementation of "shape I" in this codebase.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shape I produces a host, not a game server.</strong> That is the whole architectural claim this
/// type exists to test, and it is visible in what the type does <em>not</em> contain: there is no SteamCMD
/// invocation here, no package manager, no archive extraction, no game definition, no shell script, no
/// cloud-init authoring — no install step of any kind. The adapter creates a machine, waits for it to have an
/// address, and hands back a <see cref="TargetDescriptor"/> whose
/// <see cref="TargetDescriptor.TransportId"/> is <c>"ssh"</c>. From there the existing SSH host-install
/// adapter (shape H) installs onto it exactly as it would onto a bare-metal box a human had racked. A cloud
/// deployment is therefore a two-stage plan, and this is only the first stage.
/// </para>
/// <para>
/// <strong>The composition is by value, not by object.</strong> Worth stating precisely, because it is weaker
/// than the architecture doc implies. <c>SshProcessProvisioner</c>'s constructor takes an endpoint string, a
/// credential URN and an options dictionary — not a <see cref="TargetDescriptor"/> — and builds its own
/// descriptor internally. So the hand-off from shape I to shape H is parameter-passing that a call site has
/// to get right, and nothing at the type level prevents a caller forwarding the endpoint while dropping the
/// credential URN. The data does survive the trip unchanged (a test pins that), but by convention at the call
/// site rather than by construction. The same caveat already applies to the Docker hand-off's transport-id
/// magic string.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no HTTP request at all. A plan is pure
/// computation over the request — including its cost figure, which is why
/// <see cref="DigitalOceanDropletPricing"/> is a static snapshot rather than a pricing API call. This is the
/// strongest form of the "planning changes nothing" guarantee: there is no request to audit, and no way for a
/// plan to spend money or to leak the account token.
/// </para>
/// <para>
/// <strong>Mutation lives outside this type's <see cref="IProvisioner"/> surface.</strong> Creating a droplet
/// is reachable only through <see cref="CreateOperation"/>, which returns an
/// <see cref="IProvisioningOperation"/> for <c>Servyx.Application</c>'s plan executor to drive, exactly as the
/// Docker and SSH adapters do. Nothing on the <see cref="IProvisioner"/> interface mutates anything.
/// </para>
/// <para>
/// <strong>Host-key trust is unresolved for this adapter, and it is deliberately not resolved here.</strong>
/// <c>docs/provisioning.md</c> §6 asserts that a freshly created VM's host key is "captured at creation from
/// the provider API or console output and pinned", which would be stronger than trust-on-first-use.
/// DigitalOcean exposes no such thing. The <c>POST /v2/droplets</c> response carries no host key material,
/// <c>GET /v2/droplets/{id}</c> carries none, and the v2 API has no console-output or serial-console endpoint
/// at all — the fingerprint is printed to the graphical (noVNC) console during first boot, which is not
/// machine-readable. <c>/v2/account/keys</c> holds the <em>client</em> keys the operator uploaded, which are a
/// different thing entirely. So for this adapter that claim is aspirational, and it is not something the
/// adapter can fix.
/// </para>
/// <para>
/// What this type does about it is nothing, on purpose. It stamps no <c>trustPolicy</c> of its own, invents no
/// bypass, and passes the caller's transport options through to the SSH transport's existing host-key
/// mechanism unchanged. Note the consequence, because it is not "the first connection silently trusts
/// whatever answers": Servyx's <c>TrustPolicy.TrustOnFirstUse</c> does not auto-pin — it returns
/// <c>HostKeyVerdict.Unknown</c>, the same hard refusal <c>RequirePinned</c> gives, and expects a human to
/// confirm the fingerprint out of band and pin it explicitly. So an <em>unattended</em> create-then-install
/// pipeline against a brand-new droplet will stop at the SSH handshake unless the fingerprint was obtained by
/// some route outside this API — for example caller-supplied user-data that publishes it, fed back in through
/// the <c>pinnedFingerprints</c> transport option this adapter forwards untouched. That is a real gap in the
/// two-stage cloud flow, and it is recorded here rather than papered over.
/// </para>
/// <para>
/// <strong>Capabilities are what is implemented, not what DigitalOcean offers.</strong> DigitalOcean's API
/// can resize droplets, snapshot them, attach reserved IPs and manage cloud firewalls; this adapter calls none
/// of those, so <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/>,
/// <see cref="ProvisioningCapabilities.StaticAddress"/> and <see cref="ProvisioningCapabilities.FirewallRules"/>
/// are all absent. In particular a <see cref="MachineSpec.Ingress"/> rule is <em>described in the plan as not
/// applied</em> rather than quietly ignored, because a caller who believed a port had been opened when nothing
/// had would expose a server it thought was firewalled.
/// </para>
/// </remarks>
public sealed class DigitalOceanDropletProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "digitalocean-droplet";

    /// <summary>The username a stock DigitalOcean Linux image gives SSH access to.</summary>
    public const string DefaultSshUsername = "root";

    /// <summary>The port the produced <see cref="TargetDescriptor"/> endpoint names.</summary>
    public const int SshPort = 22;

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this
    /// provisioner produces — the <em>existing</em> SSH transport, not a DigitalOcean-specific one.
    /// </summary>
    /// <remarks>
    /// Kept as a constant here (this project cannot reference <c>Servyx.Infrastructure.Ssh</c>: infrastructure
    /// projects reference <c>Servyx.Domain</c> and nothing else) and asserted equal to
    /// <c>SshTransport.TransportId</c> by the composition test, so drift is caught by a test rather than by a
    /// runtime "no transport for id" failure. This is the same stringly-typed seam
    /// <c>docs/provisioning.md</c> §10.1 already flags for the Docker adapter, reproduced here for the same
    /// reason and with the same mitigation.
    /// </remarks>
    internal const string SshTransportId = "ssh";

    /// <summary>
    /// The root path stamped on every descriptor this provisioner produces.
    /// </summary>
    /// <remarks>
    /// Always <c>/</c>, and that is the shape claim restated as a value: shape I hands back a <em>host</em>,
    /// so there is no per-server data directory for it to record. A game's <c>dataDir</c> is chosen by the
    /// install stage that runs afterwards, and appears on the descriptor <em>that</em> stage produces.
    /// </remarks>
    public const string HostRootPath = "/";

    private const string PlanCostUnavailable =
        "No droplet size was named in the provisioning request, so no list price could be looked up.";

    private readonly DigitalOceanApiClient _api;
    private readonly string? _sshCredentialUrn;
    private readonly IReadOnlyDictionary<string, string> _transportOptions;
    private readonly string _sshUsername;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _addressPollInterval;
    private readonly int _addressPollAttempts;

    /// <summary>
    /// Creates a provisioner acting on the DigitalOcean account whose token is stored at
    /// <paramref name="apiTokenUrn"/>.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for every API call. Its <see cref="HttpClient.BaseAddress"/> defaults to
    /// <c>https://api.digitalocean.com/</c> when unset. Tests substitute its <see cref="HttpMessageHandler"/>,
    /// so no network access is required or attempted.
    /// </param>
    /// <param name="secretStore">Where the API token is resolved from, freshly, on every request.</param>
    /// <param name="apiTokenUrn">
    /// The URN of the DigitalOcean personal access token, e.g.
    /// <c>secret://global/digitalocean/api/token</c>. Only the URN is held; the token itself is never a field
    /// on this type or on anything it returns.
    /// </param>
    /// <param name="sshCredentialUrn">
    /// The <see cref="TargetDescriptor.CredentialUrn"/> to stamp on produced descriptors — the URN of the SSH
    /// private key matching the account key the droplet boots with. Never a literal credential, and never the
    /// DigitalOcean token.
    /// </param>
    /// <param name="transportOptions">
    /// Additional <see cref="TargetDescriptor.Options"/> the SSH transport reads (<c>usernameUrn</c>,
    /// <c>passphraseUrn</c>, <c>trustPolicy</c>, <c>pinnedFingerprints</c>, <c>declaredChannels</c>). Applied
    /// before Servyx-owned option keys, so they can never override one. This adapter adds nothing of its own
    /// to the host-key question — see the type remarks.
    /// </param>
    /// <param name="sshUsername">
    /// The username produced endpoints authenticate as. Fixed at construction rather than per-request for the
    /// same reason <c>SshProcessProvisioner</c> fixes its endpoint: <see cref="RefreshAsync"/> must be able to
    /// rebuild an identical descriptor from the droplet alone, and a droplet does not record which user
    /// Servyx intends to log in as.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry and for waiting on a new droplet's address.</param>
    /// <param name="addressPollInterval">How long to wait between address polls. Defaults to five seconds.</param>
    /// <param name="addressPollAttempts">How many address polls to make before giving up. Defaults to 60.</param>
    public DigitalOceanDropletProvisioner(
        HttpClient httpClient,
        ISecretStore secretStore,
        SecretUrn apiTokenUrn,
        string? sshCredentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string sshUsername = DefaultSshUsername,
        TimeProvider? timeProvider = null,
        TimeSpan? addressPollInterval = null,
        int addressPollAttempts = 60)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshUsername);
        ArgumentOutOfRangeException.ThrowIfLessThan(addressPollAttempts, 1);

        _api = new DigitalOceanApiClient(httpClient, secretStore, apiTokenUrn);
        _sshCredentialUrn = sshCredentialUrn;
        _transportOptions = transportOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(transportOptions, StringComparer.Ordinal);
        _sshUsername = sshUsername;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _addressPollInterval = addressPollInterval ?? TimeSpan.FromSeconds(5);
        _addressPollAttempts = addressPollAttempts;
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: droplets bill by the hour,
    /// so an orphan that cannot be found by tag bills forever. It is also the strong, registry-backed form of
    /// that capability — DigitalOcean applies the tags in the same call that creates the droplet, so there is
    /// no window in which a created droplet exists untagged.
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is present because
    /// <see cref="DigitalOceanDropletPricing"/> carries real published list prices; it answers
    /// <see cref="CostEstimate.Unknown"/> for any size it does not know rather than approximating.
    /// <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/>,
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> and
    /// <see cref="ProvisioningCapabilities.FirewallRules"/> are all deliberately absent — see the type remarks.
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.EstimatesCost;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the droplet spec from <paramref name="request"/>'s parameters and describes the
    /// stages needed to realise it. Issues no HTTP request whatsoever, so it cannot create, change, or bill for
    /// anything — and cannot resolve, let alone transmit, the account token.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var spec = BuildSpec(request);
        return Task.FromResult(BuildPlan(spec));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed the
    /// spec themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    public ProvisioningPlan BuildPlan(DigitalOceanDropletSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var dropletTags = ServyxDropletTags.ToDropletTags(TagsFor(spec));

        var stages = new List<ProvisioningStage>
        {
            new(
                "create-droplet",
                Id,
                $"Create droplet '{spec.DropletName}' in region '{spec.Machine.Region}' from image "
                + $"'{spec.Machine.ImageRef}' at size '{spec.Machine.SizeRef}', with {dropletTags.Count} Servyx tag(s), "
                + $"{spec.SshKeyFingerprints.Count} account SSH key(s), and "
                + (string.IsNullOrEmpty(spec.Machine.CloudInit)
                    ? "no user-data (this provisioner never authors cloud-init; nothing is installed on the machine here)."
                    : $"{spec.Machine.CloudInit.Length} character(s) of caller-supplied user-data, forwarded verbatim.")),
            new(
                "await-public-address",
                Id,
                "Poll the droplet until DigitalOcean reports a public IPv4 address for it. No change is made to the "
                + "droplet by this stage."),
            new(
                "handoff-ssh-target",
                Id,
                $"Hand back an 'ssh://{_sshUsername}@<address>:{SshPort}' target descriptor for the new host. "
                + "Provisioning stops here: installing a game server onto this host is a separate stage run by the "
                + "SSH host-install provisioner, identically to any bare-metal SSH box."),
        };

        if (spec.Machine.Ingress.Count > 0)
        {
            // Stated as a stage rather than silently skipped: this provisioner does not advertise
            // ProvisioningCapabilities.FirewallRules, and a caller who believed a port had been opened when
            // nothing had would expose a server it thought was firewalled.
            stages.Add(new ProvisioningStage(
                "ingress-not-applied",
                Id,
                $"NOT APPLIED: {spec.Machine.Ingress.Count} inbound rule(s) were requested "
                + $"({string.Join(", ", spec.Machine.Ingress.Select(r => $"{r.Protocol}/{r.Port} from {r.SourceCidr ?? "any"}"))}), "
                + "but this provisioner does not implement FirewallRules and will not create a DigitalOcean cloud "
                + "firewall. Apply them separately."));
        }

        var planHash = ComputePlanHash(spec, dropletTags);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.DropletName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: string.IsNullOrWhiteSpace(spec.Machine.SizeRef)
                ? CostEstimate.Unknown(PlanCostUnavailable + " " + DigitalOceanDropletPricing.Source)
                : DigitalOceanDropletPricing.For(spec.Machine.SizeRef),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the droplet back from the provider by id — the registry-backed shape, so the answer reflects the
    /// droplet as DigitalOcean currently describes it. A droplet the provider no longer has (HTTP 404), or one
    /// whose tags no longer identify it as Servyx-managed, yields <see langword="null"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The droplet exists but reports no IPv4 address yet, so no SSH target can be described for it. This is a
    /// transient boot state, not a missing resource, and is deliberately distinguished from
    /// <see langword="null"/> — treating "still booting" as "gone" would let a caller conclude a billable
    /// droplet had disappeared.
    /// </exception>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!TryReadDropletId(handle.ProviderResourceId, out var dropletId))
        {
            return null;
        }

        var droplet = await _api.GetDropletAsync(dropletId, ct).ConfigureAwait(false);
        if (droplet is null)
        {
            return null;
        }

        var tags = ServyxDropletTags.FromDropletTagsToDictionary(droplet.Tags);
        var identity = ServyxDropletTags.FromTags(tags);
        if (identity is null)
        {
            return null;
        }

        return new ProvisionedResource(
            Handle: HandleFor(droplet, tags),
            ConnectorId: identity.ConnectorId,
            Target: BuildTargetDescriptor(RequireSshAddress(droplet)),
            Facts: BuildFacts(droplet));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive, and the reason this adapter may be trusted with billable resources at all.
    /// Asks DigitalOcean for every droplet carrying <see cref="ServyxDropletTags.ManagedFilter"/>, independent
    /// of any Servyx-local record, so a droplet created but never acknowledged can still be found.
    /// </para>
    /// <para>
    /// The tag filter is sent to the API <em>and</em> re-applied to every droplet in the response, the same
    /// two-step the Docker sweep performs for the same reason: the filter is the provider's promise, the second
    /// check is this process's own guarantee that nothing untagged is ever reported as Servyx-owned and
    /// subsequently destroyed. A sweep acting on a false positive deletes someone else's droplet.
    /// </para>
    /// <para>
    /// <strong>Only <see cref="OrphanScope.ProviderWide"/> is served.</strong> The provider's own inventory is
    /// the search space, so a scope describing some other search space — an
    /// <see cref="OrphanScope.MarkerDirectory"/>, say — is declined exactly as a scope naming another
    /// provisioner is: no handles, and no API call. Quietly widening a narrower request into "every managed
    /// droplet in the account" would hand a caller more droplets than it asked to sweep, and a sweep's output
    /// is a delete list.
    /// </para>
    /// <para>
    /// Unlike Docker and SSH, this provider genuinely is region-scoped, so
    /// <see cref="OrphanScope.Region"/> is honoured when set: a sweep restricted to <c>nyc3</c> reports only
    /// <c>nyc3</c> droplets. Note the direction of the risk — a region filter can only ever narrow the delete
    /// list, never widen it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope is not OrphanScope.ProviderWide || !string.Equals(scope.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return [];
        }

        var droplets = await _api
            .ListDropletsByTagAsync(ServyxDropletTags.ManagedFilter, ct)
            .ConfigureAwait(false);

        var handles = new List<ResourceHandle>();
        foreach (var droplet in droplets)
        {
            if (!ServyxDropletTags.IsManaged(droplet.Tags))
            {
                continue;
            }

            if (scope.Region is not null
                && !string.Equals(droplet.Region?.Slug, scope.Region, StringComparison.Ordinal))
            {
                continue;
            }

            handles.Add(HandleFor(droplet, ServyxDropletTags.FromDropletTagsToDictionary(droplet.Tags)));
        }

        return handles;
    }

    /// <summary>
    /// Returns the mutating operation that creates the droplet described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>: the returned operation is driven by
    /// <c>Servyx.Application</c>'s plan executor, which owns the write-ahead ledger ordering. Calling this
    /// method creates nothing on its own — and, critically for a provider that bills by the hour, makes no
    /// billable API call.
    /// </remarks>
    public IProvisioningOperation CreateOperation(DigitalOceanDropletSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new DropletCreateOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above: builds the droplet spec the same way
    /// <see cref="PlanAsync"/> does, via <see cref="BuildSpec"/>, so a plan preview and the operation that
    /// later realises it are always derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently destroys a droplet this provisioner created, making
    /// <see cref="ProvisioningCapabilities.Destroy"/> a real capability rather than an advertised one.
    /// </summary>
    /// <remarks>
    /// Destroying a droplet destroys its boot disk with it, so this is materially more destructive than the
    /// Docker adapter's container removal (which preserves volumes) or the SSH adapter's marker removal (which
    /// preserves <c>dataDir</c>). That is not something this adapter can soften — a droplet <em>is</em> the
    /// machine — which is precisely why it is reachable only through an explicit call with a handle in hand,
    /// and never as a side effect of anything on <see cref="IProvisioner"/>. Attached block-storage volumes are
    /// not deleted: DigitalOcean detaches them, and this adapter issues no volume call.
    /// </remarks>
    /// <returns><see langword="true"/> if the droplet was destroyed; <see langword="false"/> if it was already gone.</returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return TryReadDropletId(handle.ProviderResourceId, out var dropletId)
            && await _api.DeleteDropletAsync(dropletId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into a droplet spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx tags.</description></item>
    /// <item><description><c>name</c> — required, the droplet's name at the provider.</description></item>
    /// <item><description><c>image</c>, <c>size</c>, <c>region</c> — required; DigitalOcean slugs, e.g. <c>ubuntu-24-04-x64</c>, <c>s-2vcpu-4gb</c>, <c>nyc3</c>.</description></item>
    /// <item><description><c>sshKey:&lt;n&gt;</c> — the id or MD5 fingerprint of an SSH key already registered on the account; <c>n</c> fixes the order.</description></item>
    /// <item><description><c>sshPublicKey</c> — the operator's declared public key. Part of the plan hash but not sent: DigitalOcean's API cannot consume raw key material (see <see cref="DigitalOceanDropletSpec"/>).</description></item>
    /// <item><description><c>cloudInit</c> — user-data, forwarded verbatim. Nothing here authors one.</description></item>
    /// <item><description><c>ingress:&lt;port&gt;/&lt;protocol&gt;</c> — value is the source CIDR, or empty for any. Recorded and reported as NOT applied; see the type remarks.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra Servyx tag; can never shadow a mandatory one.</description></item>
    /// </list>
    /// A key-per-item shape is used rather than one delimited string, matching both existing adapters, so no
    /// separator can collide with a value.
    /// <para>
    /// There is deliberately no <c>credentialUrn</c>, <c>sshUsername</c> or <c>endpoint</c> key: all three are
    /// fixed at construction, because <see cref="RefreshAsync"/> must be able to rebuild an identical
    /// descriptor from a droplet that records none of them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A required parameter is missing, or a value is not expressible as a DigitalOcean tag.</exception>
    public static DigitalOceanDropletSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxDropletTags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var sshKeys = new SortedDictionary<int, string>();
        var ingress = new List<FirewallRule>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("sshKey:", StringComparison.Ordinal))
            {
                sshKeys[ParseIndex(pair.Key, "sshKey:")] = pair.Value;
            }
            else if (pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                ingress.Add(ParseIngress(pair.Key["ingress:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("tag:", StringComparison.Ordinal))
            {
                extraTags[pair.Key["tag:".Length..]] = pair.Value;
            }
        }

        var machine = new MachineSpec(
            ImageRef: Required(parameters, "image"),
            SizeRef: Required(parameters, "size"),
            Region: Required(parameters, "region"),
            SshPublicKey: parameters.TryGetValue("sshPublicKey", out var publicKey) ? publicKey : string.Empty,
            CloudInit: parameters.TryGetValue("cloudInit", out var cloudInit) && !string.IsNullOrEmpty(cloudInit)
                ? cloudInit
                : null,
            Ingress: ingress
                .OrderBy(r => r.Port)
                .ThenBy(r => r.Protocol, StringComparer.Ordinal)
                .ToList(),
            Tags: tags.ToTags(extraTags));

        return new DigitalOceanDropletSpec(Required(parameters, "name"), machine, tags)
        {
            SshKeyFingerprints = sshKeys.Values.ToList(),
            AdditionalTags = extraTags,
        };
    }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for a droplet. This is the whole hand-off, and the whole shape
    /// claim: the value returned here names the <em>existing</em> SSH transport, carries an ordinary
    /// <c>ssh://user@host:port</c> endpoint, and contains not one DigitalOcean-specific field. Nothing
    /// downstream needs to know a cloud API was ever involved.
    /// </summary>
    internal TargetDescriptor BuildTargetDescriptor(string address)
    {
        // Caller-supplied options first, Servyx-owned keys last, so an option can never shadow one - the same
        // ordering rule ServyxTagKeys.Build applies to tags, and the same one SshProcessProvisioner applies to
        // its own descriptor options.
        var options = new Dictionary<string, string>(_transportOptions, StringComparer.Ordinal)
        {
            ["rootPath"] = HostRootPath,
        };

        return new TargetDescriptor(
            TransportId: SshTransportId,
            Endpoint: string.Create(CultureInfo.InvariantCulture, $"ssh://{_sshUsername}@{address}:{SshPort}"),
            CredentialUrn: _sshCredentialUrn,
            DockerContext: null,
            Options: options);
    }

    /// <summary>The full Servyx tag dictionary for a spec: caller extras first, canonical keys last.</summary>
    private static IReadOnlyDictionary<string, string> TagsFor(DigitalOceanDropletSpec spec) =>
        spec.Tags.ToTags(spec.AdditionalTags);

    private ResourceHandle HandleFor(DropletResource droplet, IReadOnlyDictionary<string, string> tags) =>
        new(
            Id,
            droplet.Id.ToString(CultureInfo.InvariantCulture),
            droplet.Region?.Slug,
            tags);

    private static ResourceFacts BuildFacts(DropletResource droplet) =>
        new(
            PublicAddress: AddressOfType(droplet, "public"),
            PrivateAddress: AddressOfType(droplet, "private"),
            Cost: DigitalOceanDropletPricing.For(droplet.SizeSlug),
            CreatedAt: droplet.CreatedAt ?? DateTimeOffset.UnixEpoch);

    private static string? AddressOfType(DropletResource droplet, string type) =>
        droplet.Networks?.V4?
            .FirstOrDefault(n => string.Equals(n.Type, type, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(n.IpAddress))?
            .IpAddress;

    /// <summary>The address a descriptor names: the public IPv4 if there is one, otherwise the private one.</summary>
    private static string RequireSshAddress(DropletResource droplet) =>
        AddressOfType(droplet, "public")
        ?? AddressOfType(droplet, "private")
        ?? throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Droplet {droplet.Id} exists (status '{droplet.Status}') but DigitalOcean reports no IPv4 address for it yet, so no SSH target can be described. This is a transient boot state, not a missing droplet - the droplet is billing and must not be treated as gone."));

    private static bool TryReadDropletId(string? providerResourceId, out long dropletId) =>
        long.TryParse(providerResourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out dropletId);

    private string ComputePlanHash(DigitalOceanDropletSpec spec, IReadOnlyList<string> dropletTags)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.DropletName).Append('\n');
        builder.Append(spec.Machine.ImageRef).Append('\n');
        builder.Append(spec.Machine.SizeRef).Append('\n');
        builder.Append(spec.Machine.Region).Append('\n');
        builder.Append(spec.Machine.SshPublicKey).Append('\n');
        builder.Append(spec.Machine.CloudInit ?? string.Empty).Append('\n');
        builder.Append(_sshUsername).Append('\n');
        builder.Append(HostRootPath).Append('\n');

        foreach (var fingerprint in spec.SshKeyFingerprints)
        {
            builder.Append(CultureInfo.InvariantCulture, $"sshKey {fingerprint}\n");
        }

        foreach (var rule in spec.Machine.Ingress)
        {
            builder.Append(CultureInfo.InvariantCulture, $"ingress {rule.Protocol}/{rule.Port} from {rule.SourceCidr ?? "any"}\n");
        }

        foreach (var tag in dropletTags)
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag}\n");
        }

        foreach (var option in _transportOptions.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"option {option.Key}={option.Value}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static int ParseIndex(string key, string prefix)
    {
        if (!int.TryParse(key[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw new ArgumentException($"'{key}' does not carry a numeric index after '{prefix}'.", nameof(key));
        }

        return index;
    }

    private static FirewallRule ParseIngress(string portKey, string sourceCidr)
    {
        var slash = portKey.IndexOf('/', StringComparison.Ordinal);
        var portText = slash < 0 ? portKey : portKey[..slash];
        var protocol = slash < 0 ? "tcp" : portKey[(slash + 1)..];

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException(
                $"'{portKey}' is not a valid 'ingress:<number>[/<protocol>]' provisioning parameter key.",
                nameof(portKey));
        }

        return new FirewallRule(port, protocol.ToLowerInvariant(), string.IsNullOrWhiteSpace(sourceCidr) ? null : sourceCidr);
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Provisioning parameter '{key}' is required by the '{Id}' provisioner.", nameof(parameters));
        }

        return value;
    }

    /// <summary>
    /// The mutating half of provisioning, kept off <see cref="IProvisioner"/> entirely and nested inside the
    /// provisioner so it — and only it — can reach the API client the provisioner is configured with.
    /// </summary>
    private sealed class DropletCreateOperation : IProvisioningOperation
    {
        private readonly DigitalOceanDropletProvisioner _owner;
        private readonly DigitalOceanDropletSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;
        private long? _createdDropletId;

        internal DropletCreateOperation(DigitalOceanDropletProvisioner owner, DigitalOceanDropletSpec spec)
        {
            _owner = owner;
            _spec = spec;

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in order
            // to commit them to the write-ahead ledger - so they must be the same values that later reach the
            // provider, not a set recomputed later.
            _tags = TagsFor(spec);
        }

        public string ProvisionerId => Id;

        public string? Region => _spec.Machine.Region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Creates the droplet, waits for it to have an address, and hands back an SSH target for it.
        /// </summary>
        /// <remarks>
        /// Tags are applied by the same call that creates the droplet, so — unlike the marker-file shape —
        /// there is no window in which a billing droplet exists that a sweep could not find. Note what this
        /// method does <em>not</em> do: it runs no command on the machine, uploads nothing to it, and never
        /// opens an SSH connection at all. It does not know what game is going to be installed.
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var body = new CreateDropletRequest
            {
                Name = _spec.DropletName,
                Region = _spec.Machine.Region,
                Size = _spec.Machine.SizeRef,
                Image = _spec.Machine.ImageRef,
                SshKeys = _spec.SshKeyFingerprints,
                Tags = ServyxDropletTags.ToDropletTags(_tags),
                UserData = _spec.Machine.CloudInit,
            };

            var created = await _owner._api.CreateDropletAsync(body, ct).ConfigureAwait(false);
            _createdDropletId = created.Id;

            var droplet = await WaitForAddressAsync(created, ct).ConfigureAwait(false);

            return new ProvisionedResource(
                Handle: _owner.HandleFor(droplet, ServyxDropletTags.FromDropletTagsToDictionary(droplet.Tags)),
                ConnectorId: _spec.Tags.ConnectorId,
                Target: _owner.BuildTargetDescriptor(RequireSshAddress(droplet)),
                Facts: BuildFacts(droplet));
        }

        /// <summary>
        /// Destroys whatever droplet this operation may have created.
        /// </summary>
        /// <remarks>
        /// When the create call never handed back an id, this does not assume nothing was created — it asks the
        /// provider by tag instead, mirroring the Docker operation's refusal to make the same assumption. For a
        /// per-hour billed machine the difference is a machine that bills forever versus one that does not.
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            if (_createdDropletId is { } dropletId)
            {
                await _owner._api.DeleteDropletAsync(dropletId, ct).ConfigureAwait(false);
                return;
            }

            var droplets = await _owner._api
                .ListDropletsByTagAsync(ServyxDropletTags.ManagedFilter, ct)
                .ConfigureAwait(false);

            foreach (var droplet in droplets)
            {
                var tags = ServyxDropletTags.FromDropletTagsToDictionary(droplet.Tags);
                if (ServyxDropletTags.IsManaged(droplet.Tags)
                    && tags.TryGetValue(ServyxDropletTags.InstanceIdTag, out var instanceId)
                    && string.Equals(instanceId, _spec.Tags.InstanceId, StringComparison.Ordinal))
                {
                    await _owner._api.DeleteDropletAsync(droplet.Id, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task<DropletResource> WaitForAddressAsync(DropletResource created, CancellationToken ct)
        {
            var latest = created;

            for (var attempt = 0; attempt < _owner._addressPollAttempts; attempt++)
            {
                if (AddressOfType(latest, "public") is not null || AddressOfType(latest, "private") is not null)
                {
                    return latest;
                }

                await Task.Delay(_owner._addressPollInterval, _owner._timeProvider, ct).ConfigureAwait(false);

                latest = await _owner._api.GetDropletAsync(created.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture, $"Droplet {created.Id} was created but DigitalOcean no longer reports it. Servyx cannot describe a target for it; reconcile by tag before assuming nothing is billing."));
            }

            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Droplet {created.Id} did not report an IPv4 address within {_owner._addressPollAttempts} poll(s). The droplet exists and is billing; compensation will destroy it."));
        }
    }
}
