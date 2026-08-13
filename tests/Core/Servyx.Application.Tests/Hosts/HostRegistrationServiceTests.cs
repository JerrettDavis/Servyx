using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Hosts;
using Servyx.Application.Tests.Auditing;
using Servyx.Domain.Common;
using Servyx.Domain.Connectors;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Secrets;

namespace Servyx.Application.Tests.Hosts;

/// <summary>
/// Tests for <see cref="HostRegistrationService"/> — the "probe a remote SSH host, confirm its fingerprint,
/// register it, view it, deregister it" surface. Every collaborator is a hand-written fake: each one carries
/// state across calls (rows added then listed, keys pinned then inspected, invalidations counted), which is
/// exactly the shape a sequence of stubbed returns expresses badly.
/// <para>
/// The tests that matter most here are the fingerprint ones. Registration's whole security value is that a
/// fingerprint arriving from a caller is compared against one this process observed, never pinned on faith,
/// so there are assertions below for a caller that invents a fingerprint outright, a caller that replays one
/// this host no longer presents, and a caller that skips the probe step entirely.
/// </para>
/// </summary>
public class HostRegistrationServiceTests
{
    private const string Endpoint = "ssh:steam@10.0.0.4:22";
    private const string RealFingerprint = "SHA256:aVeryRealFingerprintValueForTests0123456789";
    private const string AttackerFingerprint = "SHA256:anAttackerSuppliedFingerprintValue98765432";
    private const string Actor = "operator";

    private static readonly byte[] RealKeyBlob = [0x00, 0x00, 0x00, 0x0b, 0xde, 0xad, 0xbe, 0xef];
    private static readonly ReadOnlyMemory<byte> PrivateKey = Encoding.UTF8.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----\nnot-a-real-key\n");

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeHostRepository : IHostRepository
    {
        public List<Host> Rows { get; } = [];

        /// <summary>Set to make <see cref="AddAsync"/> fail, exercising the partial-write path.</summary>
        public Exception? AddFailure { get; set; }

        /// <summary>Set to make <see cref="ListAsync"/> fail, exercising the honest-degradation path.</summary>
        public Exception? ListFailure { get; set; }

        public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) =>
            ListFailure is not null
                ? Task.FromException<IReadOnlyList<Host>>(ListFailure)
                : Task.FromResult<IReadOnlyList<Host>>(Rows.ToList());

        public Task<Host?> TryGetAsync(HostId id, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(row => row.Id == id));

