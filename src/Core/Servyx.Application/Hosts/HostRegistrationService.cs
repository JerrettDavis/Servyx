using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Servyx.Application.Auditing;
using Servyx.Domain.Common;
using Servyx.Domain.Connectors;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Secrets;

namespace Servyx.Application.Hosts;

/// <summary>
/// <see cref="IHostRegistrationService"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where the trust decision actually lives.</strong> The one thing this type must never do is pin a
/// fingerprint it was handed. It keeps a short-lived, in-process record of every host key it has itself
/// observed, keyed by the endpoint string the probe was asked about
/// (<see cref="ObservationLifetime"/>), and <see cref="RegisterAsync"/> resolves the key to pin from that
/// record — or from a fresh probe when the record has expired — before comparing the caller's claimed
/// fingerprint against it in fixed time. The caller's string is therefore only ever a gate, never a value.
/// See <see cref="IHostRegistrationService.RegisterAsync"/>'s remarks for the full argument.
/// </para>
/// <para>
/// <strong>Why an in-process cache is sufficient.</strong> The cache is an optimisation and a UX affordance,
/// not the security boundary: its only effect is to save a second network round trip between the operator
/// reading a fingerprint and submitting the form. Losing it (process restart, expiry, a different endpoint
/// spelling) degrades to re-probing, which is exactly as safe — both paths compare against something this
/// server observed on the wire. That is why it needs no durability, no coordination across replicas, and no
/// signed token: there is no state here whose loss can weaken the check, only state whose presence avoids a
/// round trip. Bounded by <see cref="MaxTrackedEndpoints"/> and swept on write, so a caller that probes
/// endless distinct endpoints cannot grow it without limit.
/// </para>
/// </remarks>
public sealed class HostRegistrationService : IHostRegistrationService
{
    /// <summary>
    /// How long a probe observation stays usable for a subsequent <see cref="RegisterAsync"/>. Long enough for
    /// an operator to read a fingerprint off a server console and compare it; short enough that a stale
    /// observation is not silently reused across an unrelated session. Expiry is never a security failure —
    /// it just forces the fresh re-probe below.
    /// </summary>
    public static readonly TimeSpan ObservationLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound on retained probe observations, so the cache cannot grow without limit.</summary>
    private const int MaxTrackedEndpoints = 256;

    /// <summary>The trust policy stamped on a host registered through this service.</summary>
    /// <remarks>
    /// Always <c>requirePinned</c>, never <c>trustOnFirstUse</c>: by the time a row exists here a specific key
    /// has already been confirmed by a human and pinned, so there is no first use left to trust. The row also
    /// carries that fingerprint in <see cref="Host.PinnedFingerprints"/>, which
    /// <c>RegisteredHostTargetFactory</c> forwards as the transport's <c>pinnedFingerprints</c> option — the
    /// strictest of the available postures.
    /// </remarks>
    public const string RegisteredTrustPolicy = "requirePinned";

    /// <summary>The <see cref="Host.ConnectorId"/> prefix for a host registered through this service.</summary>
    private const string ConnectorIdPrefix = "ssh:";

    private readonly IHostRepository _repository;
    private readonly IHostKeyProbe _probe;
    private readonly IHostKeyStore _hostKeys;
    private readonly IHostCredentialImporter _credentials;
    private readonly IHostConnectionRefresher _connections;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<HostRegistrationService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, TrackedObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a <see cref="HostRegistrationService"/>.</summary>
    /// <param name="timeProvider">Clock used for observation expiry and row/pin timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    public HostRegistrationService(
        IHostRepository repository,
        IHostKeyProbe probe,
        IHostKeyStore hostKeys,
        IHostCredentialImporter credentials,
        IHostConnectionRefresher connections,
        IAuditLogger auditLogger,
        ILogger<HostRegistrationService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(hostKeys);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(auditLogger);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _probe = probe;
        _hostKeys = hostKeys;
        _credentials = credentials;
        _connections = connections;
        _auditLogger = auditLogger;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<HostProbeResult> ProbeAsync(string endpoint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var trimmedEndpoint = endpoint.Trim();
        var observation = await _probe.ObserveAsync(trimmedEndpoint, ct).ConfigureAwait(false);

        if (observation.Status == HostKeyObservationStatus.Observed)
        {
            Remember(trimmedEndpoint, observation);
        }

        return ToProbeResult(observation);
    }

