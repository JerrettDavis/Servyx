using Servyx.Composition;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The UI-facing label. <see cref="WritableServers.IsWritable"/> must mean "a live write control is safe to
/// render" — which a <see cref="WriteMode.PreviewOnly"/> server is NOT: every actual write still throws
/// <see cref="WritesDisabledException"/> at the transport for it, only planning is permitted.
/// </summary>
/// <remarks>
/// The important property here is no longer only the mapping, it is the <em>liveness</em>: this label used to
/// be a snapshot built from configuration at process start, so the READ-ONLY / WRITES ENABLED badge and every
/// gated control reported the world as of startup. A label is allowed to be a label; it is not allowed to lie.
/// </remarks>
public class WritableServersTests : IDisposable
{
    private const string ContainerId = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";

    private readonly WriteGrantTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private WriteGrantCache OpenCache() =>
        new(new ProvisioningGate(enabled: true), _database.Factory);

    [Fact]
    public void Preview_only_does_not_report_the_server_as_writable()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.PreviewOnly);

        var writable = WritableServers.Live(OpenCache());

        writable.IsWritable(ContainerId).Should().BeFalse(
            because: "a PreviewOnly server may plan, but every apply still throws at the transport — a page " +
                "that renders a live write control for it would be lying");
        writable.Mode(ContainerId).Should().Be(WriteMode.PreviewOnly,
            because: "the UI needs to distinguish preview-capable from fully writable, not collapse both into one bool");
    }

    [Fact]
    public void Enabled_reports_the_server_as_writable()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var writable = WritableServers.Live(OpenCache());

        writable.IsWritable(ContainerId).Should().BeTrue();
        writable.Mode(ContainerId).Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void An_unmentioned_server_is_read_only_and_reports_ReadOnly()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var writable = WritableServers.Live(OpenCache());

        writable.IsWritable("never-adopted").Should().BeFalse();
        writable.Mode("never-adopted").Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_container_name_never_satisfies_a_grant_keyed_on_a_container_id()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var writable = WritableServers.Live(OpenCache());

        writable.Mode(serverId: null, serverName: "palworld-server").Should().Be(WriteMode.ReadOnly,
            because: "a container name can be reassigned to a different workload outside Servyx at any time; " +
                "the label must answer exactly what the write guard would, and the guard matches on the id");
    }

    [Fact]
    public void A_closed_master_switch_reports_ReadOnly_for_a_server_the_database_says_is_Enabled()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var writable = WritableServers.Live(new WriteGrantCache(ProvisioningGate.Closed, _database.Factory));

        writable.Any.Should().BeFalse();
        writable.IsWritable(ContainerId).Should().BeFalse();
        writable.Mode(ContainerId).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public async Task The_label_reflects_a_grant_flipped_seconds_ago()
    {
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        var cache = OpenCache();
        var writable = WritableServers.Live(cache);

        writable.Any.Should().BeFalse("nothing has been granted yet");
        writable.IsWritable(ContainerId).Should().BeFalse();

        var granted = new WriteGrantService(
            new ProvisioningGate(enabled: true),
            _database.Repository,
            cache,
            new RecordingLogger());

        await granted.SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        writable.IsWritable(ContainerId).Should().BeTrue(
            because: "the badge and every gated control read this label; reporting the startup world would " +
                "leave the UI lying about a grant the operator just made");
        writable.Any.Should().BeTrue();
        writable.Keys.Should().ContainSingle().Which.Should().Be(ContainerId);
    }

    [Fact]
    public void The_fixed_set_still_grants_Enabled_for_a_named_key()
    {
        var writable = new WritableServers(["palworld-server"]);

        writable.IsWritable("palworld-server").Should().BeTrue();
        writable.Mode("palworld-server").Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void None_is_read_only_for_everything()
    {
        WritableServers.None.Any.Should().BeFalse();
        WritableServers.None.Mode("anything", "anything").Should().Be(WriteMode.ReadOnly);
    }
}
