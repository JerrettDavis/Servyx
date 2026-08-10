using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Web.Components.Pages.Games;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for <c>ImportDefinitionDialog</c> — Phase 5's paste/upload-YAML import surface. Every
/// call goes through <see cref="FakeDefinitionImportService"/>, so these tests exercise the component's
/// rendering and event wiring only; <see cref="Servyx.Definitions.Tests"/>' own
/// <c>DefinitionImportServiceTests</c> cover the real validation/write/refresh/security behaviour.
/// </summary>
public class ImportDefinitionDialogTests : BunitContext
{
    private FakeDefinitionImportService RegisterFake()
    {
        var fake = new FakeDefinitionImportService();
        Services.AddSingleton<IDefinitionImportService>(fake);
        return fake;
    }

    [Fact]
    public void Nothing_registered_renders_gracefully_instead_of_throwing()
    {
        // No IDefinitionImportService registered — matches AdoptionPanel's own "optional collaborator"
        // fail-closed pattern for a host (or test harness) that has not wired the service up.
        var cut = Render<ImportDefinitionDialog>();

        cut.Markup.Should().NotContain("import-section");
        cut.FindAll("[data-testid=import-textarea]").Should().BeEmpty();
    }

    [Fact]
    public void Import_button_is_disabled_until_text_is_entered()
    {
        RegisterFake();
        var cut = Render<ImportDefinitionDialog>();

        cut.Find("[data-testid=import-button]").HasAttribute("disabled").Should().BeTrue();

        cut.Find("[data-testid=import-textarea]").Change("apiVersion: servyx.dev/v1");

        cut.Find("[data-testid=import-button]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Clicking_import_calls_the_service_with_the_pasted_text_and_overwrite_false()
    {
        var fake = RegisterFake();
        var cut = Render<ImportDefinitionDialog>();

        cut.Find("[data-testid=import-textarea]").Change("apiVersion: servyx.dev/v1\nkind: GameDefinition");
        cut.Find("[data-testid=import-button]").Click();

        fake.Calls.Should().ContainSingle();
        fake.Calls[0].Yaml.Should().Be("apiVersion: servyx.dev/v1\nkind: GameDefinition");
        fake.Calls[0].Overwrite.Should().BeFalse();
    }

    [Fact]
    public void A_successful_import_shows_success_clears_the_textarea_and_notifies_the_host_page()
    {
        var fake = RegisterFake();
        var notified = 0;

        var cut = Render<ImportDefinitionDialog>(parameters => parameters
            .Add(p => p.OnImported, () => notified++));

        cut.Find("[data-testid=import-textarea]").Change("apiVersion: servyx.dev/v1");
        cut.Find("[data-testid=import-button]").Click();

        cut.Find("[data-testid=import-success]").TextContent.Should().Contain("imported");
        cut.Find("[data-testid=import-textarea]").GetAttribute("value").Should().BeNullOrEmpty();
        notified.Should().Be(1);
    }

    [Fact]
    public void A_validation_failure_renders_every_issue_with_line_column_severity_and_message_as_a_list()
    {
        var fake = RegisterFake();
        var report = new ValidationReport(false,
        [
            new ValidationIssue("Unknown field 'privleged'.", 12, 5, ValidationSeverity.Error),
            new ValidationIssue("'signature' is declared but not verified.", 40, 1, ValidationSeverity.Warning),
        ]);
        fake.ResultFactory = (_, _, _) => new DefinitionImportResult(
            DefinitionImportOutcome.ValidationFailed, report, null, null, "The definition failed validation and was not written.");

        var cut = Render<ImportDefinitionDialog>();
        cut.Find("[data-testid=import-textarea]").Change("bad: yaml");
        cut.Find("[data-testid=import-button]").Click();

        cut.Find("[data-testid=import-error]").TextContent.Should().Contain("failed validation");

        var rows = cut.FindAll("[data-testid=import-issue-row]");
        rows.Should().HaveCount(2);

        rows[0].TextContent.Should().Contain("Line 12").And.Contain("column 5").And.Contain("Unknown field 'privleged'.");
        rows[0].GetAttribute("data-severity").Should().Be(nameof(ValidationSeverity.Error));

        rows[1].TextContent.Should().Contain("Line 40").And.Contain("column 1").And.Contain("not verified");
        rows[1].GetAttribute("data-severity").Should().Be(nameof(ValidationSeverity.Warning));

        // Nothing was written — no success notification should have gone out and the textarea keeps the
        // rejected text so the operator can fix it in place rather than retyping it.
        cut.FindAll("[data-testid=import-success]").Should().BeEmpty();
        cut.Find("[data-testid=import-textarea]").GetAttribute("value").Should().Be("bad: yaml");
    }

    [Fact]
    public void A_duplicate_id_offers_an_explicit_overwrite_action_rather_than_silently_replacing()
    {
        var fake = RegisterFake();
        fake.ResultFactory = (_, _, overwrite) => overwrite
            ? new DefinitionImportResult(DefinitionImportOutcome.Imported, null, "dup-game", "dup-game.yaml", "'dup-game' was imported and is now available in the catalog.")
            : new DefinitionImportResult(DefinitionImportOutcome.DuplicateId, null, "dup-game", "dup-game.yaml", "A definition with id 'dup-game' already exists. Nothing was written.");

        var cut = Render<ImportDefinitionDialog>();
        cut.Find("[data-testid=import-textarea]").Change("id: dup-game");
        cut.Find("[data-testid=import-button]").Click();

        cut.Find("[data-testid=import-error]").TextContent.Should().Contain("already exists");
        cut.FindAll("[data-testid=import-overwrite-button]").Should().ContainSingle();
        fake.Calls.Should().ContainSingle(c => !c.Overwrite);

        cut.Find("[data-testid=import-overwrite-button]").Click();

        fake.Calls.Should().Contain(c => c.Overwrite);
        cut.Find("[data-testid=import-success]").TextContent.Should().Contain("imported");
        cut.FindAll("[data-testid=import-overwrite-button]").Should().BeEmpty();
    }

    [Fact]
    public void The_overwrite_action_is_not_offered_for_a_plain_validation_failure()
    {
        var fake = RegisterFake();
        fake.ResultFactory = (_, _, _) => new DefinitionImportResult(
            DefinitionImportOutcome.ValidationFailed,
            new ValidationReport(false, [new ValidationIssue("Bad.", 1, 1, ValidationSeverity.Error)]),
            null, null, "The definition failed validation and was not written.");

        var cut = Render<ImportDefinitionDialog>();
        cut.Find("[data-testid=import-textarea]").Change("bad");
        cut.Find("[data-testid=import-button]").Click();

        cut.FindAll("[data-testid=import-overwrite-button]").Should().BeEmpty();
    }
}
