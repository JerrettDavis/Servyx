using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Persistence;
using Servyx.Infrastructure.Persistence.Configuration;

namespace Servyx.Config.Tests;

/// <summary>
/// Covers <see cref="PlanExecutor.PreviewAsync"/>: the multi-binding collection
/// <see cref="SettingDescriptor.WritableSurface"/> would silently truncate, the refusals that keep a plan
/// from promising a write it cannot make, secret masking of the persisted diff, consequence derivation
/// across a transitive <c>derivedFrom</c> chain (including a malformed cyclic one), and the guarantee that
/// preview touches no game server.
/// </summary>
public class PlanExecutorTests
{
    private const string ContainerId = "container-1";
    private const string ComposeDirectory = "/opt/servyx/pal";
    private const string DataDirectory = "/palworld";

    /// <summary>Encodes fixture text without prepending a BOM, so a test that wants one writes it explicitly.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const string Env = """
        # The image's source of truth.
        SERVER_NAME=Authoritative Name
        ADMIN_PASSWORD=hunter2
        PORT=8211
        """;

    private const string Compose = """
        services:
          palworld:
            image: thijsvanloef/palworld-server-docker
            ports:
              - "8211:8211/udp"
        """;

    private const string Ini = """
        [/Script/Pal.PalGameWorldSettings]
        OptionSettings=(Difficulty=None,ServerName="Rendered Name",PublicPort=8211)
        """;

    // ── 1. Every write binding is collected, not just the first ────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_ForAPortWithTwoWriteBindings_PlansTheEnvWriteAndBlocksTheComposeOne()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("PORT", "8300"));

        // SettingDescriptor.WritableSurface would have returned only the env binding and dropped the compose
        // one on the floor — the operator would see one green row and never learn the port was not published.
        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "env");
        plan.Blocked.Should().ContainSingle(b => b.SurfaceId == "compose");
        plan.Feasibility.Should().Be(PlanFeasibility.PartiallyAchievable);

        var blocked = plan.Blocked.Single();
        blocked.SettingKey.Should().Be("PORT");
        blocked.Reason.Should().Contain("not an addressable value");
        blocked.RemediationHint.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_TheBlockedComposePointer_NamesTheSequenceContainerRatherThanThrowing()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("PORT", "8300"));

        // '/services/palworld/ports' is a sequence CONTAINER. The YAML adapter registers spans for scalars
        // only, so there is no span to splice — detected from the span set before any write is attempted,
        // never by catching KeyNotFoundException out of ConfigDocument.WithValue.
        plan.Blocked.Single().Reason.Should().Contain("/services/palworld/ports");
        plan.Blocked.Single().RemediationHint.Should().Contain("publish-udp");
    }

    [Fact]
    public async Task PreviewAsync_ForASettingThatRequiresRecreate_SaysSo()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("PORT", "8300"));

        plan.RequiresRecreate.Should().BeTrue();
        plan.Consequences.Should().Contain(c => c.Kind == ConsequenceKind.RecreateRequired);
    }

    [Fact]
    public async Task PreviewAsync_WithNothingBlocked_ReportsFullyAchievable()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Blocked.Should().BeEmpty();
        plan.Feasibility.Should().Be(PlanFeasibility.FullyAchievable);
        plan.IsFullyReversible.Should().BeTrue();
    }

    [Fact]
    public async Task PreviewAsync_WhenTheDesiredValueIsAlreadyInPlace_PlansNothingAndBlocksNothing()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("SERVER_NAME", "Authoritative Name"));

        // Not blocked — nothing is obstructing it — and not an action either: writing identical bytes to a
        // game server's config file is a mutation with no purpose.
        plan.Actions.Should().BeEmpty();
        plan.Blocked.Should().BeEmpty();
        plan.Feasibility.Should().Be(PlanFeasibility.FullyAchievable);
    }

    [Fact]
    public async Task PreviewAsync_ForAKeyTheDefinitionDoesNotDeclare_BlocksItRatherThanIgnoringIt()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("NOT_A_SETTING", "x"));

        plan.Actions.Should().BeEmpty();
        plan.Blocked.Should().ContainSingle(b => b.SettingKey == "NOT_A_SETTING");
        plan.Feasibility.Should().Be(PlanFeasibility.Blocked);
    }

    // ── 2. Managed-block preservation ──────────────────────────────────────────────────────────────────

    private const string ManagedEnv = """
        # A header Servyx does not own.
        UNMANAGED_BEFORE=keep-me-exactly
        # >>> servyx:managed >>>
        SERVER_NAME=Authoritative Name
        # <<< servyx:managed <<<
        UNMANAGED_AFTER=keep-me-too
        """;

    [Fact]
    public async Task PreviewAsync_UnderAManagedBlockPolicy_LeavesEveryLineOutsideTheRegionByteIdentical()
    {
        var harness = new Harness(envContent: ManagedEnv, envPolicy: MergePolicy.ManagedBlock);

        await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        var action = harness.Store.Actions.Single(a => a.SurfaceId == "env");

        // Split on the exact terminator, with NO ReplaceLineEndings normalization — normalizing first would
        // make the comparison blind to a post-image that silently rewrote every line ending.
        //
        // Scope, precisely: the ManagedEnv fixture is a raw string literal, which on this checkout (no
        // .gitattributes normalizing the source file) is LF-only. So what this actually proves today is that
        // no CRLF is INTRODUCED — not that an existing CRLF survives. The latter is proven by
        // PreviewAsync_ForACrlfThroughoutFile_IsNotRefused, which builds its fixture from explicit "\r\n"
        // and is therefore independent of how this source file happens to be checked out. Kept split on '\n'
        // rather than normalized so that if the fixture ever does arrive as CRLF, this test tightens rather
        // than silently stops checking.
        var before = action.PreImageContent!.Split('\n');
        var after = action.PostImageContent!.Split('\n');

        after.Should().HaveCount(before.Length);
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].StartsWith("SERVER_NAME=", StringComparison.Ordinal))
            {
                after[i].Should().Be(before[i].Replace("Authoritative Name", "A New Name", StringComparison.Ordinal));
                continue;
            }

            // Byte-identical, carriage returns and all.
            after[i].Should().Be(before[i]);
        }

        action.PostImageContent.Should().Contain("UNMANAGED_BEFORE=keep-me-exactly");
        action.PostImageContent.Should().Contain("UNMANAGED_AFTER=keep-me-too");
        action.PostImageContent.Should().Contain("# A header Servyx does not own.");
    }

    // ── 2b. Byte fidelity at the preview -> apply seam ─────────────────────────────────────────────────

    /// <summary>
    /// Pins the pre/post image against the file's real bytes across the four shapes a config surface
    /// realistically arrives in.
    /// </summary>
    /// <remarks>
    /// <strong>Only the two BOM cases are regression detectors for the BOM-stripping defect</strong> — those
    /// two fail against the old read path, which consumed the BOM before the adapter ever saw it. The two
    /// no-BOM cases pass under both the old and the new code: they are genuine fidelity assertions and they
    /// lock the behaviour in, but they would not have caught the bug that prompted this theory. Line-ending
    /// fidelity is covered separately and deliberately, by the CRLF/LF/mixed trio above — this theory's CRLF
    /// rows exercise it but are not what proves it.
    /// </remarks>
    [Theory]
    [InlineData("\n", false, "LF, no BOM")]
    [InlineData("\r\n", false, "CRLF, no BOM")]
    [InlineData("\n", true, "LF with a UTF-8 BOM")]
    [InlineData("\r\n", true, "CRLF with a UTF-8 BOM")]
    public async Task PreviewAsync_PreservesLineEndingsAndTheByteOrderMark_Exactly(
        string newline, bool bom, string description)
    {
        var body = string.Join(newline, ["SERVER_NAME=Authoritative Name", "ADMIN_PASSWORD=hunter2", "PORT=8211"]);
        var content = (bom ? "﻿" : string.Empty) + body;
        var harness = new Harness(envContent: content);

        await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        var action = harness.Store.Actions.Single(a => a.SurfaceId == "env");

        // The pre-image must BE the file, byte for byte — that is what RevertAsync will restore.
        action.PreImageContent.Should().Be(content, because: $"the surface is {description}");
        Encoding.UTF8.GetBytes(action.PreImageContent!).Should().Equal(Encoding.UTF8.GetBytes(content));

        // The post-image must differ from the file in exactly the approved value and nothing else: same BOM,
        // same terminators. Apply writes these bytes verbatim, so anything else here is an unapproved edit.
        var expectedAfter = (bom ? "﻿" : string.Empty)
            + string.Join(newline, ["SERVER_NAME=A New Name", "ADMIN_PASSWORD=hunter2", "PORT=8211"]);

        action.PostImageContent.Should().Be(expectedAfter);
        action.PostImageContent!.StartsWith('﻿').Should().Be(bom);
        action.PostImageContent.Contains("\r\n", StringComparison.Ordinal).Should().Be(newline == "\r\n");
    }

    [Fact]
    public async Task PreviewAsync_HashesRawBytes_InTheSameDomainAndFormatEveryTransportUses()
    {
        const string body = "SERVER_NAME=Authoritative Name\nADMIN_PASSWORD=hunter2\nPORT=8211";
        const string content = "﻿" + body;
        var harness = new Harness(envContent: content);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));
        var action = harness.Store.Actions.Single(a => a.SurfaceId == "env");

        // Bare lower-case hex over the RAW bytes — byte-for-byte what LocalExecutionTarget, SftpFileChannel,
        // ShellFileChannel and DockerExecutionTarget each compute, so a persisted PreImageHash can be handed
        // straight to a transport's pre-image check. A "sha256:" prefix, or a digest over decoded text (which
        // would drop the BOM), would make that comparison fail on every file.
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        action.PreImageHash.Should().Be(expected);
        action.PreImageHash.Should().MatchRegex("^[0-9a-f]{64}$");
        action.PreImageHash.Should().NotStartWith("sha256:");

        // One surface cannot have two digests within one plan.
        plan.SurfaceHashes["env"].Should().Be(action.PreImageHash);

        action.PostImageHash.Should().Be(Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(action.PostImageContent!))));
    }

    [Fact]
    public async Task PreviewAsync_ForACrlfThroughoutFile_IsNotRefused()
    {
        // THE OVER-REFUSAL BOUNDARY, asserted directly rather than inferred. The mixed-ending guard must
        // catch only genuinely mixed files: an all-CRLF .env is the normal Windows case and has to stay
        // fully manageable. A guard that refused it would be its own outage, and the byte-fidelity theory
        // below would not say so in as many words — it would just stop passing, for reasons a reader would
        // have to reconstruct.
        const string crlf = "SERVER_NAME=Authoritative Name\r\nADMIN_PASSWORD=hunter2\r\nPORT=8211\r\n";
        var harness = new Harness(envContent: crlf);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Blocked.Should().BeEmpty();
        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "env");
        plan.Feasibility.Should().Be(PlanFeasibility.FullyAchievable);

        // And the CRLF convention survives the edit rather than being normalized to LF on the way out.
        var action = harness.Store.Actions.Single();
        action.PostImageContent.Should().Be(
            "SERVER_NAME=A New Name\r\nADMIN_PASSWORD=hunter2\r\nPORT=8211\r\n");
        action.PostImageContent.Should().NotMatchRegex("(?<!\r)\n");
    }

    [Fact]
    public async Task PreviewAsync_ForAnLfThroughoutFile_IsNotRefused()
    {
        const string lf = "SERVER_NAME=Authoritative Name\nADMIN_PASSWORD=hunter2\nPORT=8211\n";
        var harness = new Harness(envContent: lf);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Blocked.Should().BeEmpty();
        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "env");

        // No stray carriage returns introduced on the way out either.
        harness.Store.Actions.Single().PostImageContent.Should().NotContain("\r");
    }

    [Fact]
    public async Task PreviewAsync_ForAMixedLineEndingFile_RefusesRatherThanSilentlyReformattingIt()
    {
        // ConfigDocument.Render joins with a single dominant terminator, so this file cannot round-trip.
        // Applying a post-image derived from it would rewrite every line ending alongside the one approved
        // value — a whole-file reformat nobody saw in the diff.
        const string mixed = "SERVER_NAME=Authoritative Name\r\nADMIN_PASSWORD=hunter2\nPORT=8211\n";
        var harness = new Harness(envContent: mixed);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Actions.Should().BeEmpty();
        plan.Feasibility.Should().Be(PlanFeasibility.Blocked);
        plan.Blocked.Single().Reason.Should().Contain("round trip");
    }

    [Fact]
    public async Task PreviewAsync_ForAUtf16Surface_RefusesRatherThanTranscodingIt()
    {
        var harness = new Harness(envBytes: Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("SERVER_NAME=Authoritative Name\n")).ToArray());

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Actions.Should().BeEmpty();
        plan.Blocked.Single().Reason.Should().Contain("UTF-16");
    }

    [Fact]
    public async Task PreviewAsync_ForAnInvalidUtf8Surface_RefusesRatherThanSubstitutingReplacementCharacters()
    {
        // 0xFF is never valid UTF-8. A replacing decoder would turn it into U+FFFD and the post-image would
        // overwrite a real byte with a question mark.
        var harness = new Harness(envBytes: [.. Encoding.UTF8.GetBytes("SERVER_NAME=abc"), 0xFF, .. Encoding.UTF8.GetBytes("\n")]);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Actions.Should().BeEmpty();
        plan.Blocked.Single().Reason.Should().Contain("not valid UTF-8");
    }

    [Fact]
    public async Task PreviewAsync_UnderAManagedBlockPolicy_BlocksAWriteThatFallsOutsideTheRegion()
    {
        // ADMIN_PASSWORD sits outside the managed markers here, so the merger must refuse it — and the
        // refusal must be attributed to that one setting rather than sinking the whole surface's plan.
        const string mixed = """
            ADMIN_PASSWORD=hunter2
            # >>> servyx:managed >>>
            SERVER_NAME=Authoritative Name
            # <<< servyx:managed <<<
            """;

        var harness = new Harness(envContent: mixed, envPolicy: MergePolicy.ManagedBlock);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("ADMIN_PASSWORD", "rotated"));

        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "env");
        plan.Blocked.Should().ContainSingle(b => b.SettingKey == "ADMIN_PASSWORD");
        plan.Blocked.Single().Reason.Should().Contain("ManagedBlock");
        plan.Feasibility.Should().Be(PlanFeasibility.PartiallyAchievable);
    }

    // ── 3. Secrets ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_ForASecret_MasksBothSidesOfTheDiff_AndStillShowsThatItChanged()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("ADMIN_PASSWORD", "a-brand-new-password"));

        var diff = plan.Actions.Single().UnifiedDiff;

        diff.Should().NotContain("hunter2");
        diff.Should().NotContain("a-brand-new-password");
        diff.Should().Contain(PlanExecutor.SecretMask);

        // Masking both sides to the same token would make the two lines compare equal and the diff would
        // contain no hunk at all — a masked secret must still be visibly a change.
        diff.Should().Contain(PlanExecutor.ChangedSecretMask);
    }

    [Fact]
    public async Task PreviewAsync_MasksASecretItIsNotEvenWriting_BecauseDiffContextLinesPrintIt()
    {
        var harness = new Harness();

        // SERVER_NAME sits one line above ADMIN_PASSWORD, well inside the diff's context window.
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Actions.Single().UnifiedDiff.Should().NotContain("hunter2");
        plan.Actions.Single().UnifiedDiff.Should().Contain(PlanExecutor.SecretMask);
    }

    [Fact]
    public async Task PreviewAsync_ForASecret_PersistsAMaskedDiffAndFlagsTheRow()
    {
        var harness = new Harness();

        await harness.PreviewAsync(("ADMIN_PASSWORD", "a-brand-new-password"));

        var row = harness.Store.Actions.Single();

        // The stored diff can never leak, even transiently: masking happens while the diff is being built,
        // before the row is constructed.
        row.UnifiedDiff.Should().NotContain("hunter2");
        row.UnifiedDiff.Should().NotContain("a-brand-new-password");
        row.ContainsSecrets.Should().BeTrue();

        // The images are deliberately NOT masked — an exact revert needs the real bytes, which is exactly
        // what ContainsSecrets exists to warn a read path about.
        row.PreImageContent.Should().Contain("hunter2");
        row.PostImageContent.Should().Contain("a-brand-new-password");
    }

    [Fact]
    public async Task PreviewAsync_WithADuplicatedSecretKey_MasksEveryOccurrence_NotJustTheOneAWriteWouldEdit()
    {
        // Hand-edited .env files really do end up with a key twice. ConfigDocument.WithValue edits only the
        // LAST span (last-wins semantics, correct for a write), so masking through the merger would mask the
        // second ADMIN_PASSWORD and print the first one in plaintext into a persisted database column.
        const string duplicated = """
            ADMIN_PASSWORD=first-password
            SERVER_NAME=Authoritative Name
            ADMIN_PASSWORD=second-password
            """;

        var harness = new Harness(envContent: duplicated);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        var diff = plan.Actions.Single().UnifiedDiff;
        diff.Should().NotContain("first-password");
        diff.Should().NotContain("second-password");

        harness.Store.Actions.Single().UnifiedDiff.Should().NotContain("first-password");
        harness.Store.Actions.Single().UnifiedDiff.Should().NotContain("second-password");
    }

    [Fact]
    public async Task PreviewAsync_ForAKeyAbsentFromTheFile_SaysItIsAbsent_NotThatItIsACollection()
    {
        var harness = new Harness(envContent: "PORT=8211");

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        var blocked = plan.Blocked.Single();
        blocked.SettingKey.Should().Be("SERVER_NAME");

        // The old wording talked about collections and multi-line values, which is nonsense for a key that is
        // simply not in the file and would send an operator hunting for something that was never involved.
        blocked.Reason.Should().Contain("contains no 'SERVER_NAME' entry");
        blocked.Reason.Should().NotContain("collection");
        blocked.RemediationHint.Should().Contain("by hand");
    }

    // ── 4. A derived surface is never written ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_ForAWriteBindingOnADerivedSurface_BlocksItRatherThanPlanningOrThrowing()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("DERIVED_WRITE", "Hard"));

        plan.Actions.Should().BeEmpty();
        plan.Blocked.Should().ContainSingle();

        var blocked = plan.Blocked.Single();
        blocked.SurfaceId.Should().Be("palworldsettings");
        blocked.Reason.Should().Contain("Derived");
        blocked.Reason.Should().Contain("regenerates");

        // The remediation an operator can actually act on is the upstream surface the definition declares.
        blocked.RemediationHint.Should().Contain("'env'");
    }

    [Fact]
    public async Task PreviewAsync_ForADerivedSurfaceUnderAProfileWhereItIsAuthoritative_PlansTheWrite()
    {
        // Palworld's 'palworldsettings' is Derived under the docker profile and Authoritative under the
        // process profile. The refusal must follow the resolved role for THIS server's profile, never a
        // hardcoded assumption about a surface id.
        var harness = new Harness(iniRole: SurfaceRole.Authoritative);

        var plan = await harness.PreviewAsync(("DERIVED_WRITE", "Hard"));

        plan.Blocked.Should().BeEmpty();
        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "palworldsettings");

        // Written through the codec, not as a naive key/value line: the value lives inside a single packed
        // scalar, and a bare 'Difficulty=Hard' key is not something the workload reads.
        harness.Store.Actions.Single().PostImageContent.Should().Contain("Difficulty=Hard");
        harness.Store.Actions.Single().PostImageContent.Should().Contain("OptionSettings=(");
    }

    // ── 5. Capabilities ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_WhenTheSessionCannotWriteFiles_BlocksTheChangeWithTheMissingCapability()
    {
        var harness = new Harness(composeSessionCapabilities: TransportCapabilities.FileRead);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.Actions.Should().BeEmpty();
        plan.Feasibility.Should().Be(PlanFeasibility.Blocked);

        var blocked = plan.Blocked.Single();
        blocked.SurfaceId.Should().Be("env");
        blocked.Reason.Should().Contain("FileWrite");
        blocked.RemediationHint.Should().NotBeEmpty();
    }

    // ── 6. Consequences ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_DerivesTheRestartConsequenceFromTheDefinitionsOwnRegenerationTrigger()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.RequiresRestart.Should().BeTrue();

        // The trigger's own description text, not a sentence invented here.
        plan.Consequences.Should().Contain(c =>
            c.Kind == ConsequenceKind.RestartRequired
            && c.Description == "Regenerated from .env by the image entrypoint on every start.");
    }

    [Fact]
    public async Task PreviewAsync_WalksDerivedFromTransitively_NotOneHop()
    {
        // c (authoritative) <- b (derived from c) <- a (derived from b). Writing c must surface BOTH
        // downstream triggers; a one-hop walk would find only b's.
        var harness = new Harness(surfaces: Chain(), settings: ChainSettings(), files: ChainFiles());

        var plan = await harness.PreviewAsync(("CHAINED", "new-value"));

        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "c");
        plan.Consequences.Should().Contain(x => x.Description == "b is regenerated when the container restarts.");
        plan.Consequences.Should().Contain(x => x.Description == "a is regenerated when the process restarts.");
    }

    [Fact]
    public async Task PreviewAsync_WithACycleInDerivedFrom_TerminatesAndReportsTheDefectRatherThanHanging()
    {
        var harness = new Harness(surfaces: Cyclic(), settings: ChainSettings(), files: ChainFiles());

        var plan = await harness.PreviewAsync(("CHAINED", "new-value"));

        // Terminating is necessary but not sufficient: a definition defect silently absorbed would leave an
        // operator with a quietly incomplete consequence list and no way to know it.
        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "c");

        plan.Diagnostics.Should().ContainSingle(d => d.Kind == PlanDiagnosticKind.DefinitionDefect);

        var defect = plan.Diagnostics.Single(d => d.Kind == PlanDiagnosticKind.DefinitionDefect);
        defect.Message.Should().Contain("cycle");
        defect.Message.Should().Contain("'a'");
        defect.Message.Should().Contain("'b'");

        // The consequences reachable without the cyclic edge are still derived, not discarded.
        plan.Consequences.Should().Contain(x => x.Description == "b is regenerated when the container restarts.");
    }

    [Fact]
    public async Task PreviewAsync_WithADiamondInDerivedFrom_DoesNotMistakeItForACycle()
    {
        var harness = new Harness(surfaces: Diamond(), settings: ChainSettings(), files: ChainFiles());

        var plan = await harness.PreviewAsync(("CHAINED", "new-value"));

        // Revisiting a node is normal in a diamond. Reporting that as a malformed definition would cry wolf,
        // which is why cycle detection colours the current path rather than using a plain visited set.
        plan.Diagnostics.Should().NotContain(d => d.Kind == PlanDiagnosticKind.DefinitionDefect);
        plan.Consequences.Should().Contain(x => x.Description == "b is regenerated when the container restarts.");
    }

    [Fact]
    public async Task PreviewAsync_ForAManuallyRegeneratedDownstreamSurface_SaysSoInsteadOfStayingSilent()
    {
        var harness = new Harness(surfaces: ManualChain(), settings: ChainSettings(), files: ChainFiles());

        var plan = await harness.PreviewAsync(("CHAINED", "new-value"));

        plan.Actions.Should().ContainSingle(a => a.SurfaceId == "c");

        // Not RestartRequired: no restart regenerates a manual surface, and saying otherwise would send an
        // operator to reboot a workload for nothing. Not silence either: silence reads as "this takes effect
        // immediately", which is precisely what a manual trigger means it will not.
        plan.RequiresRestart.Should().BeFalse();
        plan.Consequences.Should().NotContain(c => c.Kind == ConsequenceKind.ServiceInterruption);

        plan.Diagnostics.Should().ContainSingle(d => d.Kind == PlanDiagnosticKind.ManualRegenerationRequired);

        var note = plan.Diagnostics.Single(d => d.Kind == PlanDiagnosticKind.ManualRegenerationRequired);
        note.SurfaceId.Should().Be("b");
        note.Message.Should().Contain("by hand");
    }

    // ── 7. Persistence ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_PersistsThePlan_AndTheReturnedIdReadsTheSamePlanAndActionsBackOut()
    {
        using var database = new PlanDatabase();
        var harness = new Harness(store: new EfChangePlanStore(database.Factory), serverSettings: database.Settings);

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("ADMIN_PASSWORD", "rotated"));

        var stored = await new EfChangePlanStore(database.Factory)
            .TryGetAsync(ChangePlanId.Parse(plan.Id));

        stored.Should().NotBeNull();
        stored!.Plan.Id.ToString().Should().Be(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Previewed);
        stored.Plan.ServerId.Should().Be(database.ServerRowId);
        stored.Plan.DefinitionId.Should().Be("palworld");
        stored.Plan.DefinitionVersion.Should().Be("sha256:test");
        stored.Plan.CreatedBy.Should().Be(PlanExecutor.DefaultActor);

        stored.Actions.Should().HaveCount(plan.Actions.Count);
        stored.Actions.Select(a => a.SurfaceId).Should().BeEquivalentTo(plan.Actions.Select(a => a.SurfaceId));
        stored.Actions.Select(a => a.Ordinal).Should().BeInAscendingOrder();
        stored.Actions.Should().OnlyContain(a => a.Status == ChangePlanActionStatus.Pending);
        stored.Actions.Should().OnlyContain(a => a.PreImageHash!.Length == 64);
        stored.Actions.Should().OnlyContain(a => a.PostImageHash!.Length == 64);
        stored.Actions.Single().UnifiedDiff.Should().NotContain("hunter2");

        // Diagnostics now survive the round trip. Losing them is the single most dangerous omission on a
        // re-read plan: a manual-regeneration note is exactly what makes an otherwise ordinary-looking plan
        // not take effect, and its absence reads as "this applies immediately".
        stored.Plan.DiagnosticsJson.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PreviewAsync_PersistsDiagnostics_SoAReReadPlanStillCarriesTheManualRegenerationWarning()
    {
        var harness = new Harness(surfaces: ManualChain(), settings: ChainSettings(), files: ChainFiles());

        var plan = await harness.PreviewAsync(("CHAINED", "new-value"));

        plan.Diagnostics.Should().ContainSingle(d => d.Kind == PlanDiagnosticKind.ManualRegenerationRequired);

        // Without a DiagnosticsJson column this note existed only on the returned object and vanished the
        // moment the plan was read back — leaving a plan that looks unconditionally applicable and is not.
        harness.Store.Plan!.DiagnosticsJson.Should().Contain("ManualRegenerationRequired");
        harness.Store.Plan.DiagnosticsJson.Should().Contain("by hand");
    }

    [Fact]
    public async Task PreviewAsync_WithNoDiagnostics_PersistsAnEmptyArrayRatherThanNull()
    {
        var harness = new Harness();

        await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        harness.Store.Plan!.DiagnosticsJson.Should().Be("[]");
    }

    [Fact]
    public async Task PreviewAsync_ComputesExpiryFromTheInjectedClockAndTheEntitysOwnTtl()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        var harness = new Harness(time: clock);

        await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        harness.Store.Plan!.CreatedAt.Should().Be(clock.Now);
        harness.Store.Plan.ExpiresAt.Should().Be(clock.Now + ChangePlanRecord.DefaultTtl);
    }

    [Fact]
    public async Task PreviewAsync_RecordsAHashOfEverySurfaceItRead()
    {
        var harness = new Harness();

        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        plan.SurfaceHashes.Should().ContainKey("env");

        // Bare lower-case hex, the format every transport's own file digest uses — see the hashing test above.
        plan.SurfaceHashes["env"].Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ── 8. Preview never writes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAsync_NeverTouchesAMutatingMemberOfTheTarget()
    {
        var harness = new Harness();

        // Enough desired values to exercise every surface, every adapter, the codec path, and the blocked
        // paths — so "no write happened" is a statement about a real plan, not about a no-op.
        var plan = await harness.PreviewAsync(
            ("SERVER_NAME", "A New Name"),
            ("ADMIN_PASSWORD", "rotated"),
            ("PORT", "8300"),
            ("DERIVED_WRITE", "Hard"));

        plan.Actions.Should().NotBeEmpty();
        harness.Mutations.Should().BeEmpty();
        harness.Reads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ApplyAsync_AndRevertAsync_RefuseClearly_RatherThanSilentlyDoingNothing()
    {
        var harness = new Harness();
        var executor = harness.Executor;

        var apply = async () => await executor.ApplyAsync("any-plan");
        var revert = async () => await executor.RevertAsync("any-plan");

        (await apply.Should().ThrowAsync<NotImplementedException>()).Which.Message.Should().Contain("not implemented");
        (await revert.Should().ThrowAsync<NotImplementedException>()).Which.Message.Should().Contain("not implemented");
    }

    [Fact]
    public async Task PreviewAsync_ForAnUntrackedServer_RefusesLoudlyRatherThanReturningAnUnstorablePlan()
    {
        var harness = new Harness(tracked: false);

        var preview = async () => await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // A ConfigChangePlan whose Id names no stored row would break the one guarantee the contract makes
        // about that id, so this is refused rather than degraded.
        (await preview.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("tracks no server");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        private readonly FakeTarget _data;
        private readonly FakeTarget _compose;

        public Harness(
            string? envContent = null,
            byte[]? envBytes = null,
            MergePolicy envPolicy = MergePolicy.PreserveUnknown,
            SurfaceRole iniRole = SurfaceRole.Derived,
            TransportCapabilities? composeSessionCapabilities = null,
            IReadOnlyList<DeclaredConfigSurface>? surfaces = null,
            IReadOnlyList<SettingDescriptor>? settings = null,
            Dictionary<string, string>? files = null,
            IChangePlanStore? store = null,
            IServerSettingsService? serverSettings = null,
            TimeProvider? time = null,
            bool tracked = true)
        {
            // Content is held as BYTES, not strings, so a test can pin a BOM, a CRLF terminator or an
            // outright invalid encoding — none of which survives being modelled as a string.
            var text = files ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".env"] = envContent ?? Env,
                ["compose.yaml"] = Compose,
                ["Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"] = Ini,
            };

            var content = text.ToDictionary(
                pair => pair.Key,
                pair => Utf8NoBom.GetBytes(pair.Value),
                StringComparer.Ordinal);

            if (envBytes is not null)
            {
                content[".env"] = envBytes;
            }

            _data = new FakeTarget(content);
            _compose = new FakeTarget(content);

            var declared = surfaces ?? Surfaces(envPolicy, iniRole);
            var catalogue = settings ?? Settings();

            var contexts = new MappedContexts
            {
                [_data] = new SurfaceResolutionContext(
                    TransportCapabilities.FileRead
                        | TransportCapabilities.FileWrite
                        | TransportCapabilities.ContainerScopedFiles,
                    SessionRoot: DataDirectory,
                    DataDirectory: DataDirectory,
                    ComposeDirectory: null,
                    DataDirectoryIsContainerScoped: true),
                [_compose] = new SurfaceResolutionContext(
                    composeSessionCapabilities
                        ?? (TransportCapabilities.FileRead | TransportCapabilities.FileWrite),
                    SessionRoot: ComposeDirectory,
                    DataDirectory: null,
                    ComposeDirectory: ComposeDirectory,
                    DataDirectoryIsContainerScoped: false),
            };

            var adapters = new IConfigAdapter[]
            {
                new DotEnvConfigAdapter(),
                new IniConfigAdapter(),
                new PropertiesConfigAdapter(),
                new JsonConfigAdapter(),
                new YamlConfigAdapter(),
            };

            IConfigValueCodec[] codecs = [new UnrealOptionSettingsCodec()];

            Store = store as RecordingStore ?? new RecordingStore();

            Executor = new PlanExecutor(
                new StubSessions(new ServerConfigSessions(
                    [
                        new ConfigSession(_data, "the deployment's data directory"),
                        new ConfigSession(_compose, "the host compose directory"),
                    ],
                    declared)),
                new StubCatalog(new ServerPlanCatalog("palworld", "sha256:test", catalogue)),
                new SurfaceResolver(contexts, adapters),
                serverSettings ?? new StubServerSettings(tracked),
                new ConfigMerger(codecs),
                store ?? Store,
                adapters,
                codecs,
                time);
        }

        public PlanExecutor Executor { get; }

        public RecordingStore Store { get; }

        public IReadOnlyList<string> Mutations => [.. _data.Mutations, .. _compose.Mutations];

        public int Reads => _data.Reads + _compose.Reads;

        public Task<ConfigChangePlan> PreviewAsync(params (string Key, string Value)[] desired) =>
            Executor.PreviewAsync(
                ContainerId,
                desired.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal));
    }

    // ── Surface sets ───────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DeclaredConfigSurface> Surfaces(MergePolicy envPolicy, SurfaceRole iniRole) =>
    [
        new(
            "env",
            SurfaceRole.Authoritative,
            SurfaceFormat.Dotenv,
            Codec: null,
            CodecPath: null,
            new SurfaceLocator.HostFile("${COMPOSE_DIR}/.env"),
            ManagedSubtree: null,
            envPolicy,
            DerivedFrom: [],
            Regeneration: null),
        new(
            "compose",
            SurfaceRole.Authoritative,
            SurfaceFormat.Yaml,
            Codec: null,
            CodecPath: null,
            new SurfaceLocator.HostFile("${COMPOSE_DIR}/compose.yaml"),
            ManagedSubtree: "services.palworld",
            MergePolicy.PreserveUnknown,
            DerivedFrom: [],
            Regeneration: null),
        new(
            "palworldsettings",
            iniRole,
            SurfaceFormat.Ini,
            Codec: "unreal-option-settings",
            CodecPath: """["/Script/Pal.PalGameWorldSettings"].OptionSettings""",
            new SurfaceLocator.HostFile("${DATA_DIR}/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"),
            ManagedSubtree: null,
            MergePolicy.PreserveUnknown,
            DerivedFrom: iniRole == SurfaceRole.Derived ? ["env"] : [],
            iniRole == SurfaceRole.Derived
                ? new RegenerationTrigger(
                    RegenerationKind.ContainerRestart,
                    "Regenerated from .env by the image entrypoint on every start.")
                : null),
    ];

    private static Dictionary<string, string> ChainFiles() => new(StringComparer.Ordinal)
    {
        [".env"] = "CHAINED=old-value",
    };

    private static DeclaredConfigSurface ChainRoot() => new(
        "c",
        SurfaceRole.Authoritative,
        SurfaceFormat.Dotenv,
        Codec: null,
        CodecPath: null,
        new SurfaceLocator.HostFile("${COMPOSE_DIR}/.env"),
        ManagedSubtree: null,
        MergePolicy.PreserveUnknown,
        DerivedFrom: [],
        Regeneration: null);

    private static DeclaredConfigSurface Downstream(
        string id,
        string[] derivedFrom,
        RegenerationKind kind,
        string description) => new(
        id,
        SurfaceRole.Derived,
        SurfaceFormat.Json,
        Codec: null,
        CodecPath: null,

        // A control-channel locator, so surface resolution never binds it to a file. A downstream surface
        // contributes its regeneration trigger whether or not this session can reach it — exactly the case
        // for Palworld's control-channel-backed 'live'.
        new SurfaceLocator.ControlChannel("rest", $"/{id}"),
        ManagedSubtree: null,
        MergePolicy.PreserveUnknown,
        derivedFrom,
        new RegenerationTrigger(kind, description));

    /// <summary>c (authoritative) &lt;- b &lt;- a. Writing c must reach both.</summary>
    private static IReadOnlyList<DeclaredConfigSurface> Chain() =>
    [
        ChainRoot(),
        Downstream("b", ["c"], RegenerationKind.ContainerRestart, "b is regenerated when the container restarts."),
        Downstream("a", ["b"], RegenerationKind.ProcessRestart, "a is regenerated when the process restarts."),
    ];

    /// <summary>The same chain, plus a back edge from a to b: a definition-authoring defect, not a topology.</summary>
    private static IReadOnlyList<DeclaredConfigSurface> Cyclic() =>
    [
        ChainRoot(),
        Downstream("b", ["c", "a"], RegenerationKind.ContainerRestart, "b is regenerated when the container restarts."),
        Downstream("a", ["b"], RegenerationKind.ProcessRestart, "a is regenerated when the process restarts."),
    ];

    /// <summary>b and a both derived from c, and a also from b. A revisited node, but no back edge.</summary>
    private static IReadOnlyList<DeclaredConfigSurface> Diamond() =>
    [
        ChainRoot(),
        Downstream("b", ["c"], RegenerationKind.ContainerRestart, "b is regenerated when the container restarts."),
        Downstream("a", ["c", "b"], RegenerationKind.ProcessRestart, "a is regenerated when the process restarts."),
    ];

    /// <summary>One downstream surface, regenerated only by hand.</summary>
    private static IReadOnlyList<DeclaredConfigSurface> ManualChain() =>
    [
        ChainRoot(),
        Downstream("b", ["c"], RegenerationKind.Manual, "Regenerated only when an operator re-runs the generator."),
    ];

    // ── Settings ───────────────────────────────────────────────────────────────────────────────────────

    private static readonly SettingConstraints NoConstraints =
        new(null, null, null, null, null, null, null, null, null);

    private static IReadOnlyList<SettingDescriptor> Settings() =>
    [
        Describe("SERVER_NAME", SettingType.String, [new SettingBinding.ByKey("env", BindingDirection.Write, false, "SERVER_NAME")]),
        Describe("ADMIN_PASSWORD", SettingType.Secret, [new SettingBinding.ByKey("env", BindingDirection.Write, false, "ADMIN_PASSWORD")]),
        Describe(
            "PORT",
            SettingType.Port,
            [
                new SettingBinding.ByKey("env", BindingDirection.Write, false, "PORT"),
                new SettingBinding.ByPointer("compose", BindingDirection.Write, false, "/services/palworld/ports", "publish-udp"),
                new SettingBinding.ByMember("palworldsettings", BindingDirection.Read, false, "PublicPort", false),
            ],
            requiresRecreate: true),
        Describe(
            "DERIVED_WRITE",
            SettingType.Enum,
            [new SettingBinding.ByMember("palworldsettings", BindingDirection.Write, false, "Difficulty", false)]),
    ];

    private static IReadOnlyList<SettingDescriptor> ChainSettings() =>
    [
        Describe("CHAINED", SettingType.String, [new SettingBinding.ByKey("c", BindingDirection.Write, false, "CHAINED")]),
    ];

    private static SettingDescriptor Describe(
        string key,
        SettingType type,
        IReadOnlyList<SettingBinding> bindings,
        bool requiresRecreate = false) => new(
        key,
        key,
        "General",
        type,
        Required: false,
        Default: null,
        RenderFormat: null,
        requiresRecreate,
        PublishByDefault: null,
        NoConstraints,
        bindings);

    // ── Doubles ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class StubSessions(ServerConfigSessions? sessions) : IServerConfigSessionSource
    {
        public Task<ServerConfigSessions?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(sessions);
    }

    private sealed class StubCatalog(ServerPlanCatalog? catalog) : IServerPlanCatalogSource
    {
        public Task<ServerPlanCatalog?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(catalog);
    }

    private sealed class MappedContexts : ISurfaceResolutionContextSource
    {
        private readonly Dictionary<IExecutionTarget, SurfaceResolutionContext> _byTarget = [];

        public SurfaceResolutionContext this[IExecutionTarget target]
        {
            set => _byTarget[target] = value;
        }

        public Task<SurfaceResolutionContext?> GetAsync(string serverId, IExecutionTarget target, CancellationToken ct = default) =>
            Task.FromResult(_byTarget.TryGetValue(target, out var context) ? context : null);
    }

    private sealed class StubServerSettings(bool tracked) : IServerSettingsService
    {
        public static readonly ServerId Id = ServerId.New();

        public Task<ServerSettingsSnapshot?> LoadAsync(string containerId, CancellationToken ct = default) =>
            Task.FromResult<ServerSettingsSnapshot?>(tracked
                ? new ServerSettingsSnapshot(Id, new Dictionary<string, DesiredSettingValue>(StringComparer.Ordinal))
                : null);

        public Task<SaveDesiredValueResult> SaveDesiredValueAsync(
            ServerId serverId, string key, string? value, string actor, CancellationToken ct = default) =>
            throw new InvalidOperationException("Previewing a plan must never record a desired value.");
    }

    private sealed class RecordingStore : IChangePlanStore
    {
        public ChangePlanRecord? Plan { get; private set; }

        public IReadOnlyList<ChangePlanActionRecord> Actions { get; private set; } = [];

        public Task SaveAsync(ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default)
        {
            Plan = plan;
            Actions = actions;
            return Task.CompletedTask;
        }

        public Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default) =>
            Task.FromResult(Plan is not null && Plan.Id == id ? new StoredChangePlan(Plan, Actions) : null);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// A read-only session whose every mutating member records the attempt and throws. Preview must never be
    /// able to write to a game server, and a double that quietly tolerated one would prove nothing.
    /// </summary>
    private sealed class FakeTarget(Dictionary<string, byte[]> content) : IExecutionTarget
    {
        public List<string> Mutations { get; } = [];

        public int Reads { get; private set; }

        public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
        {
            if (!content.TryGetValue(path.Value, out var bytes))
            {
                throw new FileNotFoundException($"No such file on the target: '{path.Value}'.", path.Value);
            }

            Reads++;
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
            Task.FromResult(content.ContainsKey(path.Value));

        public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) => throw Refuse("ExecuteAsync");

        public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) => throw Refuse("ExecuteStreamingAsync");

        public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) => throw Refuse("StatAsync");

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) => throw Refuse("ListDirectoryAsync");

        public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) => throw Refuse("WriteFileAsync");

        public Task DeleteAsync(TargetPath path, CancellationToken ct = default) => throw Refuse("DeleteAsync");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private InvalidOperationException Refuse(string member)
        {
            Mutations.Add(member);
            return new InvalidOperationException($"Previewing a change plan must never call {member}.");
        }
    }

    /// <summary>
    /// A real, migrated, throwaway database plus the tracked <c>Server</c> row a plan's foreign key needs.
    /// </summary>
    /// <remarks>
    /// SQLite in-memory over an explicitly opened connection, not the EF InMemory provider — the latter
    /// enforces no foreign keys, no unique indexes and no NOT NULL, so this test would pass against a schema
    /// that could never be created. See <c>SqliteDatabaseFixture</c>'s own remarks in the persistence tests.
    /// </remarks>
    private sealed class PlanDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;

        public PlanDatabase()
        {
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();

            Factory = new PooledFactory(_connection);

            using (var context = Factory.CreateDbContext())
            {
                context.Database.Migrate();
                context.Servers.Add(new Server
                {
                    Id = ServerRowId,
                    Name = "palworld-eu-1",
                    ContainerId = ContainerId,
                    GameDefinitionId = "palworld",
                    DefinitionContentHash = "sha256:test",
                    HostId = null,
                    AdoptionMode = AdoptionMode.Adopted,
                    WriteMode = ServerWriteMode.ReadOnly,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                });
                context.SaveChanges();
            }

            Settings = new EfServerSettingsService(Factory);
        }

        public ServerId ServerRowId { get; } = ServerId.New();

        public IDbContextFactory<ServyxDbContext> Factory { get; }

        public IServerSettingsService Settings { get; }

        public void Dispose() => _connection.Dispose();

        private sealed class PooledFactory(SqliteConnection connection) : IDbContextFactory<ServyxDbContext>
        {
            public ServyxDbContext CreateDbContext() =>
                new(new DbContextOptionsBuilder<ServyxDbContext>().UseSqlite(connection).Options);
        }
    }
}