        public Task<Host?> TryGetByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)));

        public Task AddAsync(Host host, CancellationToken ct = default)
        {
            if (AddFailure is not null)
            {
                return Task.FromException(AddFailure);
            }

            Rows.Add(host);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(HostId id, CancellationToken ct = default)
        {
            var existing = Rows.FirstOrDefault(row => row.Id == id);
            if (existing is null)
            {
                return Task.FromResult(false);
            }

            Rows.Remove(existing);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A probe whose answer can be changed between calls, so a test can make the host present a different key
    /// the second time it is asked — the case a replayed fingerprint has to be refused for.
    /// </summary>
    private sealed class FakeHostKeyProbe : IHostKeyProbe
    {
        public HostKeyObservation Answer { get; set; } =
            HostKeyObservation.Observed("10.0.0.4", 22, "ssh-ed25519", RealFingerprint, RealKeyBlob);

        public int CallCount { get; private set; }

        public List<string> ObservedEndpoints { get; } = [];

        public Task<HostKeyObservation> ObserveAsync(string endpoint, CancellationToken ct = default)
        {
            CallCount++;
            ObservedEndpoints.Add(endpoint);
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingHostKeyStore : IHostKeyStore
    {
        public List<(HostKeyRecord Record, string Actor)> Pins { get; } = [];
        public List<(string Host, int Port, string Actor)> Revocations { get; } = [];

        /// <summary>Set to make <see cref="PinAsync"/> fail, exercising the pre-database failure path.</summary>
        public Exception? PinFailure { get; set; }

        public Task<HostKeyRecord?> FindAsync(string host, int port, CancellationToken ct = default) =>
            Task.FromResult<HostKeyRecord?>(
                Pins.LastOrDefault(p => p.Record.Host == host && p.Record.Port == port).Record);

        public Task PinAsync(HostKeyRecord record, string actor, CancellationToken ct = default)
        {
            if (PinFailure is not null)
            {
                return Task.FromException(PinFailure);
            }

            Pins.Add((record, actor));
            return Task.CompletedTask;
        }

        public Task RevokeAsync(string host, int port, string actor, CancellationToken ct = default)
        {
            Revocations.Add((host, port, actor));
            return Task.CompletedTask;
        }

        public Task<bool> IsRevokedAsync(string host, int port, CancellationToken ct = default) =>
            Task.FromResult(Revocations.Any(r => r.Host == host && r.Port == port));
    }

    private sealed class RecordingCredentialImporter : IHostCredentialImporter
    {
        public List<(string HostKey, byte[] PrivateKey, string? Passphrase, string Actor)> Imports { get; } = [];

        public Task<HostCredentialImportResult> ImportPrivateKeyAsync(
            string hostKey,
            ReadOnlyMemory<byte> privateKeyBytes,
            string? passphrase,
            string actor,
            CancellationToken ct = default)
        {
            Imports.Add((hostKey, privateKeyBytes.ToArray(), passphrase, actor));

            return Task.FromResult(new HostCredentialImportResult(
                SecretUrn.Create("connector", hostKey, "ssh", "private-key"),
                string.IsNullOrEmpty(passphrase) ? null : SecretUrn.Create("connector", hostKey, "ssh", "passphrase")));
        }
    }

    private sealed class CountingRefresher : IHostConnectionRefresher
    {
        public int InvalidateCount { get; private set; }

        public void Invalidate() => InvalidateCount++;
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Harness(
        HostRegistrationService Service,
        FakeHostRepository Repository,
        FakeHostKeyProbe Probe,
        RecordingHostKeyStore HostKeys,
        RecordingCredentialImporter Credentials,
        CountingRefresher Refresher,
        FakeAuditLogger AuditLogger,
        TestTimeProvider Time);

    private static Harness Build()
    {
        var repository = new FakeHostRepository();
        var probe = new FakeHostKeyProbe();
        var hostKeys = new RecordingHostKeyStore();
        var credentials = new RecordingCredentialImporter();
        var refresher = new CountingRefresher();
        var auditLogger = new FakeAuditLogger();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero));

        var service = new HostRegistrationService(
            repository, probe, hostKeys, credentials, refresher, auditLogger,
            NullLogger<HostRegistrationService>.Instance, time);

        return new Harness(service, repository, probe, hostKeys, credentials, refresher, auditLogger, time);
    }

    private static Task<RegistrationResult> RegisterAsync(
        Harness harness, string name = "prod-host", string fingerprint = RealFingerprint, string? passphrase = null) =>
        harness.Service.RegisterAsync(name, Endpoint, fingerprint, PrivateKey, passphrase, Actor);

    // ── Probe ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Probing_a_reachable_host_reports_the_fingerprint_it_presented()
    {
        var harness = Build();

        var result = await harness.Service.ProbeAsync(Endpoint);

        result.Outcome.Should().Be(HostProbeOutcome.Reached);
        result.Host.Should().Be("10.0.0.4");
        result.Port.Should().Be(22);
        result.Algorithm.Should().Be("ssh-ed25519");
        result.Sha256Fingerprint.Should().Be(RealFingerprint);
        result.Detail.Should().BeNull();
    }

    [Fact]
    public async Task Probing_grants_no_trust_and_writes_nothing()
    {
        var harness = Build();

        await harness.Service.ProbeAsync(Endpoint);

        harness.HostKeys.Pins.Should().BeEmpty("a probe must never pin — a human has not confirmed anything yet");
        harness.Credentials.Imports.Should().BeEmpty();
        harness.Repository.Rows.Should().BeEmpty();
        harness.Refresher.InvalidateCount.Should().Be(0);
    }

    [Fact]
    public async Task Probing_an_unreachable_host_reports_that_honestly_rather_than_throwing()
    {
        var harness = Build();
        harness.Probe.Answer = HostKeyObservation.Unreachable("10.0.0.4", 22, "Connection refused");

        var result = await harness.Service.ProbeAsync(Endpoint);

        result.Outcome.Should().Be(HostProbeOutcome.Unreachable);
        result.Sha256Fingerprint.Should().BeNull();
        result.Detail.Should().Be("Connection refused");
    }

    [Fact]
    public async Task Probing_an_unparseable_endpoint_reports_InvalidEndpoint()
    {
        var harness = Build();
        harness.Probe.Answer = HostKeyObservation.InvalidEndpoint("not a host", "'not a host' has an invalid port.");

        var result = await harness.Service.ProbeAsync("not a host");

        result.Outcome.Should().Be(HostProbeOutcome.InvalidEndpoint);
        result.Detail.Should().NotBeNullOrWhiteSpace();
    }

    // ── Registration, the happy path ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_after_confirming_the_probed_fingerprint_pins_stores_and_persists()
    {
        var harness = Build();
        var probe = await harness.Service.ProbeAsync(Endpoint);

        var result = await RegisterAsync(harness, fingerprint: probe.Sha256Fingerprint!, passphrase: "s3cret");

        result.Outcome.Should().Be(RegistrationOutcome.Registered);
        result.HostId.Should().NotBeNull();
        result.PinnedFingerprint.Should().Be(RealFingerprint);

        // The pin carries the key Servyx observed — algorithm, fingerprint, and the raw blob — attributed to
        // the operator who confirmed it.
        var pin = harness.HostKeys.Pins.Should().ContainSingle().Subject;
        pin.Actor.Should().Be(Actor);
        pin.Record.Host.Should().Be("10.0.0.4");
        pin.Record.Port.Should().Be(22);
        pin.Record.Algorithm.Should().Be("ssh-ed25519");
        pin.Record.Sha256Fingerprint.Should().Be(RealFingerprint);
        pin.Record.PublicKeyBlob.Should().Equal(RealKeyBlob);
        pin.Record.PinnedByActor.Should().Be(Actor);

        // The credential went to the store under the host's own name, with the passphrase alongside it.
        var import = harness.Credentials.Imports.Should().ContainSingle().Subject;
        import.HostKey.Should().Be("prod-host");
        import.PrivateKey.Should().Equal(PrivateKey.ToArray());
        import.Passphrase.Should().Be("s3cret");
        import.Actor.Should().Be(Actor);

        var row = harness.Repository.Rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(result.HostId);
        row.Name.Should().Be("prod-host");
        row.Endpoint.Should().Be(Endpoint);
        row.CredentialUrn.Should().Be("secret://connector/prod-host/ssh/private-key");
        row.TrustPolicy.Should().Be("requirePinned");
        row.PinnedFingerprints.Should().Be(RealFingerprint);
        row.Enabled.Should().BeTrue();
        row.RegisteredBy.Should().Be(Actor);
        row.CreatedAt.Should().Be(harness.Time.Now);
    }

    /// <summary>
    /// The restart-free refresh contract: a freshly-registered host has to become discoverable without the
    /// process being bounced, and the only thing that makes that happen is the invalidation call.
    /// </summary>
    [Fact]
    public async Task Successful_registration_invalidates_the_cached_connection_set()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        harness.Refresher.InvalidateCount.Should().Be(0);

        (await RegisterAsync(harness)).Outcome.Should().Be(RegistrationOutcome.Registered);

        harness.Refresher.InvalidateCount.Should().Be(1);
    }

    /// <summary>
    /// The probe observation is reused, so the ordinary two-step flow costs exactly one network round trip —
    /// and, more importantly, the value compared against is the same one the operator was shown.
    /// </summary>
    [Fact]
    public async Task Registering_straight_after_a_probe_reuses_that_observation_rather_than_re_probing()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        await RegisterAsync(harness);

        harness.Probe.CallCount.Should().Be(1);
    }

    /// <summary>
    /// The cache is an optimisation, not the check: with no prior probe (or an expired one) registration
    /// probes for itself rather than falling back to trusting the caller's string.
    /// </summary>
    [Fact]
    public async Task Registering_without_a_prior_probe_probes_for_itself_and_still_succeeds()
    {
        var harness = Build();

        var result = await RegisterAsync(harness);

        result.Outcome.Should().Be(RegistrationOutcome.Registered);
        harness.Probe.CallCount.Should().Be(1, "the register step had to observe the key itself");
    }

    // ── Registration, the security property ──────────────────────────────────────────────────────────

    /// <summary>
    /// The core anti-tampering case. A compromised or buggy caller submits a fingerprint of its own choosing —
    /// one this server never observed — and registration must refuse it outright, writing nothing anywhere.
    /// Pinning the submitted value here would trust an attacker's key for every future connection to this host.
    /// </summary>
    [Fact]
    public async Task A_fingerprint_the_server_never_observed_is_refused_and_nothing_is_written()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        var result = await RegisterAsync(harness, fingerprint: AttackerFingerprint);

        result.Outcome.Should().Be(RegistrationOutcome.FingerprintNotConfirmed);
        result.HostId.Should().BeNull();
        result.PinnedFingerprint.Should().BeNull();

        harness.HostKeys.Pins.Should().BeEmpty("the attacker-supplied fingerprint must never be pinned");
        harness.Credentials.Imports.Should().BeEmpty("no key material may be stored for a host that was not confirmed");
        harness.Repository.Rows.Should().BeEmpty();
        harness.Refresher.InvalidateCount.Should().Be(0);
    }

    /// <summary>The refusal must not leak the value that would have been accepted.</summary>
    [Fact]
    public async Task A_refused_fingerprint_does_not_echo_the_observed_one_back_to_the_caller()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        var result = await RegisterAsync(harness, fingerprint: AttackerFingerprint);

        result.Detail.Should().NotContain(RealFingerprint);
    }

    /// <summary>
    /// A caller that skips the probe entirely and posts a fingerprint straight to registration is in exactly
    /// the position an attacker-controlled form is: it gets checked against a fresh observation and loses.
    /// </summary>
    [Fact]
    public async Task A_fingerprint_submitted_with_no_probe_at_all_is_still_checked_against_a_fresh_observation()
    {
        var harness = Build();

        var result = await RegisterAsync(harness, fingerprint: AttackerFingerprint);

        result.Outcome.Should().Be(RegistrationOutcome.FingerprintNotConfirmed);
        harness.Probe.CallCount.Should().Be(1);
        harness.HostKeys.Pins.Should().BeEmpty();
        harness.Repository.Rows.Should().BeEmpty();
    }

    /// <summary>
    /// Once the observation window closes, a fingerprint confirmed against the old key is re-checked against
    /// whatever the host presents now — so a key that changed in the meantime (the machine-in-the-middle
    /// signature) is caught rather than waved through on a stale record.
    /// </summary>
    [Fact]
    public async Task An_expired_observation_is_re_probed_and_a_now_stale_fingerprint_is_refused()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        harness.Time.Now += HostRegistrationService.ObservationLifetime + TimeSpan.FromMinutes(1);
        harness.Probe.Answer = HostKeyObservation.Observed("10.0.0.4", 22, "ssh-ed25519", AttackerFingerprint, [0x01, 0x02]);

        var result = await RegisterAsync(harness, fingerprint: RealFingerprint);

        result.Outcome.Should().Be(RegistrationOutcome.FingerprintNotConfirmed);
        harness.Probe.CallCount.Should().Be(2, "the stale observation must be discarded and the host re-probed");
        harness.HostKeys.Pins.Should().BeEmpty();
        harness.Repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_confirmed_fingerprint_is_refused_the_same_as_a_wrong_one()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        var result = await RegisterAsync(harness, fingerprint: "   ");

        result.Outcome.Should().Be(RegistrationOutcome.FingerprintNotConfirmed);
        harness.HostKeys.Pins.Should().BeEmpty();
        harness.Repository.Rows.Should().BeEmpty();
    }

    // ── Registration, the other refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_a_name_that_is_already_taken_is_refused_without_touching_anything()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        (await RegisterAsync(harness)).Outcome.Should().Be(RegistrationOutcome.Registered);

        var existingId = harness.Repository.Rows.Single().Id;
        var pinsBefore = harness.HostKeys.Pins.Count;
        var importsBefore = harness.Credentials.Imports.Count;
        var invalidationsBefore = harness.Refresher.InvalidateCount;

        var result = await RegisterAsync(harness);

        result.Outcome.Should().Be(RegistrationOutcome.AlreadyExists);
        result.HostId.Should().Be(existingId, "a caller needs to be able to point at the row that is in the way");
        result.Detail.Should().Contain("prod-host");

        harness.Repository.Rows.Should().ContainSingle("no second row may be created");
        harness.HostKeys.Pins.Count.Should().Be(pinsBefore, "a duplicate name must not re-pin anything");
        harness.Credentials.Imports.Count.Should().Be(importsBefore, "a duplicate name must not overwrite the stored key");
        harness.Refresher.InvalidateCount.Should().Be(invalidationsBefore);
    }

    [Fact]
    public async Task Registering_an_unreachable_host_is_reported_as_such_and_writes_nothing()
    {
        var harness = Build();
        harness.Probe.Answer = HostKeyObservation.Unreachable("10.0.0.4", 22, "Connection timed out");

        var result = await RegisterAsync(harness);

        result.Outcome.Should().Be(RegistrationOutcome.HostUnreachable);
        result.Detail.Should().Contain("Connection timed out");

        harness.HostKeys.Pins.Should().BeEmpty();
        harness.Credentials.Imports.Should().BeEmpty();
        harness.Repository.Rows.Should().BeEmpty();
        harness.Refresher.InvalidateCount.Should().Be(0);
    }

    /// <summary>
    /// A host that was reachable at probe time but is not at register time cannot be registered on the
    /// strength of the earlier observation alone — but only because the observation has expired; while it is
    /// live, reuse is the intended behaviour. This asserts the expired-and-now-unreachable combination.
    /// </summary>
    [Fact]
    public async Task An_expired_observation_for_a_now_unreachable_host_reports_HostUnreachable()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        harness.Time.Now += HostRegistrationService.ObservationLifetime + TimeSpan.FromMinutes(1);
        harness.Probe.Answer = HostKeyObservation.Unreachable("10.0.0.4", 22, "Connection refused");

        var result = await RegisterAsync(harness);

        result.Outcome.Should().Be(RegistrationOutcome.HostUnreachable);
        harness.Repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Registering_with_an_unparseable_endpoint_reports_InvalidEndpoint()
    {
        var harness = Build();
        harness.Probe.Answer = HostKeyObservation.InvalidEndpoint("nope::", "'nope::' has an invalid port ':'.");

        var result = await harness.Service.RegisterAsync(
            "prod-host", "nope::", RealFingerprint, PrivateKey, null, Actor);

        result.Outcome.Should().Be(RegistrationOutcome.InvalidEndpoint);
        harness.Repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Registering_with_an_empty_private_key_is_refused_before_anything_is_probed()
    {
        var harness = Build();

        var result = await harness.Service.RegisterAsync(
            "prod-host", Endpoint, RealFingerprint, ReadOnlyMemory<byte>.Empty, null, Actor);

        result.Outcome.Should().Be(RegistrationOutcome.InvalidCredential);
        harness.Probe.CallCount.Should().Be(0);
        harness.Repository.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("..")]
    public async Task A_name_that_is_not_a_valid_secret_urn_segment_is_refused(string name)
    {
        var harness = Build();

        var result = await RegisterAsync(harness, name: name);

        result.Outcome.Should().Be(RegistrationOutcome.InvalidName);
        harness.Credentials.Imports.Should().BeEmpty("a name that cannot form a URN must never reach the secret store");
        harness.Repository.Rows.Should().BeEmpty();
    }

    /// <summary>
    /// A failure before the host row exists is reportable, not fatal: Servyx's own tracking is untouched, so
    /// there is nothing inconsistent to explain and a retry is safe.
    /// </summary>
    [Fact]
    public async Task A_pin_failure_is_reported_as_Failed_with_no_row_created()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        harness.HostKeys.PinFailure = new IOException("the host key file is read-only");

        var result = await RegisterAsync(harness);

        result.Outcome.Should().Be(RegistrationOutcome.Failed);
        result.Detail.Should().Contain("read-only");
        harness.Repository.Rows.Should().BeEmpty();
        harness.Refresher.InvalidateCount.Should().Be(0);
    }

    /// <summary>
    /// The opposite side of that line: once side effects exist, a failed row write is a genuine fault and
    /// propagates, rather than being smoothed into a result record that hides what was left behind.
    /// </summary>
    [Fact]
    public async Task A_row_write_failure_after_the_credential_was_stored_propagates_rather_than_being_swallowed()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        harness.Repository.AddFailure = new InvalidOperationException("database is locked");

        var act = () => RegisterAsync(harness);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("database is locked");

        harness.Repository.Rows.Should().BeEmpty();
        harness.Refresher.InvalidateCount.Should().Be(0, "nothing became discoverable, so nothing should be refreshed");

        // The documented leftovers: an inert secret and a pin of a key the operator did confirm. Neither is
        // rolled back — un-pinning would mean revoking, which would falsely mark the host compromised.
        harness.Credentials.Imports.Should().ContainSingle();
        harness.HostKeys.Pins.Should().ContainSingle();
        harness.HostKeys.Revocations.Should().BeEmpty("a failed registration must not leave a revocation tombstone behind");
    }

    // ── Listing ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_reports_registered_hosts_without_their_credential_urns()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        await RegisterAsync(harness);

        var result = await harness.Service.ListAsync();

        result.ListingFailed.Should().BeFalse();
        var host = result.Hosts.Should().ContainSingle().Subject;
        host.Name.Should().Be("prod-host");
        host.Endpoint.Should().Be(Endpoint);
        host.TrustPolicy.Should().Be("requirePinned");
        host.PinnedFingerprints.Should().Be(RealFingerprint);
        host.Enabled.Should().BeTrue();
        host.RegisteredBy.Should().Be(Actor);
        host.RegisteredAt.Should().Be(harness.Time.Now);
    }

    [Fact]
    public async Task Registering_a_host_records_a_HostRegisteredAuditEntry()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        await RegisterAsync(harness);

        var entry = harness.AuditLogger.Entries.Should().ContainSingle().Which;
        entry.Actor.Should().Be(Actor);
        entry.Action.Should().Be(AuditActions.HostRegistered);
        entry.TargetType.Should().Be("host");
        entry.TargetId.Should().Be("prod-host");
        entry.Details.Should().Be(Endpoint);
    }

    [Fact]
    public async Task A_refused_registration_records_no_audit_entry()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);

        await RegisterAsync(harness, fingerprint: AttackerFingerprint);

        harness.AuditLogger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Listing_reports_a_read_failure_honestly_rather_than_as_an_empty_list()
    {
        var harness = Build();
        harness.Repository.ListFailure = new InvalidOperationException("database unavailable");

        var result = await harness.Service.ListAsync();

        result.ListingFailed.Should().BeTrue();
        result.Hosts.Should().BeEmpty();
        result.FailureDetail.Should().Contain("database unavailable");
    }

    // ── Deregistration ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deregistering_removes_the_row_and_refreshes_the_connection_set()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        await RegisterAsync(harness);

        var result = await harness.Service.DeregisterAsync("prod-host", Actor);

        result.Outcome.Should().Be(DeregistrationOutcome.Deregistered);
        harness.Repository.Rows.Should().BeEmpty();
        harness.Refresher.InvalidateCount.Should().Be(2, "once for the registration, once for the removal");
    }

    /// <summary>
    /// Mirrors <c>ServerAdoptionService.ForgetAsync</c>'s contract at the host level: forgetting is a change to
    /// Servyx's own bookkeeping and nothing else. Notably it does NOT revoke the pinned key — the only
    /// un-pinning mechanism available — because a revocation says "this host is compromised", which is a
    /// different and false claim to make just because an operator tidied a list.
    /// </summary>
    [Fact]
    public async Task Deregistering_leaves_the_stored_credential_and_the_pinned_key_untouched()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        await RegisterAsync(harness);

        await harness.Service.DeregisterAsync("prod-host", Actor);

        harness.HostKeys.Pins.Should().ContainSingle();
        harness.HostKeys.Revocations.Should().BeEmpty();
        harness.Credentials.Imports.Should().ContainSingle();
    }

    [Fact]
    public async Task Deregistering_a_host_that_was_never_registered_reports_NotFound_and_refreshes_nothing()
    {
        var harness = Build();

        var result = await harness.Service.DeregisterAsync("never-registered", Actor);

        result.Outcome.Should().Be(DeregistrationOutcome.NotFound);
        result.Detail.Should().Contain("never-registered");
        harness.Refresher.InvalidateCount.Should().Be(0);
    }

    [Fact]
    public async Task Deregistering_records_a_HostDeregisteredAuditEntry()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        await RegisterAsync(harness);
        harness.AuditLogger.Entries.Clear();

        await harness.Service.DeregisterAsync("prod-host", Actor);

        var entry = harness.AuditLogger.Entries.Should().ContainSingle().Which;
        entry.Actor.Should().Be(Actor);
        entry.Action.Should().Be(AuditActions.HostDeregistered);
        entry.TargetType.Should().Be("host");
        entry.TargetId.Should().Be("prod-host");
    }

    [Fact]
    public async Task Deregistering_an_unknown_host_records_no_audit_entry()
    {
        var harness = Build();

        await harness.Service.DeregisterAsync("never-registered", Actor);

        harness.AuditLogger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_can_be_reused_after_the_host_holding_it_is_deregistered()
    {
        var harness = Build();
        await harness.Service.ProbeAsync(Endpoint);
        await RegisterAsync(harness);
        await harness.Service.DeregisterAsync("prod-host", Actor);

        var result = await RegisterAsync(harness);

        result.Outcome.Should().Be(RegistrationOutcome.Registered);
        harness.Repository.Rows.Should().ContainSingle();
    }
}
