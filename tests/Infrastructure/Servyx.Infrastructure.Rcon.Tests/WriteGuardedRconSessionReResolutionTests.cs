using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The RCON-path half of per-command re-resolution, at the unit level: a guard built over a live posture
/// source re-reads it on every gated call rather than holding the value it was built with.
/// </summary>
/// <remarks>
/// This is a second, entirely independent capture site from the exec path's
/// <c>WriteGuardedExecutionTarget</c>, and the two fail independently — fixing only one would leave mutating
/// control commands (<c>save</c>, <c>broadcast</c>, <c>shutdown</c>) flowing on a grant the operator already
/// revoked. RCON sessions are memoized per channel for the life of the process and are never evicted once
/// acquisition succeeds, so a value baked in at build time would outlive any number of grant changes.
/// </remarks>
public class WriteGuardedRconSessionReResolutionTests
{
    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
    ]);

    [Fact]
    public async Task A_revoked_grant_is_refused_on_the_next_command_of_the_same_session()
    {
        var mode = WriteMode.Enabled;
        var inner = new ScriptedRconSession();
        var guarded = new WriteGuardedRconSession(inner, Palworld(), () => mode, "palworld-server");

        await guarded.InvokeAsync("save", null);

        mode = WriteMode.ReadOnly;

        var act = async () => await guarded.InvokeAsync("save", null);
        await act.Should().ThrowAsync<WritesDisabledException>();

        inner.Invoked.Should().ContainSingle(
            because: "the guard refuses before the session, the secret store, or the socket is touched at all");
    }

    [Fact]
    public async Task A_new_grant_is_honoured_on_the_next_command_of_the_same_session()
    {
        var mode = WriteMode.ReadOnly;
        var inner = new ScriptedRconSession();
        var guarded = new WriteGuardedRconSession(inner, Palworld(), () => mode, "palworld-server");

        var refused = async () => await guarded.InvokeAsync("save", null);
        await refused.Should().ThrowAsync<WritesDisabledException>();

        mode = WriteMode.Enabled;

        await guarded.InvokeAsync("save", null);
        inner.Invoked.Should().ContainSingle();
    }

    [Fact]
    public async Task A_raw_line_follows_the_same_live_posture()
    {
        var mode = WriteMode.Enabled;
        var inner = new ScriptedRconSession();
        var guarded = new WriteGuardedRconSession(inner, Palworld(), () => mode, "palworld-server");

        // The double refuses the raw escape hatch itself, so reaching NotSupportedException is exactly the
        // proof that the guard let the call through.
        var permitted = async () => await guarded.SendRawAsync("Save");
        await permitted.Should().ThrowAsync<NotSupportedException>();

        mode = WriteMode.ReadOnly;

        var refused = async () => await guarded.SendRawAsync("Save");
        await refused.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task A_read_only_command_never_reads_the_posture_at_all()
    {
        var reads = 0;
        var inner = new ScriptedRconSession();
        var guarded = new WriteGuardedRconSession(
            inner,
            Palworld(),
            () =>
            {
                reads++;
                return WriteMode.ReadOnly;
            },
            "palworld-server");

        await guarded.InvokeAsync("info", null);

        reads.Should().Be(0,
            because: "the definition declares 'info' readOnly, so there is nothing to gate — and a read-only " +
                "server that could not be queried would defeat the purpose of the read-only tier");
    }

    [Fact]
    public void The_reported_mode_tracks_the_live_source_rather_than_a_captured_value()
    {
        var mode = WriteMode.PreviewOnly;
        var guarded = new WriteGuardedRconSession(
            new ScriptedRconSession(), Palworld(), () => mode, "palworld-server");

        guarded.Mode.Should().Be(WriteMode.PreviewOnly);
        guarded.WritesPermitted.Should().BeFalse();

        mode = WriteMode.Enabled;

        guarded.Mode.Should().Be(WriteMode.Enabled);
        guarded.WritesPermitted.Should().BeTrue();
    }

    [Fact]
    public void A_null_live_source_is_refused_at_construction()
    {
        ((Action)(() => _ = new WriteGuardedRconSession(
            new ScriptedRconSession(), Palworld(), (Func<WriteMode>)null!, "palworld-server")))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_fixed_mode_constructor_still_holds_one_posture_for_its_lifetime()
    {
        var guarded = new WriteGuardedRconSession(
            new ScriptedRconSession(), Palworld(), WriteMode.Enabled, "palworld-server");

        guarded.Mode.Should().Be(WriteMode.Enabled);
    }
}
