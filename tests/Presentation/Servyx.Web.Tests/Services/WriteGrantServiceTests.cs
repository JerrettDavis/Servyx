using Microsoft.Extensions.Logging;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Web.Authentication;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The only sanctioned way a per-server write grant is created, changed, or revoked: what it persists, what
/// it refuses, what it records, and the ordering that makes a UI flip real before the call returns.
/// </summary>
public class WriteGrantServiceTests : IDisposable
{
    private const string ContainerId = "1111111111111111111111111111111111111111111111111111111111111111";

    private readonly WriteGrantTestDatabase _database = new();
    private readonly RecordingLogger _audit = new();

    public void Dispose() => _database.Dispose();

    private WriteGrantService Service(ProvisioningGate gate, WriteGrantCache? cache = null, TimeProvider? time = null) =>
        new(gate, _database.Repository, cache ?? new WriteGrantCache(gate, _database.Factory), _audit, time);

    [Fact]
    public async Task A_grant_records_the_actor_and_the_time_on_the_row_itself()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await Service(gate, time: clock).SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        result.Applied.Should().BeTrue();
        result.Mode.Should().Be(ServerWriteMode.Enabled);
        result.ChangedBy.Should().Be("operator");
        result.ChangedAt.Should().Be(clock.GetUtcNow());

        var row = _database.Reload(id);
        row.WriteMode.Should().Be(ServerWriteMode.Enabled);
        row.WriteModeChangedBy.Should().Be("operator",
            because: "a row must never end up carrying a grant with no record of who made it");
        row.WriteModeChangedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task A_revocation_is_attributed_the_same_way_a_grant_is()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        await Service(gate).SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        var row = _database.Reload(id);
        row.WriteMode.Should().Be(ServerWriteMode.ReadOnly);
        row.WriteModeChangedBy.Should().Be("operator");
        row.WriteModeChangedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_closed_master_switch_refuses_without_writing_anything()
    {
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        var result = await Service(ProvisioningGate.Closed, new WriteGrantCache(ProvisioningGate.Closed, null))
            .SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        result.Outcome.Should().Be(WriteGrantOutcome.MasterSwitchClosed);
        result.Applied.Should().BeFalse();

        var row = _database.Reload(id);
        row.WriteMode.Should().Be(ServerWriteMode.ReadOnly);
        row.WriteModeChangedBy.Should().BeNull(
            because: "refusing must leave no trace of a change that did not happen");

        _audit.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(ProvisioningGate.ConfigurationKey));
    }

    [Fact]
    public async Task A_server_that_is_not_tracked_cannot_be_granted_anything()
    {
        var gate = new ProvisioningGate(enabled: true);

        var result = await Service(gate).SetWriteModeAsync(ServerId.New(), ServerWriteMode.Enabled, "operator");

        result.Outcome.Should().Be(WriteGrantOutcome.ServerNotFound);
        result.Mode.Should().Be(ServerWriteMode.ReadOnly);
    }

    [Fact]
    public async Task An_applied_grant_is_recorded_under_the_shared_audit_event()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        await Service(gate).SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        var entry = _audit.Entries.Should().ContainSingle().Subject;
        entry.EventId.Should().Be(WriteGrantAudit.WriteModeGranted);
        entry.Level.Should().Be(LogLevel.Warning,
            because: "granting write access is the consequential direction and deserves more than Information");
        entry.Message.Should().Contain("palworld-server");
        entry.Message.Should().Contain(ContainerId);
        entry.Message.Should().Contain("operator");
        entry.Message.Should().Contain("Enabled");
    }

    [Fact]
    public async Task Returning_to_read_only_is_recorded_at_a_lower_level_than_granting()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        await Service(gate).SetWriteModeAsync(id, ServerWriteMode.ReadOnly, "operator");

        _audit.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task The_cache_is_invalidated_before_the_call_returns()
    {
        // Ordering is the property, not the invalidation itself: a caller that got a success back must be
        // able to rely on the next command anywhere in this process seeing the new posture.
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        var cache = new WriteGrantCache(gate, _database.Factory);
        cache.ModeFor(ContainerId).Should().Be(ServerWriteMode.ReadOnly);

        await Service(gate, cache).SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        cache.ModeFor(ContainerId).Should().Be(ServerWriteMode.Enabled);
    }

    [Fact]
    public async Task Describe_reports_the_persisted_row_including_its_attribution()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);
        var service = Service(gate);

        var before = await service.DescribeAsync(ContainerId);
        before.Should().NotBeNull();
        before!.Id.Should().Be(id);
        before.Name.Should().Be("palworld-server");
        before.Mode.Should().Be(ServerWriteMode.ReadOnly);
        before.ChangedBy.Should().BeNull();
        before.ChangedAt.Should().BeNull();

        await service.SetWriteModeAsync(id, ServerWriteMode.PreviewOnly, "operator");

        var after = await service.DescribeAsync(ContainerId);
        after!.Mode.Should().Be(ServerWriteMode.PreviewOnly);
        after.ChangedBy.Should().Be("operator");
        after.ChangedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Describe_reports_nothing_for_a_container_Servyx_does_not_track()
    {
        (await Service(new ProvisioningGate(enabled: true)).DescribeAsync("never-adopted")).Should().BeNull(
            because: "a container with no row has no grant to flip — the UI must say 'adopt it first', not " +
                "offer a control that would write nothing");
    }

    [Fact]
    public async Task An_empty_actor_is_refused_rather_than_recorded_as_blank()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        var act = async () => await Service(gate).SetWriteModeAsync(id, ServerWriteMode.Enabled, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// The grant audit constants live in <c>Servyx.Composition</c> because it sits below
    /// <c>Servyx.Web</c> and cannot reference it, and they deliberately mirror the web host's own. This is
    /// the test that stops the two copies from drifting apart into two audit streams.
    /// </summary>
    [Fact]
    public void The_grant_audit_constants_mirror_the_web_hosts_own()
    {
        WriteGrantAudit.LogCategory.Should().Be(OperatorAuthentication.AuditLogCategory);
        WriteGrantAudit.WriteModeGranted.Id.Should().Be(AuthenticationAudit.WriteModeGranted.Id);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
