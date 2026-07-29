using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that installs a game server as a plain process on the machine Servyx is
/// running on, and hands the result back as a <see cref="TargetDescriptor"/> the existing
/// <see cref="LocalProcessTransport"/> consumes directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The local half of "shape H".</strong> <c>DockerContainerProvisioner</c> showed a container install
/// fits behind <see cref="IProvisioner"/>; <c>SshProcessProvisioner</c> showed a remote host-process install
/// does too. This is the same process shape with the network removed, which is a sharper test of the seam than
/// it sounds: everything the SSH adapter can leave to a POSIX host (path syntax, <c>mkdir</c>, an SFTP channel
/// that quietly re-roots every path at <c>/</c>) has to be answered here by the local OS, whichever one that
/// is.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> opens no session and issues no command at all.
/// A plan is pure computation over the request — there is no call to audit.
/// </para>
/// <para>
/// <strong>Mutation lives outside this type's <see cref="IProvisioner"/> surface.</strong> Installing is
/// reachable only through <see cref="CreateOperation(LocalProcessSpec)"/>, which returns an
/// <see cref="IProvisioningOperation"/> for <c>Servyx.Application</c>'s plan executor to drive.
/// </para>
/// <para>
/// <strong>Steps are argv arrays, never shell strings.</strong> The one install verb that runs a program
/// becomes a <see cref="CommandSpec"/> with a separate <see cref="CommandSpec.Executable"/> and
/// <see cref="CommandSpec.Arguments"/>, which <see cref="LocalExecutionTarget"/> passes to
/// <c>ProcessStartInfo.ArgumentList</c> — there is no command line for a hostile app id or path to escape out
/// of. The other verb runs no program at all; see the remarks on <see cref="LocalInstallStep"/>.
/// </para>
/// <para>
/// <strong>No cost estimation, no firewall rules, deliberately.</strong> <see cref="Capabilities"/> omits
/// <see cref="ProvisioningCapabilities.EstimatesCost"/> — a process on a machine Servyx did not rent has no
/// provider-billed price, and <see cref="CostEstimate.Unknown"/> is the honest answer — and
/// <see cref="ProvisioningCapabilities.FirewallRules"/>, because this provisioner does not touch the machine's
/// firewall and advertising the capability would let a caller believe a port had been opened when nothing had.
/// </para>
/// <para>
/// <strong>Maintenance and update execution live in their own files.</strong> This type also implements
/// <see cref="IMaintainer"/> (see <c>LocalProcessProvisioner.Maintenance.cs</c>) and
/// <see cref="IUpdateApplier"/> (see <c>LocalProcessProvisioner.Update.cs</c>). The split is the same one the
/// EC2, Azure and DigitalOcean adapters draw, and for the same reason: the read-only half — drift detection and
/// update planning — is separated on disk from the one file in this assembly that can change an install which
/// already exists, so a reviewer can read the mutating surface on its own.
/// </para>
/// </remarks>
public sealed partial class LocalProcessProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "local-process";

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this
    /// provisioner produces. Asserted equal to <see cref="LocalProcessTransport.TransportId"/> by the handoff
    /// test, so drift is caught by a test rather than by a runtime "no transport for id" failure.
    /// </summary>
    internal const string LocalTransportId = LocalProcessTransport.Id;

    private const string CostSource =
        "A process installed on the machine Servyx runs on has no provider-billed price; this provisioner does not advertise EstimatesCost.";

    private readonly ITransport _transport;
    private readonly string _machineId;
    private readonly string _endpoint;
    private readonly string? _credentialUrn;
    private readonly IReadOnlyDictionary<string, string> _transportOptions;
    private readonly string _markerRoot;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a provisioner that installs onto this machine, reached through <paramref name="transport"/>.
    /// </summary>
    /// <param name="transport">
    /// The local-process transport. Must report <see cref="ITransport.TransportId"/> <c>"local"</c>.
    /// Substituted in tests.
    /// </param>
    /// <param name="machineId">
    /// A stable name for the machine, stamped into every <see cref="TargetDescriptor.Endpoint"/> this
    /// provisioner produces as <c>local://{machineId}</c>. Defaults to <see cref="Environment.MachineName"/>.
    /// It is an identifier, never something dialled: a local target is reached by opening files and starting
    /// processes, not by connecting to an address.
    /// </param>
    /// <param name="credentialUrn">The <see cref="TargetDescriptor.CredentialUrn"/> to stamp; never a literal credential.</param>
    /// <param name="transportOptions">
    /// Additional <see cref="TargetDescriptor.Options"/>. Applied before Servyx-owned option keys, so they can
    /// never override one.
    /// </param>
    /// <param name="markerRoot">
    /// The directory marker files are <em>written</em> to, and the default directory a sweep enumerates. Fixed
    /// at construction because a <see cref="ProvisioningRequest"/> able to relocate where its own marker lands
    /// could place an install outside the directory the sweep covers, hiding it from the mechanism that exists
    /// to find it. A sweep may still be pointed elsewhere — <see cref="OrphanScope.MarkerDirectory"/> names the
    /// root to enumerate and <see cref="ReconcileAsync"/> honours it — because reading a different directory
    /// cannot hide anything; only writing to one can. Defaults to <see cref="DefaultMarkerRoot"/>.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry and creation timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="transport"/> is not a local transport, or <paramref name="markerRoot"/> is not a
    /// fully-qualified directory path on this machine.
    /// </exception>
    public LocalProcessProvisioner(
        ITransport transport,
        string? machineId = null,
        string? credentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string? markerRoot = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (!string.Equals(transport.TransportId, LocalTransportId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The '{Id}' provisioner requires a transport with TransportId '{LocalTransportId}', not '{transport.TransportId}'.",
                nameof(transport));
        }

        _transport = transport;
        _machineId = string.IsNullOrWhiteSpace(machineId) ? Environment.MachineName : machineId;
        _endpoint = $"local://{_machineId}";
        _credentialUrn = credentialUrn;
        _transportOptions = transportOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(transportOptions, StringComparer.Ordinal);
        _markerRoot = ValidateMarkerRoot(markerRoot ?? DefaultMarkerRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The directory marker files live under when no other root is configured: <c>/var/lib/servyx/instances</c>
    /// on Unix, and <c>%ProgramData%\Servyx\instances</c> on Windows.
    /// </summary>
    /// <remarks>
    /// Computed rather than a constant, because there is no single string that is a sane system-wide state
    /// directory on both platforms — the SSH adapter can hard-code a POSIX path only because its target is
    /// POSIX by definition.
    /// </remarks>
    public static string DefaultMarkerRoot { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Servyx", "instances")
        : "/var/lib/servyx/instances";

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: it is what
    /// <see cref="ReconcileAsync"/> depends on to find installs Servyx created but lost track of. See the type
    /// remarks for why neither <see cref="ProvisioningCapabilities.FirewallRules"/> nor
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is claimed.
    /// <para>
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/> is present and
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/> is deliberately absent — the same pairing the SSH
    /// process adapter claims, and the opposite of the Docker adapter's. Re-running the install verbs against an
    /// existing install directory mutates the install without discarding its provider identity (the marker path
    /// never changes), so every update this adapter can plan is in place; there is no recreate story to
    /// advertise. <see cref="ProvisioningCapabilities.DetectDrift"/> is claimed because
    /// <see cref="DetectDriftAsync"/> reads the live filesystem, not merely the record.
    /// </para>
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.UpdateInPlace
        | ProvisioningCapabilities.DetectDrift;

    /// <summary>
    /// A single machine is not region-scoped, so every handle and plan this provisioner produces carries a
    /// null region rather than inventing one.
    /// </summary>
    public static string? Region => null;

    /// <summary>
    /// The directory this provisioner writes marker files to, and the directory it sweeps unless an
    /// <see cref="OrphanScope.MarkerDirectory"/> names another one.
    /// </summary>
    public string MarkerRoot => _markerRoot;

    /// <summary>The machine identifier stamped into every descriptor this provisioner produces.</summary>
    public string MachineId => _machineId;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the install spec from <paramref name="request"/>'s parameters and describes the
    /// stages needed to realise it. Opens no session and issues no command, so it cannot mutate anything. An
    /// install verb outside <see cref="LocalInstallStep.AllowedVerbs"/> — and a data directory or ensure-dir
    /// path that is not fully qualified — is rejected here, at plan time, before anything is reachable.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(BuildPlan(BuildSpec(request)));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed the
    /// spec themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    public ProvisioningPlan BuildPlan(LocalProcessSpec spec)
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
    /// Reads back the marker file identified by <see cref="ResourceHandle.ProviderResourceId"/>. A marker the
    /// machine no longer has, or one whose contents no longer identify a Servyx-managed install, yields
    /// <see langword="null"/>.
    /// </remarks>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await using var session = await _transport
            .ConnectAsync(MachineDescriptor(handle.ProviderResourceId), ct)
            .ConfigureAwait(false);

        var tags = await ReadMarkerAsync(session, handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (ServyxProcessMarker.FromTags(tags) is not { } marker)
        {
            return null;
        }

        var rootPath = tags!.TryGetValue(ServyxProcessMarker.RootPathTag, out var recordedRoot) && !string.IsNullOrWhiteSpace(recordedRoot)
            ? recordedRoot
            : MachineRootOf(handle.ProviderResourceId);

        return new ProvisionedResource(
            Handle: new ResourceHandle(Id, handle.ProviderResourceId, Region, tags),
            ConnectorId: marker.ConnectorId,
            Target: BuildTargetDescriptor(rootPath),
            Facts: BuildFacts(tags));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive. There is no daemon to ask, so the sweep enumerates <see cref="MarkerRoot"/>
    /// — independent of any Servyx-local record, so an install created but never acknowledged can still be
    /// found.
    /// </para>
    /// <para>
    /// The filename suffix narrows the listing <em>and</em> each candidate's contents are re-checked for
    /// <c>servyx.managed=true</c>: the first step is a cheap filter over what the directory reported, the
    /// second is this process's own guarantee that nothing unmarked is ever reported as Servyx-owned and
    /// subsequently destroyed. A sweep acting on a false positive deletes someone else's install.
    /// </para>
    /// <para>
    /// <strong>Scope handling matches <c>SshProcessProvisioner</c> exactly, including the parts that are not
    /// obvious.</strong> A scope naming another provisioner reports nothing without opening a session. An
    /// <see cref="OrphanScope.MarkerDirectory"/> names the root to enumerate and is honoured, after passing the
    /// same validation a constructor-supplied root does — a scope is not a route around the rule. Every other
    /// scope shape, <see cref="OrphanScope.ProviderWide"/> included, falls back to the constructed
    /// <see cref="MarkerRoot"/> rather than being refused: for a marker-backed adapter "sweep everything this
    /// provisioner owns" and "sweep the directory it writes to" are the same request.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="scope"/> is an <see cref="OrphanScope.MarkerDirectory"/> whose root is not a
    /// fully-qualified directory path on this machine.
    /// </exception>
    public async Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!string.Equals(scope.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return [];
        }

        var markerRoot = MarkerRootFor(scope);

        await using var session = await _transport.ConnectAsync(MachineDescriptor(markerRoot), ct).ConfigureAwait(false);

        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await session.ListDirectoryAsync(ToMachinePath(markerRoot), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // An absent marker root and an empty one carry the same meaning — this machine holds no
            // Servyx-managed install — so both answer with an empty sweep rather than an error. Note this
            // catch is narrow on purpose: a permission failure must not be mistaken for "nothing to
            // reconcile", because that reading turns an unreadable directory into a silent all-clear.
            return [];
        }

        var handles = new List<ResourceHandle>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || !ServyxProcessMarker.IsMarkerFileName(entry.Name))
            {
                continue;
            }

            var path = Path.Combine(markerRoot, entry.Name);
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
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>: the returned operation is driven by
    /// <c>Servyx.Application</c>'s plan executor, which owns the write-ahead ledger ordering. Calling this
    /// method installs nothing on its own.
    /// </remarks>
    public IProvisioningOperation CreateOperation(LocalProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new LocalProcessInstallOperation(this, spec);
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
    /// Deliberately leaves the data directory alone, exactly as the SSH and Docker provisioners do: the
    /// Servyx-owned handle to the workload is destroyed, and the game's data is not. Freeing that disk space is
    /// a separate, explicitly-confirmed operation, never a side effect of destroy.
    /// </remarks>
    /// <returns><see langword="true"/> if the marker was removed; <see langword="false"/> if it was already gone.</returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await using var session = await _transport
            .ConnectAsync(MachineDescriptor(handle.ProviderResourceId), ct)
            .ConfigureAwait(false);

        return await RemoveMarkerAsync(session, handle.ProviderResourceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into an install spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys, deliberately identical to the SSH adapter's so one definition's <c>kind: process</c>
    /// deployment profile can be provisioned either locally or over SSH without rewriting its parameters:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx marker tags.</description></item>
    /// <item><description><c>dataDir</c> — required, the profile's <c>${DATA_DIR}</c>. Must be fully qualified on this machine.</description></item>
    /// <item><description><c>executable</c> — required, the profile's <c>executable</c> for this platform.</description></item>
    /// <item><description><c>steamcmdPath</c> — the <c>steamcmd</c> binary to invoke. Defaults to <c>steamcmd</c> on this machine's PATH.</description></item>
    /// <item><description><c>install:&lt;n&gt;:verb</c> — the nth install entry's verb; <c>n</c> fixes the order.</description></item>
    /// <item><description><c>install:&lt;n&gt;:&lt;field&gt;</c> — that entry's fields (<c>appId</c>, <c>validate</c>, <c>path</c>).</description></item>
    /// <item><description><c>env:&lt;NAME&gt;</c> — an environment variable applied to every install command.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra marker tag; can never shadow a mandatory Servyx tag.</description></item>
    /// </list>
    /// A key-per-item shape is used rather than one delimited string so no separator can ever collide with a
    /// path or a value containing a colon: the structured part is always the <em>key</em>, and every value is
    /// opaque text. There is deliberately no <c>markerRoot</c> key — see the constructor's remarks.
    /// </remarks>
    /// <exception cref="ArgumentException">A required parameter is missing, or an install entry names a verb outside the allowlist.</exception>
    public static LocalProcessSpec BuildSpec(ProvisioningRequest request)
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

        var steps = new List<LocalInstallStep>();
        foreach (var (index, fields) in installFields)
        {
            if (!fields.TryGetValue("verb", out var verb) || string.IsNullOrWhiteSpace(verb))
            {
                throw new ArgumentException(
                    $"Install entry {index} has no 'verb'. Every entry needs an 'install:{index}:verb' parameter.",
                    nameof(request));
            }

            steps.Add(LocalProcessSpec.Parse(verb, fields));
        }

        return new LocalProcessSpec(Required(parameters, "dataDir"), Required(parameters, "executable"), marker)
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
    /// Builds the <see cref="TargetDescriptor"/> for a session rooted at <paramref name="rootPath"/>. This is
    /// the whole handoff: the value returned here is what <see cref="LocalProcessTransport.ProbeAsync"/> and
    /// <see cref="LocalProcessTransport.ConnectAsync"/> consume, with no adapter in between — the option key is
    /// exactly the one that transport already reads.
    /// </summary>
    internal TargetDescriptor BuildTargetDescriptor(string rootPath)
    {
        // Caller-supplied options first, Servyx-owned keys last, so an option can never shadow one — the same
        // ordering rule ServyxTagKeys.Build applies to tags.
        var options = new Dictionary<string, string>(_transportOptions, StringComparer.Ordinal)
        {
            [LocalProcessTransport.RootPathOption] = rootPath,
        };

        return new TargetDescriptor(
            TransportId: LocalTransportId,
            Endpoint: _endpoint,
            CredentialUrn: _credentialUrn,
            DockerContext: null,
            Options: options);
    }

    /// <summary>The absolute marker-file path for <paramref name="spec"/> under this provisioner's marker root.</summary>
    internal string MarkerPathFor(LocalProcessSpec spec) => ServyxProcessMarker.PathFor(_markerRoot, spec.Marker.InstanceId);

    /// <summary>
    /// The descriptor used to reach a path that is not inside one install's data directory — a marker file,
    /// or the marker root itself.
    /// </summary>
    /// <remarks>
    /// <strong>This is where a local target is stricter than the SSH one, and needs to be.</strong>
    /// <c>SftpFileChannel</c> re-prepends <c>/</c> to every <see cref="TargetPath"/>, so the SSH provisioner
    /// can write a marker under <c>/var/lib/servyx</c> through a session whose declared root is
    /// <c>/opt/palworld</c> — the root is a naming convention there, not a fence.
    /// <see cref="LocalExecutionTarget"/> actually enforces its root, so reaching a marker outside the data
    /// directory requires a session rooted at the volume/filesystem root that contains it. Hence two sessions
    /// during an install rather than one; the alternative would have been to weaken the sandbox, which is not a
    /// trade worth making for one fewer object.
    /// </remarks>
    private TargetDescriptor MachineDescriptor(string forPath) => BuildTargetDescriptor(MachineRootOf(forPath));

    /// <summary>The volume/filesystem root containing <paramref name="absolutePath"/> (<c>/</c>, or e.g. <c>C:\</c>).</summary>
    private static string MachineRootOf(string absolutePath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(absolutePath));
        return string.IsNullOrEmpty(root) ? Path.DirectorySeparatorChar.ToString() : root;
    }

    /// <summary>
    /// Turns an absolute path on this machine into the <see cref="TargetPath"/> a session rooted at that
    /// path's volume root accepts. Uses <see cref="SandboxedPathResolver"/> — the sanctioned factory — rather
    /// than constructing a path value by hand.
    /// </summary>
    private static TargetPath ToMachinePath(string absolutePath) =>
        new SandboxedPathResolver(MachineRootOf(absolutePath)).Resolve(absolutePath);

    /// <summary>
    /// The marker root a sweep under <paramref name="scope"/> covers: the scope's own root when it names one,
    /// otherwise the root fixed at construction. Only sweeps are scope-directed; installs keep writing to the
    /// constructed <see cref="MarkerRoot"/>.
    /// </summary>
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
            path = ToMachinePath(markerPath);
        }
        catch (Exception ex) when (ex is PathEscapesSandboxException or ArgumentException)
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
        var path = ToMachinePath(markerPath);
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

        // The machine name is how Servyx refers to this machine, not an address it verified as routable.
        // Reporting it as PrivateAddress and leaving PublicAddress null is the honest reading; claiming a
        // public address Servyx has not verified would be a fabricated fact of exactly the kind
        // CostEstimate.Unknown exists to avoid.
        return new ResourceFacts(
            PublicAddress: null,
            PrivateAddress: _machineId,
            Cost: CostEstimate.Unknown(CostSource),
            CreatedAt: createdAt);
    }

    private string ComputePlanHash(LocalProcessSpec spec, string markerPath)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.DataDirectory).Append('\n');
        builder.Append(spec.Executable).Append('\n');
        builder.Append(spec.SteamCmdPath).Append('\n');
        builder.Append(markerPath).Append('\n');

        for (var i = 0; i < spec.InstallSteps.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"step {i} {spec.InstallSteps[i].HashInput(spec)}\n");
        }

        foreach (var entry in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"env {entry.Key}={entry.Value}\n");
        }

        foreach (var tag in spec.Marker.ToTags(spec.AdditionalTags).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
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

        if (!Path.IsPathFullyQualified(markerRoot))
        {
            throw new ArgumentException(
                $"Marker root '{markerRoot}' must be a fully-qualified directory path on this machine.",
                paramName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(markerRoot));
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
    /// provisioner so it — and only it — can reach the transport the provisioner is configured with.
    /// </summary>
    private sealed class LocalProcessInstallOperation : IProvisioningOperation
    {
        private readonly LocalProcessProvisioner _owner;
        private readonly LocalProcessSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;
        private readonly string _markerPath;

        internal LocalProcessInstallOperation(LocalProcessProvisioner owner, LocalProcessSpec spec)
        {
            _owner = owner;
            _spec = spec;
            _markerPath = owner.MarkerPathFor(spec);

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in
            // order to commit them to the write-ahead ledger — so they must be the same values that later
            // reach the machine, not a set recomputed with a different timestamp.
            _tags = spec.Marker.ToTags(new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
            {
                [ServyxProcessMarker.RootPathTag] = spec.DataDirectory,
                [ServyxProcessMarker.ProvisionerIdTag] = Id,
                [ServyxProcessMarker.ExecutableTag] = spec.Executable,
                [ServyxProcessMarker.CreatedAtTag] = owner._timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            });
        }

        public string ProvisionerId => Id;

        public string? Region => LocalProcessProvisioner.Region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Installs the workload, marker first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ordering is the whole point and is not incidental: the marker is written before any install verb
        /// runs. A container gets its labels from the same atomic call that creates it, so it is never
        /// unlabelled; a process install has no such atomicity, so Servyx buys the equivalent guarantee by
        /// ordering — after the marker write, every subsequent failure leaves something on the machine that
        /// <see cref="ReconcileAsync"/> can find.
        /// </para>
        /// <para>
        /// The data directory is created before anything else, which the SSH operation does not do. It has to
        /// be: a local command runs with the session's root as its working directory, and a process cannot be
        /// started in a directory that does not exist. Over SSH the equivalent failure is deferred to the
        /// remote shell, so the SSH adapter never had to confront it.
        /// </para>
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            // The descriptor connected with is the same instance handed back below, so the machine installed on
            // and the machine recorded cannot differ.
            var target = _owner.BuildTargetDescriptor(_spec.DataDirectory);

            Directory.CreateDirectory(_spec.DataDirectory);
            Directory.CreateDirectory(_owner._markerRoot);

            await using (var markerSession = await _owner._transport
                .ConnectAsync(_owner.MachineDescriptor(_markerPath), ct)
                .ConfigureAwait(false))
            {
                await using var content = new MemoryStream(ServyxProcessMarker.Serialize(_tags), writable: false);
                await markerSession
                    .WriteFileAsync(ToMachinePath(_markerPath), content, new FileWriteOptions(null), ct)
                    .ConfigureAwait(false);
            }

            await using (var session = await _owner._transport.ConnectAsync(target, ct).ConfigureAwait(false))
            {
                for (var i = 0; i < _spec.InstallSteps.Count; i++)
                {
                    await RunInstallStepAsync(session, _spec, _spec.InstallSteps[i], i, ct).ConfigureAwait(false);
                }
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
        /// The marker path is deterministic from the spec, so this does not depend on <see cref="CreateAsync"/>
        /// having reported success. It deliberately does not remove the data directory: see the remarks on
        /// <see cref="DestroyAsync"/>.
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            await using var session = await _owner._transport
                .ConnectAsync(_owner.MachineDescriptor(_markerPath), ct)
                .ConfigureAwait(false);

            await RemoveMarkerAsync(session, _markerPath, ct).ConfigureAwait(false);
        }

    }
}