    /// <inheritdoc />
    public async Task<RegistrationResult> RegisterAsync(
        string name,
        string endpoint,
        string confirmedFingerprint,
        ReadOnlyMemory<byte> privateKeyBytes,
        string? passphrase,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (string.IsNullOrWhiteSpace(name))
        {
            return RegistrationResult.InvalidName("A host name is required.");
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return RegistrationResult.InvalidEndpoint("An endpoint is required.");
        }

        if (string.IsNullOrWhiteSpace(confirmedFingerprint))
        {
            // Not a mismatch — the caller never confirmed anything at all. Refused under the same outcome,
            // because from the host's point of view the effect is identical: no human vouched for this key.
            return RegistrationResult.FingerprintNotConfirmed();
        }

        if (privateKeyBytes.Length == 0)
        {
            return RegistrationResult.InvalidCredential(
                "The SSH private key is empty. Upload the key file itself, not an empty or unreadable one.");
        }

        var trimmedName = name.Trim();
        var trimmedEndpoint = endpoint.Trim();

        // Reuse SecretUrn's own validation rather than restating its charset here: the name becomes the URN's
        // scope-id segment, so "is this a legal host name?" and "is this a legal URN segment?" are literally
        // the same question, and answering it twice in two places is how they drift apart.
        try
        {
            _ = SecretUrn.Create("connector", trimmedName, "ssh", "private-key");
        }
        catch (ArgumentException ex)
        {
            return RegistrationResult.InvalidName(
                $"'{trimmedName}' cannot be used as a host name: {ex.Message} Use letters, digits, '-', '_', or '.'.");
        }

        // Pre-check the unique index rather than surfacing a raw constraint violation as an exception, exactly
        // as ServerAdoptionService.AdoptAsync does. This is not a substitute for the index — a concurrent
        // registration under the same name still loses at the database, and that is meant to be loud.
        var existing = await _repository.TryGetByNameAsync(trimmedName, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return RegistrationResult.AlreadyExists(trimmedName, existing.Id);
        }

        // ── The trust check ───────────────────────────────────────────────────────────────────────────
        //
        // Everything below this point pins, stores, or persists, so this is the last gate. The observation
        // resolved here is Servyx's own: either one it recorded during ProbeAsync, or one it takes right now.
        // confirmedFingerprint is never assigned to anything — it is only ever compared.
        var observation = await ResolveObservationAsync(trimmedEndpoint, ct).ConfigureAwait(false);

        switch (observation.Status)
        {
            case HostKeyObservationStatus.InvalidEndpoint:
                return RegistrationResult.InvalidEndpoint(observation.FailureReason);
            case HostKeyObservationStatus.Unreachable:
                return RegistrationResult.HostUnreachable(trimmedEndpoint, observation.FailureReason);
        }

        var observedFingerprint = observation.Sha256Fingerprint!;
        if (!FixedTimeEquals(observedFingerprint, confirmedFingerprint.Trim()))
        {
            // Logged without either fingerprint: a mismatch is exactly the shape a machine-in-the-middle
            // attempt takes, and the operator's next step is to re-probe and look, not to read a log line that
            // helpfully prints the value that would have been accepted.
            _logger.LogWarning(
                "Refused to register host '{HostName}' at '{Endpoint}': the confirmed fingerprint did not match "
                + "the host key this server observed. Nothing was pinned, stored, or persisted.",
                trimmedName,
                trimmedEndpoint);

            return RegistrationResult.FingerprintNotConfirmed();
        }

        var now = _timeProvider.GetUtcNow();

        // ── Side effects begin ────────────────────────────────────────────────────────────────────────
        //
        // Pin and credential first, host row last: the row is the only thing that makes either of the other
        // two reachable, so a failure before it leaves nothing that can be used. See the interface remarks.
        SecretUrn privateKeyUrn;
        try
        {
            await _hostKeys.PinAsync(
                new HostKeyRecord(
                    observation.Host,
                    observation.Port,
                    observation.Algorithm!,
                    observedFingerprint,
                    observation.PublicKeyBlob!,
                    now,
                    actor),
                actor,
                ct).ConfigureAwait(false);

            var imported = await _credentials
                .ImportPrivateKeyAsync(trimmedName, privateKeyBytes, passphrase, actor, ct)
                .ConfigureAwait(false);

            privateKeyUrn = imported.PrivateKeyUrn;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reportable rather than fatal: no host row exists, so Servyx's own tracking is exactly as it was
            // and a retry is safe. The message is the exception's, never key material — neither the pin nor the
            // importer is given anything secret-derived to put in one.
            _logger.LogError(ex, "Failed to establish trust or store credentials for host '{HostName}'.", trimmedName);
            return RegistrationResult.Failed(ex.Message);
        }

        var host = new Host
        {
            Id = HostId.New(),
            Name = trimmedName,
            ConnectorId = ConnectorIdPrefix + trimmedName,
            Endpoint = trimmedEndpoint,
            // The URN the importer actually wrote to, not one re-derived here from the naming convention.
            CredentialUrn = privateKeyUrn.Value,
            TrustPolicy = RegisteredTrustPolicy,
            // Servyx's own observation, never the caller's claim — the two are known equal by now, but only
            // one of them has a provenance worth persisting.
            PinnedFingerprints = observedFingerprint,
            Enabled = true,
            RegisteredBy = actor,
            CreatedAt = now,
        };

        try
        {
            await _repository.AddAsync(host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately rethrown, not converted to a result: side effects already exist by this point, and
            // this log line is the record of exactly which ones. Nothing is rolled back — see the interface
            // remarks for why un-pinning (which would mean revoking) is worse than leaving the pin.
            _logger.LogError(
                "Host row for '{HostName}' could not be persisted after its key was pinned and its credential "
                + "stored at '{CredentialUrn}'. Both are inert while no row references them, and re-registering "
                + "'{HostName}' will overwrite the credential and re-pin the same key.",
                trimmedName,
                privateKeyUrn.Value,
                trimmedName);

            throw;
        }

        // Only after the row is durable: a freshly-registered host becomes discoverable without a restart.
        _connections.Invalidate();

        _logger.LogInformation(
            "Registered host '{HostName}' at '{Endpoint}' with a confirmed, pinned host key, by '{Actor}'.",
            trimmedName,
            trimmedEndpoint,
            actor);

        await _auditLogger.RecordAsync(
            actor, AuditActions.HostRegistered, targetType: "host", targetId: trimmedName,
            details: trimmedEndpoint, ct).ConfigureAwait(false);

        // The observation has done its job; dropping it means a second registration attempt under a different
        // name re-probes rather than reusing a record the operator has moved on from.
        _observations.TryRemove(trimmedEndpoint, out _);

        return RegistrationResult.Registered(host.Id, observedFingerprint);
    }

    /// <inheritdoc />
    public async Task<RegisteredHostsResult> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await _repository.ListAsync(ct).ConfigureAwait(false);
            return RegisteredHostsResult.Ok(rows
                .Select(row => new RegisteredHost(
                    row.Id,
                    row.Name,
                    row.Endpoint,
                    row.TrustPolicy,
                    row.PinnedFingerprints,
                    row.Enabled,
                    row.RegisteredBy,
                    row.CreatedAt))
                .ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read registered hosts.");
            return RegisteredHostsResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<DeregistrationResult> DeregisterAsync(string name, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var trimmedName = name.Trim();

        var existing = await _repository.TryGetByNameAsync(trimmedName, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return DeregistrationResult.NotFound(trimmedName);
        }

        var removed = await _repository.RemoveAsync(existing.Id, ct).ConfigureAwait(false);
        if (!removed)
        {
            // Lost a race with a concurrent deregistration. Reported as NotFound rather than as a failure:
            // the caller's intent — "this host should not be registered" — holds either way.
            return DeregistrationResult.NotFound(trimmedName);
        }

        // Stop discovering the host straight away rather than at the next restart.
        _connections.Invalidate();

        _logger.LogInformation(
            "Deregistered host '{HostName}' by '{Actor}'. Its stored credential and pinned host key were left "
            + "untouched, and no command was issued to the host itself.",
            trimmedName,
            actor);

        await _auditLogger.RecordAsync(
            actor, AuditActions.HostDeregistered, targetType: "host", targetId: trimmedName, ct: ct)
            .ConfigureAwait(false);

        return DeregistrationResult.Deregistered();
    }

    /// <summary>
    /// Resolves the host key observation <see cref="RegisterAsync"/> checks against: a still-valid record from
    /// a prior <see cref="ProbeAsync"/> if one exists, otherwise a fresh probe. Both branches return something
    /// this server saw on the wire; the cache only decides whether a second round trip happens.
    /// </summary>
    private async Task<HostKeyObservation> ResolveObservationAsync(string endpoint, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        if (_observations.TryGetValue(endpoint, out var tracked))
        {
            if (tracked.ExpiresAt > now)
            {
                return tracked.Observation;
            }

            _observations.TryRemove(endpoint, out _);
        }

        var observation = await _probe.ObserveAsync(endpoint, ct).ConfigureAwait(false);
        if (observation.Status == HostKeyObservationStatus.Observed)
        {
            Remember(endpoint, observation);
        }

        return observation;
    }

    /// <summary><paramref name="endpoint"/> must already be trimmed — it becomes the cache key verbatim.</summary>
    private void Remember(string endpoint, HostKeyObservation observation)
    {
        var now = _timeProvider.GetUtcNow();
        SweepExpired(now);

        // Hard cap after the sweep: if every entry is still live and the cap is reached, the new observation is
        // simply not retained. That is a UX cost only (the register step re-probes), never a correctness one.
        if (_observations.Count >= MaxTrackedEndpoints && !_observations.ContainsKey(endpoint))
        {
            return;
        }

        _observations[endpoint] = new TrackedObservation(observation, now + ObservationLifetime);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var entry in _observations)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _observations.TryRemove(entry.Key, out _);
            }
        }
    }

    private static HostProbeResult ToProbeResult(HostKeyObservation observation) => new(
        observation.Status switch
        {
            HostKeyObservationStatus.Observed => HostProbeOutcome.Reached,
            HostKeyObservationStatus.InvalidEndpoint => HostProbeOutcome.InvalidEndpoint,
            _ => HostProbeOutcome.Unreachable,
        },
        observation.Host,
        observation.Port,
        observation.Algorithm,
        observation.Sha256Fingerprint,
        observation.FailureReason);

    /// <summary>
    /// Compares two fingerprint strings without short-circuiting on the first differing character, matching
    /// <c>HostKeyVerifier</c>'s own comparison discipline. A fingerprint is not a secret, but the answer to
    /// "how much of my guess was right?" is exactly what a caller probing this endpoint would want, and there
    /// is no reason to hand it over one byte at a time.
    /// </summary>
    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private sealed record TrackedObservation(HostKeyObservation Observation, DateTimeOffset ExpiresAt);
}
