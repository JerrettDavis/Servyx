using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Servers;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for <c>AdoptionPanel</c> — the whole of Phase 1's UI: list adoption candidates, adopt one
/// against a chosen game definition, view what is tracked, and forget a tracked server behind a two-step
/// confirm. <see cref="FakeServerAdoptionService"/> stands in for <c>IServerAdoptionService</c>; no
/// <c>GameDefinitionCatalog</c> is registered for most of these tests — the component must still work using
/// each candidate's own <see cref="AdoptionCandidate.SuggestedDefinitionIds"/> for preselection, and must
/// render gracefully (not throw) with no catalog registered at all, matching the "optional collaborator"
/// pattern <c>MainLayout</c>/<c>NavMenu</c>/<c>DeployPage</c> already use.
/// </summary>
public class AdoptionPanelTests : BunitContext
{
    private static AdoptionCandidate Candidate(string containerId = "container-1", string name = "palworld-server") =>
        new(containerId, name, "thijsvanloef/palworld-server-docker:latest", "running", ["palworld"]);

    private FakeServerAdoptionService RegisterFakeAdoptionService()
    {
        var fake = new FakeServerAdoptionService();
        Services.AddSingleton<IServerAdoptionService>(fake);
        return fake;
    }

    [Fact]
    public void Candidates_render_with_name_image_and_state()
    {
        var fake = RegisterFakeAdoptionService();
        fake.Candidates.Add(Candidate());

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-candidate-row]").Should().HaveCount(1));

