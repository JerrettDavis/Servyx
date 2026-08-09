using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Web.Components.Pages.Servers;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the one control in the product that creates a write grant.
/// </summary>
/// <remarks>
/// Two properties matter most here, and both are about honesty rather than mechanics. First, with the
/// process-level master switch closed the control is <em>visible but locked</em>, and the lock's reason
/// names the exact configuration key an admin has to set — hiding the one control that explains the lock is
/// the pattern this product's README explicitly forbids. Second, granting is itself a mutating,
/// consequential act, so it takes two deliberate clicks and the confirmation copy states plainly what is
/// being permitted.
/// </remarks>
public class WriteModeControlTests : BunitContext
{
    private const string ContainerId = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>
    /// A hand-written <see cref="IWriteGrantService"/> rather than a substitute: these tests assert on the
    /// sequence of postures the control renders after applying, which is easier to read as a tiny in-memory
    /// implementation than as a stack of configured returns.
    /// </summary>
    private sealed class FakeWriteGrantService(ServerWriteMode initial, WriteGrantOutcome outcome = WriteGrantOutcome.Applied)
        : IWriteGrantService
    {
        private readonly ServerId _id = ServerId.New();

        public ServerWriteMode Mode { get; private set; } = initial;

        public string? Actor { get; private set; }

        public int Calls { get; private set; }

        public Task<WriteGrantResult> SetWriteModeAsync(
            ServerId id, ServerWriteMode mode, string actor, CancellationToken ct = default)
        {
            Calls++;
            Actor = actor;

            if (outcome != WriteGrantOutcome.Applied)
            {
                return Task.FromResult(new WriteGrantResult(outcome, ServerWriteMode.ReadOnly));
            }

            Mode = mode;
            return Task.FromResult(new WriteGrantResult(
                WriteGrantOutcome.Applied, mode, actor, new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero)));
        }

