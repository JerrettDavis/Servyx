using Servyx.Domain.Provisioning;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="IProvisioner"/> that plans from a canned <see cref="ProvisioningPlan"/> and touches
/// nothing, mirroring <c>DockerContainerProvisioner</c>'s shape: <see cref="CreateOperation"/> only ever
/// hands back an inert <see cref="RecordingProvisioningOperation"/> — the actual provider-mutating call,
/// <see cref="IProvisioningOperation.CreateAsync"/>, is never reachable through this type or the interface
/// it implements.
/// </summary>
/// <remarks>
/// The call counters exist so a UI test can assert the negative that matters — that rendering a plan
/// preview never reached a create path — rather than merely asserting that stages appeared on screen.
/// </remarks>
public sealed class FakeProvisioner : IProvisioner
{
    private readonly Func<ProvisioningRequest, ProvisioningPlan> _planFactory;

    public FakeProvisioner(
        string provisionerId,
        ProvisioningCapabilities capabilities,
        ProvisioningPlan plan,
        RecordingProvisioningOperation? operation = null)
        : this(provisionerId, capabilities, _ => plan, operation)
    {
    }

    /// <summary>
    /// Plans through <paramref name="planFactory"/>, so a test can make the plan hash depend on the request
    /// exactly as a real provisioner's does — which is what makes a genuine stale-plan test possible rather
    /// than a simulated one.
    /// </summary>
    public FakeProvisioner(
        string provisionerId,
        ProvisioningCapabilities capabilities,
        Func<ProvisioningRequest, ProvisioningPlan> planFactory,
        RecordingProvisioningOperation? operation = null)
    {
        ProvisionerId = provisionerId;
        Capabilities = capabilities;
        _planFactory = planFactory;
        Operation = operation ?? new RecordingProvisioningOperation();
    }

    public string ProvisionerId { get; }

    public ProvisioningCapabilities Capabilities { get; }

    /// <summary>How many times a plan was requested.</summary>
    public int PlanCalls { get; private set; }

    /// <summary>How many times the mutating entry point was reached. Must stay zero during a preview.</summary>
    public int CreateOperationCalls { get; private set; }

    /// <summary>The single operation this fake ever hands out, so its own call count can be inspected.</summary>
    public RecordingProvisioningOperation Operation { get; }

    /// <summary>The last request this fake was asked to plan for.</summary>
    public ProvisioningRequest? LastRequest { get; private set; }

    /// <summary>The last request this fake was asked to build an operation for.</summary>
    public ProvisioningRequest? LastCreateOperationRequest { get; private set; }

    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        PlanCalls++;
        LastRequest = request;
        return Task.FromResult(_planFactory(request));
    }

    public Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
        => Task.FromResult<ProvisionedResource?>(null);

    public Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ResourceHandle>>([]);

    /// <summary>
    /// Hands back the single canned <see cref="RecordingProvisioningOperation"/>, ignoring
    /// <paramref name="request"/>. Building the operation is not itself a provider call — only driving the
    /// operation's own <see cref="IProvisioningOperation.CreateAsync"/> is, and this fake never does that on
    /// its own.
    /// </summary>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request)
    {
        CreateOperationCalls++;
        LastCreateOperationRequest = request;
        return Operation;
    }
}

/// <summary>
/// A <see cref="FakeProvisioner"/> that is <em>also</em> an <see cref="IMaintainer"/>, so a UI test can
/// exercise the drift and update paths against the real
/// <see cref="Servyx.Application.Provisioning.ProvisioningDashboardService"/> type test rather than around
/// it.
/// </summary>
/// <remarks>
/// Both maintenance members are reads by contract, and this fake keeps them that way: neither touches
/// <see cref="Operation"/>. The call counters exist so a test can assert the negative that matters — that
/// previewing an update, or failing to acknowledge its data impact, never reached the apply path.
/// </remarks>
public sealed class FakeMaintainingProvisioner : IProvisioner, IMaintainer
{
    private readonly Func<ProvisioningRequest, ProvisioningPlan> _planFactory;
    private readonly Func<ResourceHandle, ProvisioningRequest, UpdatePlan?> _updatePlanFactory;
    private readonly Func<ResourceHandle, DriftResult> _driftFactory;

