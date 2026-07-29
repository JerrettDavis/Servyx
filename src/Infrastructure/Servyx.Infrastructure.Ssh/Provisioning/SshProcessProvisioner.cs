using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that installs a game server as a plain host process over SSH, and hands the
/// result back as a <see cref="TargetDescriptor"/> the existing <see cref="SshTransport"/> consumes directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The process half of "shape H".</strong> <c>DockerContainerProvisioner</c> proved that a container
/// install fits behind <see cref="IProvisioner"/>. This type is the other half of that claim: a genuinely
/// different installation mechanism — SteamCMD over SSH, writing files onto a host filesystem — behind the
/// same seam, differing only in installer strategy. Where the two shapes had to be reconciled, the reconciling
/// is documented in place rather than smoothed over; see <see cref="ServyxProcessMarker"/> for the largest
/// one (labels versus marker files).
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> opens no connection and issues no command at
/// all. A plan is pure computation over the request, which is the strongest form of the "planning changes
/// nothing" guarantee: there is no call to audit.
/// </para>
/// <para>
/// <strong>Mutation lives outside this type's <see cref="IProvisioner"/> surface.</strong> Installing is
/// reachable only through <see cref="CreateOperation"/>, which returns an <see cref="IProvisioningOperation"/>
/// for <c>Servyx.Application</c>'s plan executor to drive. Nothing on the <see cref="IProvisioner"/> interface
/// mutates anything, exactly as its remarks require.
/// </para>
/// <para>
/// <strong>One endpoint, resolved once.</strong> The host this provisioner acts on and the host its
/// descriptors name are the same value by construction: every connection it opens is opened <em>with the
/// descriptor it stamps</em>, so the two cannot drift apart. That is the direct lesson of the Docker
/// provisioning registration bug, in which a caller-supplied endpoint was stamped onto descriptors while the
/// client was built from an unrelated one.
/// </para>
/// <para>
/// <strong>Steps are argv arrays, never shell strings.</strong> Every install verb becomes a
/// <see cref="CommandSpec"/> with a separate <see cref="CommandSpec.Executable"/> and
/// <see cref="CommandSpec.Arguments"/>. No code path here builds a command line; the single place a string is
/// unavoidable is the SSH <c>exec</c> wire format, where <see cref="PosixArgv"/> quotes each element
/// individually. A path or app id containing <c>; rm -rf /</c> is therefore an inert argv element.
/// </para>
/// <para>
/// <strong>No cost estimation, deliberately.</strong> <see cref="Capabilities"/> omits
/// <see cref="ProvisioningCapabilities.EstimatesCost"/>: a process on a machine Servyx did not rent has no
/// provider-billed price, and <see cref="CostEstimate.Unknown"/> is the honest answer.
/// <see cref="ProvisioningCapabilities.FirewallRules"/> is likewise omitted — this provisioner does not touch
/// the host's firewall, and advertising a capability it does not implement would let a caller believe a port
/// had been opened when nothing had.
/// </para>
/// </remarks>
public sealed class SshProcessProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "ssh-process";

    /// <summary>The directory marker files live under when no other root is configured.</summary>
    public const string DefaultMarkerRoot = "/var/lib/servyx/instances";

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this
    /// provisioner produces. Kept as a constant here (rather than instantiating an <see cref="SshTransport"/>
    /// merely to read its property) and asserted equal to <see cref="SshTransport.TransportId"/> by the
    /// handoff test, so drift is caught by a test rather than by a runtime "no transport for id" failure.
    /// </summary>
    internal const string SshTransportId = "ssh";

    private const string CostSource =
        "A process installed on a host Servyx did not rent has no provider-billed price; this provisioner does not advertise EstimatesCost.";

    /// <summary>
    /// Turns an absolute path on the target host into a <see cref="TargetPath"/>, the only form
    /// <see cref="IExecutionTarget"/>'s file operations accept.
    /// </summary>
    /// <remarks>
    /// Rooted at <c>/</c> because a provisioner's reach genuinely is the whole host — it is installing into
    /// system locations, not editing files inside one server's sandbox — and because
    /// <see cref="SftpFileChannel"/> re-prepends <c>/</c> to a <see cref="TargetPath.Value"/>, so an absolute
    /// remote path survives the round trip unchanged. Note that <see cref="SandboxedPathResolver"/> applies
    /// the <em>local</em> OS's path rules to a <em>remote</em> POSIX path (on Windows it will reject a colon,
    /// and treats <c>\</c> as a separator); that mismatch is pre-existing — <see cref="SftpFileChannel"/>
    /// already lives with it — and is why marker filenames are constrained to a conservative charset by
    /// <see cref="ServyxProcessMarker.For"/> rather than trusted to normalise identically on both platforms.
    /// </remarks>
    private static readonly SandboxedPathResolver HostPaths = new("/");

    private readonly ITransport _transport;
    private readonly string _endpoint;
    private readonly string? _credentialUrn;
    private readonly IReadOnlyDictionary<string, string> _transportOptions;
    private readonly string _markerRoot;
    private readonly string _host;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a provisioner that installs onto the host at <paramref name="endpoint"/>, reached through
    /// <paramref name="transport"/>.
    /// </summary>
    /// <param name="transport">
    /// The SSH transport. Must report <see cref="ITransport.TransportId"/> <c>"ssh"</c>. Substituted in tests;
    /// no live SSH server is required.
    /// </param>
    /// <param name="endpoint">
    /// The SSH endpoint, in <see cref="SshEndpoint"/>'s <c>[user@]host[:port]</c> form. This single value is
    /// both connected to and stamped onto every <see cref="TargetDescriptor"/> this provisioner produces.
    /// </param>
    /// <param name="credentialUrn">The <see cref="TargetDescriptor.CredentialUrn"/> to stamp; never a literal credential.</param>
    /// <param name="transportOptions">
    /// Additional <see cref="TargetDescriptor.Options"/> the SSH transport reads (<c>usernameUrn</c>,
    /// <c>passphraseUrn</c>, <c>trustPolicy</c>, <c>pinnedFingerprints</c>, <c>declaredChannels</c>). Applied
    /// before Servyx-owned option keys, so they can never override one.
    /// </param>
    /// <param name="markerRoot">
    /// The directory marker files are <em>written</em> to, and the default directory a sweep enumerates.
    /// Fixed at construction for the same reason <paramref name="endpoint"/> is: a
    /// <see cref="ProvisioningRequest"/> able to relocate where its own marker lands could place an install
    /// outside the directory the sweep covers, hiding it from the mechanism that exists to find it. A sweep
    /// may still be pointed elsewhere — <see cref="OrphanScope.MarkerDirectory"/> names the root to
    /// enumerate and <see cref="ReconcileAsync"/> honours it — because reading a different directory cannot
    /// hide anything; only writing to one can.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry and creation timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="transport"/> is not an SSH transport, <paramref name="endpoint"/> is blank or
    /// unparseable, or <paramref name="markerRoot"/> is not an absolute POSIX directory path.
    /// </exception>
    public SshProcessProvisioner(
        ITransport transport,
        string endpoint,
        string? credentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string? markerRoot = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!string.Equals(transport.TransportId, SshTransportId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The '{Id}' provisioner requires a transport with TransportId '{SshTransportId}', not '{transport.TransportId}'.",
                nameof(transport));
        }

        // Parsing here, not at connect time, is what makes a malformed endpoint a construction-time failure
        // rather than a failure discovered halfway through an install.
        var (parsed, _) = SshEndpoint.Parse(endpoint);

        _transport = transport;
        _endpoint = endpoint;
        _host = parsed.Host;
        _credentialUrn = credentialUrn;
        _transportOptions = transportOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(transportOptions, StringComparer.Ordinal);
        _markerRoot = ValidateMarkerRoot(markerRoot ?? DefaultMarkerRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: it is what
    /// <see cref="ReconcileAsync"/> depends on to find installs Servyx created but lost track of. Neither
    /// <see cref="ProvisioningCapabilities.FirewallRules"/> nor
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is claimed — see the type remarks.
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery;

    /// <summary>
    /// A single SSH host is not region-scoped, so every handle and plan this provisioner produces carries a
    /// null region rather than inventing one.
    /// </summary>
    public static string? Region => null;

    /// <summary>
    /// The directory this provisioner writes marker files to, and the directory it sweeps unless an
    /// <see cref="OrphanScope.MarkerDirectory"/> names another one.
    /// </summary>
    public string MarkerRoot => _markerRoot;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the install spec from <paramref name="request"/>'s parameters and describes
    /// the stages needed to realise it. Opens no connection and issues no command, so it cannot mutate
    /// anything. An install verb outside <see cref="SshInstallStep.AllowedVerbs"/> is rejected here, at plan
    /// time, before anything is reachable.
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
    public ProvisioningPlan BuildPlan(SshProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var markerPath = MarkerPathFor(spec);
        var stages = new List<ProvisioningStage>
        {
            new(
                "write-marker",
                Id,
                $"Write the Servyx marker file '{markerPath}' recording instance '{spec.Marker.InstanceId}', job '{spec.Marker.JobId}', " +
                $"and connector '{spec.Marker.ConnectorId}'. Written before any install verb runs, so an install that fails halfway is " +
                "still discoverable by an orphan sweep."),
        };

        for (var i = 0; i < spec.InstallSteps.Count; i++)
        {
            stages.Add(new ProvisioningStage(spec.InstallSteps[i].StageId(i), Id, spec.InstallSteps[i].Describe(spec)));
        }

        var planHash = ComputePlanHash(spec, markerPath);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.Marker.InstanceId}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: CostEstimate.Unknown(CostSource),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads back the marker file identified by <see cref="ResourceHandle.ProviderResourceId"/> — the
    /// process-shape counterpart of inspecting a container by id. A marker the host no longer has, or one
    /// whose contents no longer identify a Servyx-managed install, yields <see langword="null"/>.
    /// </remarks>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await using var session = await _transport.ConnectAsync(HostDescriptor(), ct).ConfigureAwait(false);

        var tags = await ReadMarkerAsync(session, handle.ProviderResourceId, ct).ConfigureAwait(false);
        var marker = ServyxProcessMarker.FromTags(tags);
        if (marker is null)
        {
            return null;
        }

        var rootPath = tags!.TryGetValue(ServyxProcessMarker.RootPathTag, out var recordedRoot) && !string.IsNullOrWhiteSpace(recordedRoot)
            ? recordedRoot
            : "/";

        return new ProvisionedResource(
            Handle: new ResourceHandle(Id, handle.ProviderResourceId, Region, tags),
            ConnectorId: marker.ConnectorId,
            Target: BuildTargetDescriptor(rootPath),
            Facts: BuildFacts(tags));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive, and the exact structural counterpart of the Docker provisioner's
    /// "ask the daemon for everything labelled <c>servyx.managed=true</c>". There is no daemon here, so the
    /// sweep enumerates <see cref="MarkerRoot"/> instead — independent of any Servyx-local record, so an
    /// install created but never acknowledged can still be found.
    /// </para>
    /// <para>
    /// The filename suffix narrows the listing <em>and</em> each candidate's contents are re-checked for
    /// <c>servyx.managed=true</c>. That is the same two-step the Docker sweep performs for the same reason:
    /// the first step is a cheap filter over what the host reported, the second is this process's own
    /// guarantee that nothing unmarked is ever reported as Servyx-owned and subsequently destroyed. A sweep
    /// acting on a false positive deletes someone else's install.
    /// </para>
    /// <para>
    /// <strong>The directory swept comes from the scope when the scope says so.</strong> An
    /// <see cref="OrphanScope.MarkerDirectory"/> names the root to enumerate, and this method honours it; any
    /// other shape falls back to the <see cref="MarkerRoot"/> fixed at construction. That is the point of the
    /// scope carrying a search space at all: a caller holding this provisioner and a scope can see which
    /// directory the sweep will cover, instead of the real answer being invisible adapter state. The path is
    /// validated exactly as a constructor-supplied root is — a scope is not a route around the rule that a
    /// marker root must be an absolute POSIX directory path.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="scope"/> is an <see cref="OrphanScope.MarkerDirectory"/> whose root is not an
    /// absolute POSIX directory path.
    /// </exception>
    public async Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!string.Equals(scope.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return [];
        }

        var markerRoot = MarkerRootFor(scope);

        await using var session = await _transport.ConnectAsync(HostDescriptor(), ct).ConfigureAwait(false);

        IReadOnlyList<FileEntry>? entries;
        try
        {
            entries = await session.ListDirectoryAsync(ToHostPath(markerRoot), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // An absent marker root and an empty one carry the same meaning — this host holds no
            // Servyx-managed install — so both answer with an empty sweep rather than an error. Note this
            // catch is narrow on purpose: a permission or connection failure must not be mistaken for
            // "nothing to reconcile", because that reading turns an unreadable host into a silent all-clear.
            return [];
        }

        var handles = new List<ResourceHandle>();
        foreach (var entry in entries ?? [])
        {
            if (entry.IsDirectory || !ServyxProcessMarker.IsMarkerFileName(entry.Name))
            {
                continue;
            }

            var path = $"{markerRoot}/{entry.Name}";
            var tags = await ReadMarkerAsync(session, path, ct).ConfigureAwait(false);
            if (!ServyxProcessMarker.IsManaged(tags))
            {
                continue;
            }

            handles.Add(new ResourceHandle(Id, path, Region, tags!));
        }

        return handles;
    }

    /// <summary>
    /// Returns the mutating operation that performs the install described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>: the returned operation is driven
    /// by <c>Servyx.Application</c>'s plan executor, which owns the write-ahead ledger ordering. Calling this
    /// method installs nothing on its own.
    /// </remarks>
    public IProvisioningOperation CreateOperation(SshProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ProcessInstallOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above: builds the install spec the same way
    /// <see cref="PlanAsync"/> does, via <see cref="BuildSpec"/>, so a plan preview and the operation that
    /// later realises it are always derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Removes the Servyx marker for an install this provisioner created, making
    /// <see cref="ProvisioningCapabilities.Destroy"/> a real capability rather than an advertised one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately symmetric with the Docker provisioner's
    /// <c>RemoveContainerAsync(force: true, RemoveVolumes: false)</c>: the Servyx-owned handle to the
    /// workload is destroyed, and the game's data directory is not. A provisioner that silently deleted a
    /// save directory would be the single most destructive thing in this codebase.
    /// </para>
    /// <para>
    /// The symmetry is not perfect, and pretending otherwise would be worse than saying so: removing a
    /// container also removes its writable layer, so the installed game binaries go with it (they live in a
    /// shared image). Removing a marker leaves the installed binaries in <c>dataDir</c> on the host. Freeing
    /// that disk space is a separate, explicitly-confirmed operation, not a side effect of destroy.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> if the marker was removed; <see langword="false"/> if it was already gone.</returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await using var session = await _transport.ConnectAsync(HostDescriptor(), ct).ConfigureAwait(false);
        return await RemoveMarkerAsync(session, handle.ProviderResourceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into an install spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys, chosen to mirror a game definition's <c>kind: process</c> deployment profile (see the
    /// <c>native-steamcmd</c> profile of <c>definitions/palworld-docker.yaml</c>):
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx marker tags.</description></item>
    /// <item><description><c>dataDir</c> — required, the profile's <c>${DATA_DIR}</c>.</description></item>
    /// <item><description><c>executable</c> — required, the profile's <c>executable</c> for this platform.</description></item>
    /// <item><description><c>steamcmdPath</c> — the <c>steamcmd</c> binary to invoke. Defaults to <c>steamcmd</c> on the host's PATH.</description></item>
    /// <item><description><c>install:&lt;n&gt;:verb</c> — the nth install entry's verb; <c>n</c> fixes the order.</description></item>
    /// <item><description><c>install:&lt;n&gt;:&lt;field&gt;</c> — that entry's fields (<c>appId</c>, <c>validate</c>, <c>path</c>).</description></item>
    /// <item><description><c>env:&lt;NAME&gt;</c> — an environment variable applied to every install command.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra marker tag; can never shadow a mandatory Servyx tag.</description></item>
    /// </list>
    /// A key-per-item shape is used rather than one delimited string so no separator can ever collide with a
    /// path or a value containing a colon: the structured part is always the <em>key</em>, and every value is
    /// opaque text.
    /// <para>
    /// There is deliberately no <c>endpoint</c> or <c>markerRoot</c> key. Both are fixed at construction,
    /// because both must agree with values the provisioner uses outside the scope of any single request — see
    /// the constructor's remarks. Note the asymmetry with <see cref="ReconcileAsync"/>, which <em>does</em>
    /// accept a caller-supplied marker root: a request writes, and a write to an unswept directory is an
    /// install nobody can find again; a sweep only reads.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A required parameter is missing, or an install entry names a verb outside the allowlist.</exception>
    public static SshProcessSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var marker = ServyxProcessMarker.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var installFields = new SortedDictionary<int, Dictionary<string, string>>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("install:", StringComparison.Ordinal))
            {
                var (index, field) = ParseInstallKey(pair.Key);
                if (!installFields.TryGetValue(index, out var fields))
                {
                    fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    installFields[index] = fields;
                }

                fields[field] = pair.Value;
            }
            else if (pair.Key.StartsWith("env:", StringComparison.Ordinal))
            {
                environment[pair.Key["env:".Length..]] = pair.Value;
            }
            else if (pair.Key.StartsWith("tag:", StringComparison.Ordinal))
            {
                extraTags[pair.Key["tag:".Length..]] = pair.Value;
            }
        }

        var steps = new List<SshInstallStep>();
        foreach (var (index, fields) in installFields)
        {
            if (!fields.TryGetValue("verb", out var verb) || string.IsNullOrWhiteSpace(verb))
            {
                throw new ArgumentException(
                    $"Install entry {index} has no 'verb'. Every entry needs an 'install:{index}:verb' parameter.",
                    nameof(request));
            }

            steps.Add(SshProcessSpec.Parse(verb, fields));
        }

        return new SshProcessSpec(Required(parameters, "dataDir"), Required(parameters, "executable"), marker)
        {
            InstallSteps = steps,
            Environment = environment,
            AdditionalTags = extraTags,
            SteamCmdPath = parameters.TryGetValue("steamcmdPath", out var steamCmd) && !string.IsNullOrWhiteSpace(steamCmd)
                ? steamCmd
                : "steamcmd",
        };
    }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for the host. This is the whole handoff: the value returned
    /// here is what <see cref="SshTransport.ProbeAsync"/> and <see cref="SshTransport.ConnectAsync"/> consume,
    /// with no adapter in between — the option keys are exactly the ones that transport already reads.
    /// </summary>
    internal TargetDescriptor BuildTargetDescriptor(string rootPath)
    {
        // Caller-supplied options first, Servyx-owned keys last, so an option can never shadow one — the same
        // ordering rule ServyxProcessMarker.ToTags applies to tags.
        var options = new Dictionary<string, string>(_transportOptions, StringComparer.Ordinal)
        {
            ["rootPath"] = rootPath,
        };

        return new TargetDescriptor(
            TransportId: SshTransportId,
            Endpoint: _endpoint,
            CredentialUrn: _credentialUrn,
            DockerContext: null,
            Options: options);
    }

    /// <summary>The descriptor used to reach the host itself, for operations not scoped to one install's data directory.</summary>
    private TargetDescriptor HostDescriptor() => BuildTargetDescriptor("/");

    /// <summary>The absolute marker-file path for <paramref name="spec"/> under this provisioner's marker root.</summary>
    internal string MarkerPathFor(SshProcessSpec spec) => ServyxProcessMarker.PathFor(_markerRoot, spec.Marker.InstanceId);

    private static TargetPath ToHostPath(string absolutePath) => HostPaths.Resolve(absolutePath);

    /// <summary>
    /// The marker root a sweep under <paramref name="scope"/> covers: the scope's own root when it names
    /// one, otherwise the root fixed at construction.
    /// </summary>
    /// <remarks>
    /// Only sweeps are scope-directed. Installs keep writing to the constructed <see cref="MarkerRoot"/>,
    /// because a request able to relocate where its own marker lands could place an install outside the
    /// directory the sweep enumerates — hiding it from the very mechanism that exists to find it. Reading a
    /// different directory is safe in a way writing to one is not.
    /// </remarks>
    private string MarkerRootFor(OrphanScope scope) => scope switch
    {
        OrphanScope.MarkerDirectory directory => ValidateMarkerRoot(directory.MarkerRoot, nameof(scope)),
        _ => _markerRoot,
    };

    /// <summary>Reads and parses a marker file, returning <see langword="null"/> if it is absent or unreadable.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> ReadMarkerAsync(
        IExecutionTarget session,
        string markerPath,
        CancellationToken ct)
    {
        TargetPath path;
        try
        {
            path = ToHostPath(markerPath);
        }
        catch (PathEscapesSandboxException)
        {
            return null;
        }

        if (!await session.ExistsAsync(path, ct).ConfigureAwait(false))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        try
        {
            await using var stream = await session.OpenReadAsync(path, ct).ConfigureAwait(false);
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Removed between the existence check and the open. Same answer as "never existed".
            return null;
        }

        return ServyxProcessMarker.Deserialize(buffer.ToArray());
    }

    private static async Task<bool> RemoveMarkerAsync(IExecutionTarget session, string markerPath, CancellationToken ct)
    {
        var path = ToHostPath(markerPath);
        if (!await session.ExistsAsync(path, ct).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await session.DeleteAsync(path, ct).ConfigureAwait(false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private ResourceFacts BuildFacts(IReadOnlyDictionary<string, string>? tags)
    {
        var createdAt = tags is not null
            && tags.TryGetValue(ServyxProcessMarker.CreatedAtTag, out var recorded)
            && DateTimeOffset.TryParse(recorded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;

        // The endpoint host is where Servyx reaches the machine, but Servyx cannot tell whether that address
        // is publicly routable — it is just as likely to be a VPN or LAN name. Reporting it as PrivateAddress
        // and leaving PublicAddress null is the honest reading; claiming a public address Servyx has not
        // verified would be a fabricated fact of exactly the kind CostEstimate.Unknown exists to avoid.
        return new ResourceFacts(
            PublicAddress: null,
            PrivateAddress: _host,
            Cost: CostEstimate.Unknown(CostSource),
            CreatedAt: createdAt);
    }

    private string ComputePlanHash(SshProcessSpec spec, string markerPath)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.DataDirectory).Append('\n');
        builder.Append(spec.Executable).Append('\n');
        builder.Append(spec.SteamCmdPath).Append('\n');
        builder.Append(markerPath).Append('\n');

        for (var i = 0; i < spec.InstallSteps.Count; i++)
        {
            var command = spec.InstallSteps[i].ToCommand(spec);
            builder.Append(CultureInfo.InvariantCulture, $"step {i} {spec.InstallSteps[i].Verb} {command.Executable}");
            foreach (var argument in command.Arguments)
            {
                builder.Append(CultureInfo.InvariantCulture, $" {argument}");
            }

            builder.Append('\n');
        }

        foreach (var entry in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"env {entry.Key}={entry.Value}\n");
        }

        foreach (var tag in spec.Marker.ToTags(spec.AdditionalTags).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static (int Index, string Field) ParseInstallKey(string key)
    {
        var remainder = key["install:".Length..];
        var separator = remainder.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ArgumentException($"'{key}' is not a valid 'install:<n>:<field>' provisioning parameter key.", nameof(key));
        }

        if (!int.TryParse(remainder[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw new ArgumentException($"'{key}' does not carry a numeric install index.", nameof(key));
        }

        var field = remainder[(separator + 1)..];
        if (field.Length == 0)
        {
            throw new ArgumentException($"'{key}' names no install field.", nameof(key));
        }

        return (index, field);
    }

    /// <summary>
    /// The single gate every marker root passes through, whether it arrived from the constructor or from an
    /// <see cref="OrphanScope.MarkerDirectory"/>.
    /// </summary>
    /// <param name="markerRoot">The candidate root.</param>
    /// <param name="paramName">
    /// The parameter to blame in the thrown exception, so a bad root supplied through a scope is reported
    /// against <c>scope</c> rather than against a constructor argument the caller never passed.
    /// </param>
    private static string ValidateMarkerRoot(string markerRoot, string paramName = "markerRoot")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerRoot, paramName);

        if (!markerRoot.StartsWith('/') || markerRoot.Contains('\\', StringComparison.Ordinal) || markerRoot.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Marker root '{markerRoot}' must be an absolute POSIX directory path containing no backslash or colon.",
                paramName);
        }

        return markerRoot.Length > 1 ? markerRoot.TrimEnd('/') : markerRoot;
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
    /// provisioner so it — and only it — can reach the connection the provisioner is configured with.
    /// </summary>
    private sealed class ProcessInstallOperation : IProvisioningOperation
    {
        private readonly SshProcessProvisioner _owner;
        private readonly SshProcessSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;
        private readonly string _markerPath;

        internal ProcessInstallOperation(SshProcessProvisioner owner, SshProcessSpec spec)
        {
            _owner = owner;
            _spec = spec;
            _markerPath = owner.MarkerPathFor(spec);

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in
            // order to commit them to the write-ahead ledger — so they must be the same values that later
            // reach the host, not a set recomputed with a different timestamp.
            _tags = spec.Marker.ToTags(new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
            {
                [ServyxProcessMarker.RootPathTag] = spec.DataDirectory,
                [ServyxProcessMarker.ProvisionerIdTag] = Id,
                [ServyxProcessMarker.ExecutableTag] = spec.Executable,
                [ServyxProcessMarker.CreatedAtTag] = owner._timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            });
        }

        public string ProvisionerId => Id;

        public string? Region => SshProcessProvisioner.Region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Installs the workload, marker first.
        /// </summary>
        /// <remarks>
        /// The ordering is the whole point and is not incidental: the marker is written before any install
        /// verb runs. A container gets its labels from the same atomic call that creates it, so it is never
        /// unlabelled; a process install has no such atomicity, so Servyx buys the equivalent guarantee by
        /// ordering — after this method's second step, every subsequent failure leaves something on the host
        /// that <see cref="ReconcileAsync"/> can find.
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            // The descriptor connected with is the same instance handed back below, so the host installed on
            // and the host recorded cannot differ.
            var target = _owner.BuildTargetDescriptor(_spec.DataDirectory);
            await using var session = await _owner._transport.ConnectAsync(target, ct).ConfigureAwait(false);

            await RunAsync(session, new EnsureDirectoryInstallStep(_owner._markerRoot).ToCommand(_spec), "ensure-marker-root", ct)
                .ConfigureAwait(false);

            await using (var content = new MemoryStream(ServyxProcessMarker.Serialize(_tags), writable: false))
            {
                await session.WriteFileAsync(ToHostPath(_markerPath), content, new FileWriteOptions(null), ct).ConfigureAwait(false);
            }

            for (var i = 0; i < _spec.InstallSteps.Count; i++)
            {
                var step = _spec.InstallSteps[i];
                await RunAsync(session, step.ToCommand(_spec), step.StageId(i), ct).ConfigureAwait(false);
            }

            return new ProvisionedResource(
                Handle: new ResourceHandle(Id, _markerPath, Region, _tags),
                ConnectorId: _spec.Marker.ConnectorId,
                Target: target,
                Facts: _owner.BuildFacts(_tags));
        }

        /// <summary>
        /// Removes the marker this operation may have written.
        /// </summary>
        /// <remarks>
        /// The marker path is deterministic from the spec, so this does not depend on
        /// <see cref="CreateAsync"/> having reported success — mirroring the Docker operation's refusal to
        /// assume nothing was created just because no id came back. It deliberately does not remove
        /// <c>dataDir</c>: see the remarks on <see cref="DestroyAsync"/>.
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            await using var session = await _owner._transport
                .ConnectAsync(_owner.HostDescriptor(), ct)
                .ConfigureAwait(false);

            await RemoveMarkerAsync(session, _markerPath, ct).ConfigureAwait(false);
        }

        private static async Task RunAsync(IExecutionTarget session, CommandSpec command, string stageId, CancellationToken ct)
        {
            var result = await session.ExecuteAsync(command, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Install stage '{stageId}' ('{command.Executable}') exited with code {result.ExitCode}: {result.StandardError}"));
            }
        }
    }
}