        public Task<WriteGrantState?> DescribeAsync(string containerId, CancellationToken ct = default) =>
            Task.FromResult<WriteGrantState?>(new WriteGrantState(
                _id,
                "palworld-server",
                containerId,
                Mode,
                Actor,
                Actor is null ? null : new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero)));
    }

    private sealed class UntrackedWriteGrantService : IWriteGrantService
    {
        public Task<WriteGrantResult> SetWriteModeAsync(
            ServerId id, ServerWriteMode mode, string actor, CancellationToken ct = default) =>
            Task.FromResult(new WriteGrantResult(WriteGrantOutcome.ServerNotFound, ServerWriteMode.ReadOnly));

        public Task<WriteGrantState?> DescribeAsync(string containerId, CancellationToken ct = default) =>
            Task.FromResult<WriteGrantState?>(null);
    }

    private IRenderedComponent<WriteModeControl> RenderControl(
        IWriteGrantService? service, ProvisioningGate gate)
    {
        if (service is not null)
        {
            Services.AddSingleton(service);
        }

        Services.AddSingleton(gate);

        return Render<WriteModeControl>(p => p.Add(x => x.ContainerId, ContainerId));
    }

    [Fact]
    public void The_selector_is_locked_and_the_reason_names_the_master_switch_key_when_it_is_closed()
    {
        var control = RenderControl(new FakeWriteGrantService(ServerWriteMode.ReadOnly), ProvisioningGate.Closed);

        var gated = control.Find("[data-testid=\"gated-control\"]");
        gated.HasAttribute("disabled").Should().BeTrue(
            because: "an operator must be able to see the control that would grant write access, and see " +
                "that it is locked, rather than have it silently absent");

        gated.GetAttribute("title").Should().Contain(ProvisioningGate.ConfigurationKey);

        control.Find("[data-testid=\"write-mode-master-switch-note\"]").TextContent
            .Should().Contain(ProvisioningGate.ConfigurationKey);
    }

    [Fact]
    public void The_review_button_is_locked_when_the_master_switch_is_closed()
    {
        var control = RenderControl(new FakeWriteGrantService(ServerWriteMode.ReadOnly), ProvisioningGate.Closed);

        control.Find("[data-testid=\"write-mode-review\"]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void The_current_posture_and_its_attribution_are_rendered()
    {
        var control = RenderControl(
            new FakeWriteGrantService(ServerWriteMode.PreviewOnly), new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-current\"]").TextContent.Should().Contain("Preview only");
        control.Find("[data-testid=\"write-mode-attribution\"]").TextContent
            .Should().Contain("no per-operator accounts",
                because: "WriteModeChangedBy always holds one value; implying per-user attribution would be a " +
                    "trap for whoever reads that column in a year");
    }

    [Fact]
    public void Selecting_the_current_posture_leaves_the_review_button_inert()
    {
        var control = RenderControl(
            new FakeWriteGrantService(ServerWriteMode.ReadOnly), new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-review\"]").HasAttribute("disabled").Should().BeTrue(
            because: "there is nothing to confirm until the operator picks a different tier");
    }

    [Fact]
    public void Granting_takes_two_deliberate_clicks_and_the_first_changes_nothing()
    {
        var service = new FakeWriteGrantService(ServerWriteMode.ReadOnly);
        var control = RenderControl(service, new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-tier-enabled\"] input").Change("Enabled");
        control.Find("[data-testid=\"write-mode-review\"]").Click();

        service.Calls.Should().Be(0, because: "nothing has happened yet — that is what the confirm step says");

        var confirmBody = control.Find("[data-testid=\"write-mode-confirm-body\"]").TextContent;
        confirmBody.Should().Contain("Nothing has changed yet");
        confirmBody.Should().Contain("write and delete files",
            because: "the copy has to say what granting actually permits, not just name a tier");
        confirmBody.Should().Contain("Data loss is possible");

        control.Find("[data-testid=\"write-mode-confirm\"]").Click();

        service.Calls.Should().Be(1);
        service.Mode.Should().Be(ServerWriteMode.Enabled);
        control.Find("[data-testid=\"write-mode-applied\"]").TextContent.Should().Contain("Writes enabled");
    }

    [Fact]
    public void The_confirmation_says_the_grant_is_bound_to_the_container_identity_and_is_process_local()
    {
        var control = RenderControl(
            new FakeWriteGrantService(ServerWriteMode.ReadOnly), new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-tier-enabled\"] input").Change("Enabled");
        control.Find("[data-testid=\"write-mode-review\"]").Click();

        var scope = control.Find("[data-testid=\"write-mode-confirm-scope\"]").TextContent;
        scope.Should().Contain("recreating the container returns it to read-only");
        scope.Should().Contain("renaming it keeps the grant");
        scope.Should().Contain("must be restarted",
            because: "a separate MCP host keeps its own cache, and the revoke direction of that gap is the " +
                "dangerous one — the operator has to be told at the moment they flip the grant");
    }

    [Fact]
    public void Cancelling_the_confirmation_writes_nothing_and_restores_the_current_posture()
    {
        var service = new FakeWriteGrantService(ServerWriteMode.ReadOnly);
        var control = RenderControl(service, new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-tier-enabled\"] input").Change("Enabled");
        control.Find("[data-testid=\"write-mode-review\"]").Click();
        control.Find("[data-testid=\"write-mode-cancel\"]").Click();

        service.Calls.Should().Be(0);
        service.Mode.Should().Be(ServerWriteMode.ReadOnly);
        control.Find("[data-testid=\"write-mode-review\"]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Revoking_is_offered_the_same_way_granting_is()
    {
        var service = new FakeWriteGrantService(ServerWriteMode.Enabled);
        var control = RenderControl(service, new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-tier-readonly\"] input").Change("ReadOnly");
        control.Find("[data-testid=\"write-mode-review\"]").Click();
        control.Find("[data-testid=\"write-mode-confirm\"]").Click();

        service.Mode.Should().Be(ServerWriteMode.ReadOnly);
        control.Find("[data-testid=\"write-mode-applied\"]").TextContent.Should().Contain("Read-only");
    }

    [Fact]
    public void The_actor_recorded_is_the_single_operator_identity_this_product_actually_has()
    {
        var service = new FakeWriteGrantService(ServerWriteMode.ReadOnly);
        var control = RenderControl(service, new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-tier-previewonly\"] input").Change("PreviewOnly");
        control.Find("[data-testid=\"write-mode-review\"]").Click();
        control.Find("[data-testid=\"write-mode-confirm\"]").Click();

        service.Actor.Should().Be(Servyx.Web.Authentication.OperatorAuthentication.OperatorNameClaimValue);
    }

    [Fact]
    public void A_container_Servyx_does_not_track_offers_no_control_at_all()
    {
        var control = RenderControl(new UntrackedWriteGrantService(), new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-untracked\"]").TextContent.Should().Contain("Adopt it");
        control.FindAll("[data-testid=\"write-mode-review\"]").Should().BeEmpty();
    }

    [Fact]
    public void A_host_that_composed_no_grant_service_still_renders_and_says_so()
    {
        var control = RenderControl(service: null, new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-unavailable\"]").TextContent
            .Should().Contain("read-only",
                because: "degrading closed and visibly is this codebase's convention everywhere else too");
    }

    [Fact]
    public void A_refused_change_is_surfaced_rather_than_reported_as_applied()
    {
        var service = new FakeWriteGrantService(ServerWriteMode.ReadOnly, WriteGrantOutcome.MasterSwitchClosed);
        var control = RenderControl(service, new ProvisioningGate(enabled: true));

        control.Find("[data-testid=\"write-mode-tier-enabled\"] input").Change("Enabled");
        control.Find("[data-testid=\"write-mode-review\"]").Click();
        control.Find("[data-testid=\"write-mode-confirm\"]").Click();

        control.Find("[data-testid=\"write-mode-error\"]").TextContent
            .Should().Contain(ProvisioningGate.ConfigurationKey);
        control.FindAll("[data-testid=\"write-mode-applied\"]").Should().BeEmpty();
    }
}