    public FakeMaintainingProvisioner(
        string provisionerId,
        ProvisioningCapabilities capabilities,
        ProvisioningPlan plan,
        UpdatePlan? updatePlan,
        Func<ResourceHandle, DriftResult> driftFactory,
        RecordingProvisioningOperation? operation = null)
        : this(
            provisionerId,
            capabilities,
            _ => plan,
            (_, _) => updatePlan,
            driftFactory,
            operation)
    {
    }

    public FakeMaintainingProvisioner(
        string provisionerId,
        ProvisioningCapabilities capabilities,
        Func<ProvisioningRequest, ProvisioningPlan> planFactory,
        Func<ResourceHandle, ProvisioningRequest, UpdatePlan?> updatePlanFactory,
        Func<ResourceHandle, DriftResult> driftFactory,
        RecordingProvisioningOperation? operation = null)
    {
        ProvisionerId = provisionerId;
        Capabilities = capabilities;
        _planFactory = planFactory;
        _updatePlanFactory = updatePlanFactory;
        _driftFactory = driftFactory;
        Operation = operation ?? new RecordingProvisioningOperation();
    }

    public string ProvisionerId { get; }

    public ProvisioningCapabilities Capabilities { get; }

    /// <summary>The single operation this fake ever hands out, so its own call count can be inspected.</summary>
    public RecordingProvisioningOperation Operation { get; }

    /// <summary>How many times a create plan was requested.</summary>
    public int PlanCalls { get; private set; }

    /// <summary>How many times an update plan was requested. A preview and a revalidation each count once.</summary>
    public int PlanUpdateCalls { get; private set; }

    /// <summary>How many times drift was checked.</summary>
    public int DetectDriftCalls { get; private set; }

    /// <summary>How many times the mutating entry point was reached. Must stay zero during any preview.</summary>
    public int CreateOperationCalls { get; private set; }

    /// <summary>The last handle a drift check or update plan was asked about.</summary>
    public ResourceHandle? LastHandle { get; private set; }

    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        PlanCalls++;
        return Task.FromResult(_planFactory(request));
    }

    public Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
        => Task.FromResult<ProvisionedResource?>(null);

    public Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ResourceHandle>>([]);

    public IProvisioningOperation CreateOperation(ProvisioningRequest request)
    {
        CreateOperationCalls++;
        return Operation;
    }

    public Task<UpdatePlan?> PlanUpdateAsync(ResourceHandle handle, ProvisioningRequest desired, CancellationToken ct = default)
    {
        PlanUpdateCalls++;
        LastHandle = handle;
        return Task.FromResult(_updatePlanFactory(handle, desired));
    }

    public Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        DetectDriftCalls++;
        LastHandle = handle;
        return Task.FromResult(_driftFactory(handle));
    }
}

/// <summary>
/// An <see cref="IProvisioningOperation"/> that records whether it was ever driven.
/// </summary>
/// <remarks>
/// <strong>Constructed with no behaviour, it throws.</strong> That is the default on purpose: most tests
/// here assert that nothing executed, and a fake that silently succeeded would let a test that
/// <em>accidentally</em> executed still pass. A test that means to drive a create supplies the outcome
/// explicitly.
/// </remarks>
public sealed class RecordingProvisioningOperation : IProvisioningOperation
{
    private readonly Func<CancellationToken, Task<ProvisionedResource>>? _create;

    /// <summary>Creates an operation whose create call is an assertion failure. The preview-safe default.</summary>
    public RecordingProvisioningOperation()
    {
    }

    /// <summary>Creates an operation that succeeds with <paramref name="result"/>.</summary>
    public RecordingProvisioningOperation(ProvisionedResource result)
        => _create = _ => Task.FromResult(result);

    /// <summary>Creates an operation whose create call runs <paramref name="create"/> — success, failure, or a delay.</summary>
    public RecordingProvisioningOperation(Func<CancellationToken, Task<ProvisionedResource>> create)
        => _create = create;