        var row = cut.Find("[data-testid=adopt-candidate-row]");
        row.TextContent.Should().Contain("palworld-server");
        row.TextContent.Should().Contain("thijsvanloef/palworld-server-docker:latest");
        row.TextContent.Should().Contain("running");
    }

    [Fact]
    public void Empty_state_renders_when_there_are_no_candidates()
    {
        var fake = RegisterFakeAdoptionService();

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-empty-state]").Should().HaveCount(1));

        cut.FindAll("[data-testid=adopt-candidate-row]").Should().BeEmpty();
        cut.Markup.Should().Contain("No game definitions are loaded");
    }

    /// <summary>
    /// Defect 1 regression (candidates side — the same anti-pattern already fixed on the tracked side):
    /// when discovery fails (Docker unreachable, permission denied, etc.), the panel must render a
    /// distinguishable "candidates unavailable" message — never the genuine empty state, which would tell
    /// the operator "no containers available to adopt" when the truth is "Servyx could not look".
    /// </summary>
    [Fact]
    public void An_honest_unavailable_message_renders_when_reading_candidates_fails()
    {
        var fake = RegisterFakeAdoptionService();
        fake.CandidatesFailureDetail = "daemon unreachable";

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-unavailable]").Should().HaveCount(1));

        cut.FindAll("[data-testid=adopt-empty-state]").Should().BeEmpty();
        cut.FindAll("[data-testid=adopt-candidate-row]").Should().BeEmpty();
        cut.Find("[data-testid=adopt-unavailable-detail]").TextContent.Should().Contain("daemon unreachable");
    }

    [Fact]
    public void Nothing_registered_renders_gracefully_instead_of_throwing()
    {
        // No IServerAdoptionService registered at all — the fail-closed path every existing ServersList
        // bUnit test already relies on implicitly (none of them register one either).
        var cut = Render<AdoptionPanel>();

        cut.Markup.Should().NotContain("adopt-section");
        cut.FindAll("[data-testid=adopt-candidate-row]").Should().BeEmpty();
    }

    [Fact]
    public void Adopt_invokes_the_service_with_the_selected_definition_id()
    {
        var fake = RegisterFakeAdoptionService();
        fake.Candidates.Add(Candidate());

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-button]").Should().HaveCount(1));

        cut.Find("[data-testid=adopt-button]").Click();

        cut.WaitForAssertion(() => fake.AdoptCalls.Should().ContainSingle());
        fake.AdoptCalls[0].Should().Be(("container-1", "palworld"));
    }

    [Fact]
    public void A_successfully_adopted_candidate_moves_into_the_tracked_list()
    {
        var fake = RegisterFakeAdoptionService();
        fake.Candidates.Add(Candidate());

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-button]").Should().HaveCount(1));
        cut.Find("[data-testid=adopt-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tracked-server-row]").Should().HaveCount(1));
        cut.FindAll("[data-testid=adopt-candidate-row]").Should().BeEmpty();
    }

    [Fact]
    public void An_adoption_failure_renders_the_result_detail_and_does_not_add_a_tracked_row()
    {
        var fake = RegisterFakeAdoptionService();
        fake.Candidates.Add(Candidate());
        fake.AdoptResultFactory = (_, _) => AdoptionResult.ContainerNotFound("container-1");

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-button]").Should().HaveCount(1));
        cut.Find("[data-testid=adopt-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adopt-error]").Should().HaveCount(1));
        cut.Find("[data-testid=adopt-error]").TextContent.Should().Contain("was not found");
        cut.FindAll("[data-testid=tracked-server-row]").Should().BeEmpty();
    }

    [Fact]
    public void Empty_state_renders_when_nothing_is_tracked()
    {
        var fake = RegisterFakeAdoptionService();

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tracked-empty-state]").Should().HaveCount(1));
    }

    /// <summary>
    /// Defect 1 regression: when Servyx's own tracking read fails (e.g. an unwritable data directory), the
    /// panel must render a distinguishable "tracking is unavailable" message — never the genuine empty
    /// state, and never an unhandled exception. Rendering the genuine empty state here would tell the
    /// operator "nothing tracked" when the truth is "Servyx cannot currently tell".
    /// </summary>
    [Fact]
    public void An_honest_unavailable_message_renders_when_reading_tracked_servers_fails()
    {
        var fake = RegisterFakeAdoptionService();
        fake.TrackedFailureDetail = "database is unwritable";

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tracked-unavailable]").Should().HaveCount(1));

        cut.FindAll("[data-testid=tracked-empty-state]").Should().BeEmpty();
        cut.FindAll("[data-testid=tracked-server-row]").Should().BeEmpty();
        cut.Find("[data-testid=tracked-unavailable-detail]").TextContent.Should().Contain("database is unwritable");
    }

    [Fact]
    public void Forget_requires_the_second_confirm_click()
    {
        var fake = RegisterFakeAdoptionService();
        var id = ServerId.New();
        fake.Tracked.Add(new TrackedServer(id, "palworld-server", "palworld", AdoptionMode.Adopted, ServerWriteMode.ReadOnly));

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-button]").Should().HaveCount(1));

        // First click reveals the confirm step; nothing is called yet.
        cut.Find("[data-testid=forget-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-confirm-step]").Should().HaveCount(1));
        fake.ForgetCalls.Should().BeEmpty();

        // The confirm copy is explicit that the container itself is untouched.
        cut.Find("[data-testid=forget-confirm-step]").TextContent.Should().Contain("does").And.Contain("not");
        cut.Find("[data-testid=forget-confirm-step]").TextContent.Should().Contain("keeps running exactly as it is");

        // Second click actually calls Forget.
        cut.Find("[data-testid=forget-confirm]").Click();
        cut.WaitForAssertion(() => fake.ForgetCalls.Should().ContainSingle());
        fake.ForgetCalls[0].Should().Be(id);
    }

    [Fact]
    public void Cancelling_the_forget_confirm_step_calls_nothing()
    {
        var fake = RegisterFakeAdoptionService();
        var id = ServerId.New();
        fake.Tracked.Add(new TrackedServer(id, "palworld-server", "palworld", AdoptionMode.Adopted, ServerWriteMode.ReadOnly));

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-button]").Should().HaveCount(1));
        cut.Find("[data-testid=forget-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-confirm-step]").Should().HaveCount(1));

        cut.Find("[data-testid=forget-cancel]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-confirm-step]").Should().BeEmpty());
        fake.ForgetCalls.Should().BeEmpty();
        cut.FindAll("[data-testid=tracked-server-row]").Should().HaveCount(1);
    }

    [Fact]
    public void A_successful_forget_removes_the_tracked_row()
    {
        var fake = RegisterFakeAdoptionService();
        var id = ServerId.New();
        fake.Tracked.Add(new TrackedServer(id, "palworld-server", "palworld", AdoptionMode.Adopted, ServerWriteMode.ReadOnly));

        var cut = Render<AdoptionPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-button]").Should().HaveCount(1));
        cut.Find("[data-testid=forget-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=forget-confirm]").Should().HaveCount(1));
        cut.Find("[data-testid=forget-confirm]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tracked-server-row]").Should().BeEmpty());
        cut.FindAll("[data-testid=tracked-empty-state]").Should().HaveCount(1);
    }
}
