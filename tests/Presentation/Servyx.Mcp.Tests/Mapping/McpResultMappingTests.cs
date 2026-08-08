using System.Reflection;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Mcp;
using Servyx.Mcp.Tests.Support;

namespace Servyx.Mcp.Tests.Mapping;

/// <summary>
/// Pins <see cref="ResultMapping"/>'s contract: one <c>Outcome</c> discriminant per union case, the union's
/// own <c>Message</c> crossing verbatim, and — the one hazard this file exists specifically to catch — a
/// backup-prune preview never populating <c>Removed</c> and an applied prune never populating
/// <c>Candidates</c>.
/// </summary>
public sealed class McpResultMappingTests
{
    [Fact]
    public void BackupListResult_cases_map_to_distinct_outcomes()
    {
        var listed = ResultMapping.Map(new BackupListResult.Listed([], []));
        var failed = ResultMapping.Map(new BackupListResult.Failed("boom", "IOException"));

        listed.Outcome.Should().Be("listed");
        failed.Outcome.Should().Be("failed");
        listed.Outcome.Should().NotBe(failed.Outcome);
    }

    [Fact]
    public void BackupListResult_message_crosses_verbatim()
    {
        var domain = new BackupListResult.Failed("disk full", "IOException");
        var mapped = ResultMapping.Map(domain);

        mapped.Message.Should().Be(domain.Message);
        mapped.Detail.Should().Be("disk full");
        mapped.FailureKind.Should().Be("IOException");
    }

    [Fact]
    public void BackupInspectResult_cases_map_to_distinct_outcomes()
    {
        var inspected = ResultMapping.Map(new BackupInspectResult.Inspected("b1", ["a", "b"]));
        var failed = ResultMapping.Map(new BackupInspectResult.Failed("boom", "IOException"));

        inspected.Outcome.Should().Be("inspected");
        failed.Outcome.Should().Be("failed");
        inspected.Message.Should().Be(new BackupInspectResult.Inspected("b1", ["a", "b"]).Message);
        inspected.Entries.Should().Equal("a", "b");
    }

    [Fact]
    public void RestorePlanResult_cases_map_to_distinct_outcomes()
    {
        var plan = new RestorePlan("plan1", "backup1", ["path/a", "path/b"]);
        var planned = ResultMapping.Map(new RestorePlanResult.Planned(plan));
        var failed = ResultMapping.Map(new RestorePlanResult.Failed("boom", "IOException"));

        planned.Outcome.Should().Be("planned");
        planned.PlanId.Should().Be("plan1");
        planned.BackupId.Should().Be("backup1");
        planned.AffectedPaths.Should().Equal("path/a", "path/b");
        failed.Outcome.Should().Be("failed");
    }

    [Fact]
    public void BackupPruneResult_all_four_cases_map_to_distinct_outcomes()
    {
        var previewed = ResultMapping.Map(new BackupPruneResult.Previewed(["c1"], SkippedForeign: 1));
        var pruned = ResultMapping.Map(new BackupPruneResult.Pruned(["r1"], SkippedForeign: 2));
        var refused = ResultMapping.Map(new BackupPruneResult.RefusedForeign(["f1"]));
        var failed = ResultMapping.Map(new BackupPruneResult.Failed("boom", "IOException"));

        var outcomes = new[] { previewed.Outcome, pruned.Outcome, refused.Outcome, failed.Outcome };
        outcomes.Should().OnlyHaveUniqueItems("each of the four BackupPruneResult cases must map to its own outcome");

        previewed.Outcome.Should().Be("previewed");
        pruned.Outcome.Should().Be("pruned");
        refused.Outcome.Should().Be("refused-foreign");
        failed.Outcome.Should().Be("failed");
    }

    [Fact]
    public void Prune_preview_populates_Candidates_and_never_Removed()
    {
        var previewed = ResultMapping.Map(new BackupPruneResult.Previewed(["c1", "c2"], SkippedForeign: 0));

        previewed.Candidates.Should().Equal("c1", "c2");
        previewed.Removed.Should().BeNull(
            "a dry-run preview must never populate Removed — that would report a plan as a deletion");
    }

    [Fact]
    public void Applied_prune_populates_Removed_and_never_Candidates()
    {
        var pruned = ResultMapping.Map(new BackupPruneResult.Pruned(["r1", "r2"], SkippedForeign: 0));

        pruned.Removed.Should().Equal("r1", "r2");
        pruned.Candidates.Should().BeNull(
            "an applied prune must never populate Candidates — that would report a deletion as a dry run");
    }

    [Fact]
    public void RefusedForeign_reports_its_foreign_ids_and_never_touches_Candidates_or_Removed()
    {
        var refused = ResultMapping.Map(new BackupPruneResult.RefusedForeign(["f1", "f2"]));

        refused.ForeignIds.Should().Equal("f1", "f2");
        refused.Candidates.Should().BeNull();
        refused.Removed.Should().BeNull();
    }

    [Fact]
    public void No_response_record_declares_a_field_named_success()
    {
        var offenders = new List<string>();

        foreach (var type in IlScanner.LoadableTypes(typeof(ServyxMcpServer).Assembly))
        {
            if (type.Namespace is null || !type.Namespace.StartsWith("Servyx.Mcp", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (string.Equals(property.Name, "Success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "IsSuccess", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every result union crosses as a named Outcome string, never a bare bool success — found: " +
            string.Join(", ", offenders));
    }
}