    /// <summary>
    /// The provisioner this operation belongs to. Settable because the ledger indexes intents by it, so a
    /// test asserting that a failed apply left a findable row needs it to match the registered provisioner.
    /// </summary>
    public string ProvisionerId { get; init; } = "fake";

    public string? Region => null;

    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How many times the provider-mutating call was made. Must stay zero during a preview.</summary>
    public int CreateCalls { get; private set; }

    /// <summary>How many times compensation ran.</summary>
    public int CompensateCalls { get; private set; }

    public Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
    {
        CreateCalls++;

        if (_create is null)
        {
            throw new InvalidOperationException(
                "A provisioning preview must never reach CreateAsync. Reaching it means the UI executed a plan.");
        }

        return _create(ct);
    }

    public Task CompensateAsync(CancellationToken ct = default)
    {
        CompensateCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IProvisioningLedger"/> that counts every write. A write-ahead intent row is the first
/// thing <c>ProvisioningExecutor</c> does, so "<see cref="RecordIntentCalls"/> is zero" is direct evidence
/// that no execution began.
/// </summary>
public sealed class RecordingProvisioningLedger : IProvisioningLedger
{
    private readonly List<ProvisioningIntent> _intended = [];
    private readonly List<ProvisionedResourceRow> _created = [];

    /// <summary>How many intents were recorded. Must stay zero unless a test executes a plan on purpose.</summary>
    public int RecordIntentCalls { get; private set; }

    /// <summary>How many rows were advanced to Created.</summary>
    public int MarkCreatedCalls { get; private set; }

    /// <summary>
    /// The rows still in <c>Intended</c> — i.e. exactly what an orphan sweep would have to resolve.
    /// </summary>
    public IReadOnlyList<ProvisioningIntent> Intended => _intended;

    /// <summary>The rows the provider confirmed, each carrying the id the provider assigned it.</summary>
    public IReadOnlyList<ProvisionedResourceRow> Created => _created;

    /// <summary>Seeds a row that already exists, without counting as a write by the code under test.</summary>
    public RecordingProvisioningLedger Seed(ProvisioningIntent intent)
    {
        _intended.Add(intent);
        return this;
    }

    /// <summary>
    /// Seeds a row the provider already confirmed — the shape a resource Servyx owns actually has, complete
    /// with the provider-assigned id. Written directly rather than by driving
    /// <see cref="RecordIntentAsync"/> and <see cref="MarkCreatedAsync"/> so the write counters stay zero and
    /// a test can still assert that the code under test wrote nothing.
    /// </summary>
    public RecordingProvisioningLedger SeedCreated(ProvisionedResourceRow row)
    {
        _created.Add(row);
        return this;
    }

    public Task RecordIntentAsync(ProvisioningIntent intent, CancellationToken ct = default)
    {
        RecordIntentCalls++;
        _intended.Add(intent);
        return Task.CompletedTask;
    }

    public Task MarkCreatedAsync(Guid ledgerRowId, string providerResourceId, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        MarkCreatedCalls++;

        // Moved rather than deleted, exactly as the durable ledger updates the row in place: a confirmed
        // resource stops being an unresolved intent and starts being something Servyx owns and can name.
        var intent = _intended.Find(i => i.LedgerRowId == ledgerRowId);
        _intended.RemoveAll(i => i.LedgerRowId == ledgerRowId);

        if (intent is not null)
        {
            _created.Add(new ProvisionedResourceRow(
                LedgerRowId: ledgerRowId,
                Handle: new ResourceHandle(intent.ProvisionerId, providerResourceId, intent.Region, intent.Tags),
                JobId: intent.JobId,
                RecordedAt: intent.RecordedAt,
                ConfirmedAt: observedAt));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisioningIntent>> ListIntendedAsync(string provisionerId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProvisioningIntent>>(
            [.. _intended.Where(i => string.Equals(i.ProvisionerId, provisionerId, StringComparison.Ordinal))]);

    public Task<IReadOnlyList<ProvisionedResourceRow>> ListCreatedAsync(string provisionerId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProvisionedResourceRow>>(
            [.. _created.Where(r => string.Equals(r.Handle.ProvisionerId, provisionerId, StringComparison.Ordinal))]);
}
