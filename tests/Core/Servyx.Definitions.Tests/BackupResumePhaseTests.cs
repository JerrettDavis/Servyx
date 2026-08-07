using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// Covers <c>backup.resume</c>, the after-capture counterpart to <c>backup.quiesce</c>.
/// </summary>
/// <remarks>
/// <para>
/// The block exists because <c>quiesce</c> alone can only express half of the canonical safe-backup
/// sequence — disable saving, flush, copy, re-enable saving. A definition that used <c>quiesce</c> to turn
/// saving off had no way to turn it back on, so the first backup would leave the server unable to persist
/// anything until it was restarted, and nothing would say so.
/// </para>
/// <para>
/// Every test here mutates the real shipped definition rather than hand-authoring a miniature document, so
/// what is being parsed is a realistic file that differs from the shipped one in exactly one place. The
/// first test below is the compatibility guarantee: the shipped file declares no <c>resume</c> at all and
/// must keep parsing with zero Errors.
/// </para>
/// </remarks>
public class BackupResumePhaseTests
{
    /// <summary>The shipped <c>quiesce</c> block, used as the anchor every mutation below is spliced onto.</summary>
    private const string ShippedQuiesce = "  quiesce:\n    - { kind: control, channel: rcon, command: save, timeout: 30s }\n";

    private static DefinitionParseResult ParseYaml(string yaml) => new GameDefinitionYamlParser().Parse(yaml);

    private static IReadOnlyList<ValidationIssue> ErrorsOf(DefinitionParseResult result) =>
        [.. result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error)];

    /// <summary>
    /// The whole compatibility contract for this feature in one assertion: <c>resume</c> is optional, and
    /// the definition that shipped before it existed parses exactly as it did.
    /// </summary>
    [Fact]
    public void Backup_WithNoResumeBlock_ParsesWithNoErrorsAndAnEmptyResumeList()
    {
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        ErrorsOf(result).Should().BeEmpty();
        result.Definition.Should().NotBeNull();
        result.Definition!.Backup.Resume.Should().BeEmpty();
        result.Definition.Backup.Quiesce.Should().HaveCount(1);
    }

    [Fact]
    public void BackupResume_WithControlSteps_ParsesThemInDeclaredOrder()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            ShippedQuiesce,
            ShippedQuiesce
            + "  resume:\n"
            + "    - { kind: control, channel: rcon, command: save, timeout: 30s }\n"
            + "    - { kind: control, channel: rcon, command: broadcast, timeout: 10s }\n");

        var result = ParseYaml(yaml);

        ErrorsOf(result).Should().BeEmpty();
        result.Definition.Should().NotBeNull();

        var resume = result.Definition!.Backup.Resume;
        resume.Should().HaveCount(2);
        resume[0].Should().Be(new QuiesceStep.Control("rcon", "save", TimeSpan.FromSeconds(30)));
        resume[1].Should().Be(new QuiesceStep.Control("rcon", "broadcast", TimeSpan.FromSeconds(10)));

        // The quiesce block is untouched by the new key: the two phases are independent lists.
        result.Definition.Backup.Quiesce.Should().HaveCount(1);
    }

    /// <summary>
    /// A resume step's channel/command pair is cross-referenced against <c>control.channels</c> through the
    /// same deferred queue quiesce uses, so an undeclared command is an Error here too — and the message
    /// names <c>backup.resume</c>, not <c>backup.quiesce</c>, so the operator is sent to the right block.
    /// </summary>
    [Fact]
    public void BackupResume_ReferencingAnUndeclaredCommand_IsErrorNamingTheResumeBlock()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            ShippedQuiesce,
            ShippedQuiesce
            + "  resume:\n"
            + "    - { kind: control, channel: rcon, command: nosuchcommand, timeout: 30s }\n");

        var result = ParseYaml(yaml);

        result.Definition.Should().BeNull();
        ErrorsOf(result).Should().ContainSingle(i =>
            i.Message.Contains("'backup.resume'", StringComparison.Ordinal) &&
            i.Message.Contains("nosuchcommand", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupResume_ReferencingAnUndeclaredChannel_IsErrorNamingTheResumeBlock()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            ShippedQuiesce,
            ShippedQuiesce
            + "  resume:\n"
            + "    - { kind: control, channel: nosuchchannel, command: save, timeout: 30s }\n");

        var result = ParseYaml(yaml);

        result.Definition.Should().BeNull();
        ErrorsOf(result).Should().ContainSingle(i =>
            i.Message.Contains("'backup.resume'", StringComparison.Ordinal) &&
            i.Message.Contains("nosuchchannel", StringComparison.Ordinal));
    }

    /// <summary>
    /// This parser treats an unknown key as a hard Error everywhere else; the new block is registered
    /// explicitly rather than permissively ignoring what it does not recognise, so a typo inside a resume
    /// entry fails the same way it would inside a quiesce entry.
    /// </summary>
    [Fact]
    public void BackupResume_WithAnUnknownKeyInAStep_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            ShippedQuiesce,
            ShippedQuiesce
            + "  resume:\n"
            + "    - { kind: control, channel: rcon, command: save, timeout: 30s, retries: 3 }\n");

        var result = ParseYaml(yaml);

        result.Definition.Should().BeNull();
        ErrorsOf(result).Should().Contain(i =>
            i.Message.Contains("'backup.resume'", StringComparison.Ordinal) &&
            i.Message.Contains("retries", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reason the key had to be registered rather than merely tolerated: a misspelled block would
    /// otherwise be silently ignored, and its absence would only show up as "the server never started saving
    /// again" long after the backup reported success.
    /// </summary>
    [Fact]
    public void Backup_WithAMisspelledResumeKey_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            ShippedQuiesce,
            ShippedQuiesce
            + "  resumes:\n"
            + "    - { kind: control, channel: rcon, command: save, timeout: 30s }\n");

        var result = ParseYaml(yaml);

        result.Definition.Should().BeNull();
        ErrorsOf(result).Should().Contain(i => i.Message.Contains("resumes", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupResume_WithANonControlKind_IsErrorNamingTheResumeBlock()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            ShippedQuiesce,
            ShippedQuiesce
            + "  resume:\n"
            + "    - { kind: shell, channel: rcon, command: save, timeout: 30s }\n");

        var result = ParseYaml(yaml);

        result.Definition.Should().BeNull();
        ErrorsOf(result).Should().Contain(i =>
            i.Message.Contains("A 'backup.resume' entry declares 'kind: shell'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Guards the shared-parser refactor from regressing the block it was extracted out of: quiesce
    /// diagnostics must still name <c>backup.quiesce</c>, never the generalized path.
    /// </summary>
    [Fact]
    public void BackupQuiesce_Diagnostics_StillNameTheQuiesceBlock()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "    - { kind: control, channel: rcon, command: save, timeout: 30s }\n  adopt:",
            "    - { kind: shell, channel: rcon, command: save, timeout: 30s }\n  adopt:");

        var result = ParseYaml(yaml);

        ErrorsOf(result).Should().Contain(i =>
            i.Message.Contains("A 'backup.quiesce' entry declares 'kind: shell'", StringComparison.Ordinal));
    }
}
