using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Persistence;
using Servyx.Infrastructure.Persistence.Configuration;
using Servyx.Infrastructure.Persistence.Servers;

namespace Servyx.Config.Tests;

/// <summary>
/// Covers <see cref="PlanExecutor.RevertAsync"/> — putting a live game server's files back to the bytes a
/// plan recorded before it wrote them.
/// </summary>
/// <remarks>
/// <para>
/// Every test runs against the REAL <see cref="EfChangePlanStore"/> over a real migrated SQLite schema, and
/// every plan under test is produced by really calling <see cref="PlanExecutor.PreviewAsync"/> and then really
/// calling <see cref="PlanExecutor.ApplyAsync"/>. Nothing here hand-builds a stored row except to inject a
/// specific corruption (a purged image, a truncated one, a flipped flag) that no honest apply can produce, and
/// each of those helpers says which real-world event it is standing in for.
/// </para>
/// <para>
/// <strong>The recurring assertion is <c>WriteCount.Should().Be(0)</c> after a refusal.</strong> A revert is
/// all-or-nothing and that property is bought entirely by the pre-flight sweep, so a suite without those
/// assertions would let the whole sweep be deleted and still pass — the refusals would still be thrown, just
/// after some of the server had already been rewritten.
/// </para>
/// </remarks>
public class PlanExecutorRevertTests
{
    private const string ContainerId = "container-1";
    private const string ComposeDirectory = "/opt/servyx/pal";
    private const string DataDirectory = "/palworld";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const string Env = """
        # The image's source of truth.
        SERVER_NAME=Authoritative Name
        ADMIN_PASSWORD=hunter2
        PORT=8211
        """;

    private const string Properties = """
        motd=Welcome
        max-players=20
        """;

    private const string Json = """
        {
          "tickRate": 30
        }
        """;

    // ── 1. Happy path: the exact recorded pre-image goes back ──────────────────────────────────────────

    [Fact]
    public async Task RevertAsync_RestoresTheExactRecordedPreImageBytes_AndRecordsThePlanReverted()
    {
        var revertedAt = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);
        var clock = new MutableClock(revertedAt);

        using var harness = new Harness(clock);
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        harness.FileContent(".env").Should().Contain("SERVER_NAME=A New Name");
        harness.ResetTransportLog();

        var receipt = await harness.Executor.RevertAsync(plan.Id);

        receipt.PlanId.Should().Be(plan.Id);
        receipt.RevertedAt.Should().Be(revertedAt);
        receipt.Actions.Select(a => a.SurfaceId).Should().Equal("env", "props");
        receipt.Actions.Should().OnlyContain(a => a.WriteReachedServer);
        receipt.FullyVerified.Should().BeTrue();

        // BYTE-FOR-BYTE against the original file, not a "contains the old value" check. The latter passes for
        // a revert that also reflowed the file or dropped its trailing newline.
        harness.RawFile(".env").Should().Equal(Utf8NoBom.GetBytes(Env));
        harness.RawFile("server.properties").Should().Equal(Utf8NoBom.GetBytes(Properties));

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Reverted);
        stored.Plan.RevertedAt.Should().Be(revertedAt);
        stored.Plan.RevertedBy.Should().Be(PlanExecutor.DefaultActor);
        stored.Actions.Should().OnlyContain(a => a.Status == ChangePlanActionStatus.Reverted);
        stored.Actions.Should().OnlyContain(a => a.RevertedAt == revertedAt);
        stored.Actions.Should().OnlyContain(a => a.RevertWriteReachedServer);
        stored.Actions.Should().OnlyContain(a => a.RevertVerification == PostWriteVerification.Verified);

        // The apply's own account survives untouched: what an apply did and what a revert did are separate
        // facts, and the revert has its own columns precisely so it cannot overwrite the first.
        stored.Actions.Should().OnlyContain(a => a.WriteReachedServer);
        stored.Actions.Should().OnlyContain(a => a.AppliedAt != null);
        stored.Actions.Should().OnlyContain(a => a.PostWriteVerification == PostWriteVerification.Verified);

        foreach (var action in stored.Actions)
        {
            action.RevertObservedImageHash.Should().Be(action.PreImageHash);
            action.RevertFailureReason.Should().BeNull();
        }
    }

    [Fact]
    public async Task RevertAsync_CarriesTheSurfacesCurrentDigestAsTheWriteExpectation_NotEitherRecordedOne()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        var stored = await harness.ReadBackAsync(plan.Id);
        var preImage = stored.Actions[0].PreImageHash!;
        var postImage = stored.Actions[0].PostImageHash!;

        harness.ResetTransportLog();
        await harness.Executor.RevertAsync(plan.Id);

        var write = harness.Compose.Writes.Should().ContainSingle().Which;
        write.Options.Strategy.Should().Be(FileWriteStrategy.AtomicRename);

        // What the file HELD when the sweep looked. On a clean apply that happens to equal the post-image, so
        // this assertion alone would not pin the choice — the corrupted-file test below is what does that.
        // What it does pin is the direction that is always wrong: expecting the pre-image would be expecting
        // the file to already be what the revert is about to make it.
        write.Options.ExpectedPreImageHash.Should().Be(postImage);
        write.Options.ExpectedPreImageHash.Should().NotBe(preImage);
    }

    // ── 2. The revert set is WriteReachedServer, not Status == Applied ─────────────────────────────────

    [Fact]
    public async Task RevertAsync_ForAnActionThatFailedItsReadBackAfterTheWriteLanded_StillRevertsIt()
    {
        using var harness = new Harness();

        // Apply, mangling the write so the transport's receipt is honest and the FILE is not. That leaves the
        // action Failed with WriteReachedServer true — no action in the plan says Applied — which is exactly
        // the shape a Status-keyed revert set would skip, over the single most damaged file in the plan.
        var plan = await harness.ApplyAsync(
            mangle: (harness.Compose, ".env"),
            ("SERVER_NAME", "A New Name"),
            ("MOTD", "Hello"));

        var applied = await harness.ReadBackAsync(plan.Id);
        applied.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);
        applied.Actions.Should().NotContain(a => a.Status == ChangePlanActionStatus.Applied);

        var byOrdinal = applied.Actions.OrderBy(a => a.Ordinal).ToList();
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[0].WriteReachedServer.Should().BeTrue();
        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Skipped);
        byOrdinal[1].WriteReachedServer.Should().BeFalse();

        harness.FileContent(".env").Should().Be("# this is not what was approved\n");
        harness.Compose.MangleOnPath = null;
        harness.ResetTransportLog();

        var receipt = await harness.Executor.RevertAsync(plan.Id);

        // Exactly the one action that touched the server, and only it. The Skipped one never changed anything,
        // so writing its pre-image back would be a mutation of a file this plan never touched.
        receipt.Actions.Should().ContainSingle().Which.SurfaceId.Should().Be("env");
        harness.Compose.WriteCount.Should().Be(1);
        harness.Data.WriteCount.Should().Be(0);

        // And the corrupted file really is back. This also proves the write expectation is the file's CURRENT
        // digest: the post-image hash would not have matched the mangled content and the transport would have
        // refused the restore outright.
        harness.RawFile(".env").Should().Equal(Utf8NoBom.GetBytes(Env));

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Reverted);

        var reverted = stored.Actions.OrderBy(a => a.Ordinal).ToList();
        reverted[0].Status.Should().Be(ChangePlanActionStatus.Reverted);
        reverted[0].RevertWriteReachedServer.Should().BeTrue();
        reverted[0].RevertVerification.Should().Be(PostWriteVerification.Verified);

        // Untouched, and asserted rather than assumed: a revert must not invent evidence for an action whose
        // apply never reached the server.
        reverted[1].Status.Should().Be(ChangePlanActionStatus.Skipped);
        reverted[1].RevertWriteReachedServer.Should().BeFalse();
        reverted[1].RevertVerification.Should().BeNull();
        reverted[1].RevertedAt.Should().BeNull();
    }

    // ── 3. Pre-flight refusals: every one of them writes NOTHING ───────────────────────────────────────

    /// <summary>
    /// The purged-pre-image refusal, parameterized over WHICH action lost its image.
    /// </summary>
    /// <remarks>
    /// Three cases, deliberately: first, middle and last. A sweep that checked only <c>actions[0]</c>, or that
    /// stopped one short of the end, would pass a single-case test for two of the three positions while
    /// happily deleting or half-reverting a real server in the others.
    /// </remarks>
    [Theory]
    [InlineData(0, "env")]
    [InlineData(1, "json")]
    [InlineData(2, "props")]
    public async Task RevertAsync_WhenAnyOneActionsPreImageHasBeenPurged_RefusesTheWholePlanAndWritesNothing(
        int ordinal, string surfaceId)
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(
            ("SERVER_NAME", "A New Name"), ("MOTD", "Hello"), ("TICK_RATE", "60"));

        var applied = await harness.ReadBackAsync(plan.Id);
        applied.Actions.Select(a => a.SurfaceId).Should().Equal("env", "json", "props");

        await harness.PurgePreImageAsync(plan.Id, ordinal);
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Message.Should().Contain($"#{ordinal}").And.Contain($"'{surfaceId}'");
        thrown.Message.Should().Contain("retention sweep");
        thrown.Message.Should().Contain("NOTHING was written");

        // THE ALL-OR-NOTHING CONTRACT, in data rather than prose: no action's restore reached the server, and
        // the other two — whose pre-images are perfectly intact — were not opportunistically restored.
        thrown.AnyWriteReachedServer.Should().BeFalse();
        thrown.Actions.Should().HaveCount(3);
        thrown.Actions.Should().OnlyContain(a => !a.WriteReachedServer);
        thrown.Actions.Should().OnlyContain(a => a.Verification == null);

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        harness.Compose.Deletes.Should().BeEmpty();
        harness.Data.Deletes.Should().BeEmpty();

        // The server still holds the APPLIED content, all of it, exactly as before the refusal.
        harness.FileContent(".env").Should().Contain("SERVER_NAME=A New Name");
        harness.FileContent("server.properties").Should().Contain("motd=Hello");

        // And the plan is untouched, so a later revert (after the operator resolves the problem) is still
        // possible — a refusal must not consume the plan's one revert.
        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Applied);
        stored.Plan.RevertedAt.Should().BeNull();
        stored.Actions.Should().OnlyContain(a => a.RevertVerification == null);
    }

    [Fact]
    public async Task RevertAsync_WhenAPreImagesContentDisagreesWithItsRecordedDigest_RefusesBeforeAnyWrite()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // Content PRESENT, and wrong. A presence-only check sails straight through this and writes bytes that
        // are provably not what the file held onto a live server — the failure mode that makes re-hashing the
        // stored pre-image worth its one SHA-256.
        await harness.CorruptPreImageContentAsync(plan.Id, ordinal: 0);
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.Message.Should().Contain("#0").And.Contain("disagrees with itself");
        thrown.Message.Should().Contain("hashes to");
        thrown.AnyWriteReachedServer.Should().BeFalse();

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        harness.FileContent(".env").Should().Contain("SERVER_NAME=A New Name");
        harness.FileContent("server.properties").Should().Contain("motd=Hello");
    }

    [Fact]
    public async Task RevertAsync_WhenAnyActionIsMarkedNotReversible_RefusesTheWholePlanAndWritesNothing()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        await harness.MarkNotReversibleAsync(plan.Id, ordinal: 1);
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.Message.Should().Contain("#1").And.Contain("NOT reversible");
        thrown.AnyWriteReachedServer.Should().BeFalse();

        // Action #0 IS reversible and was still not reverted. That is the whole point of all-or-nothing: a
        // half-reverted server matches neither the plan nor the state before it.
        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RevertAsync_WhenTwoActionsAreBothUnrevertable_NamesBothRatherThanStoppingAtTheFirst()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(
            ("SERVER_NAME", "A New Name"), ("MOTD", "Hello"), ("TICK_RATE", "60"));

        await harness.PurgePreImageAsync(plan.Id, ordinal: 0);
        await harness.MarkNotReversibleAsync(plan.Id, ordinal: 2);
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        // Both, in one message. An operator who fixes only the problem they were told about would come back to
        // a second refusal they could have known about the first time.
        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.Message.Should().Contain("#0").And.Contain("#2");
        thrown.Message.Should().Contain("retention sweep").And.Contain("NOT reversible");

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RevertAsync_WhenTheServersWriteModeIsReadOnly_ThrowsWritesDisabled_AndAttemptsNoWriteAtAll()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        harness.ResetTransportLog();
        harness.WriteMode = WriteMode.ReadOnly;

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        (await revert.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("revert a configuration change");

        harness.Compose.WriteCount.Should().Be(0);
        harness.Compose.Deletes.Should().BeEmpty();
        (await harness.ReadBackAsync(plan.Id)).Plan.Status.Should().Be(ChangePlanStatus.Applied);
    }

    // ── 4. State guards, each on its own ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevertAsync_ForAPlanThatHasAlreadyBeenReverted_RefusesRatherThanRewritingThePreImages()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        await harness.Executor.RevertAsync(plan.Id);
        harness.ResetTransportLog();

        // Somebody edits the file by hand after the revert. A second revert must not put the pre-image back
        // over that edit — the plan's account of the world stopped being current the moment it was reverted.
        harness.SetFile(".env", Env + "\nEDITED_AFTERWARDS=1");

        var again = async () => await harness.Executor.RevertAsync(plan.Id);

        (await again.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already reverted");

        harness.Compose.WriteCount.Should().Be(0);
        harness.FileContent(".env").Should().Be(Env + "\nEDITED_AFTERWARDS=1");
    }

    [Fact]
    public async Task RevertAsync_ForAPlanWhoseWritesNeverReachedTheServer_RefusesRatherThanWritingPreImages()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // Never applied at all: every action is Pending and nothing on the server ever changed. Writing the
        // recorded pre-images back would be a mutation of files this plan has never touched — a "revert" that
        // is really a blind write of stale content.
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Message.Should().Contain("nothing to undo");
        thrown.Actions.Should().BeEmpty();
        thrown.AnyWriteReachedServer.Should().BeFalse();

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        (await harness.ReadBackAsync(plan.Id)).Plan.Status.Should().Be(ChangePlanStatus.Previewed);
    }

    [Fact]
    public async Task RevertAsync_ForAPlanWhoseFirstWriteWasRefusedByDrift_RefusesBecauseNothingLanded()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // The transport refuses before placing anything, so WriteReachedServer stays false even though the
        // action is Failed. Status alone cannot tell this apart from the mangled-write case above; the flag
        // can, and the revert set is the place that distinction has to hold.
        harness.Compose.DriftOnPath = ".env";
        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);
        await apply.Should().ThrowAsync<PlanStaleException>();

        harness.Compose.DriftOnPath = null;
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        (await revert.Should().ThrowAsync<PlanRevertException>())
            .Which.Message.Should().Contain("nothing to undo");

        harness.Compose.WriteCount.Should().Be(0);
        harness.RawFile(".env").Should().Equal(Utf8NoBom.GetBytes(Env));
    }

    [Fact]
    public async Task RevertAsync_ForAnUnknownPlanId_RefusesWithoutTouchingTheServer()
    {
        using var harness = new Harness();

        var revert = async () => await harness.Executor.RevertAsync(ChangePlanId.New().ToString());

        (await revert.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("is stored");
        harness.Compose.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RevertAsync_ForAPlanContainingAControlChannelActionThatLanded_RefusesTheWholePlan()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        await harness.RetagActionAsControlChannelAsync(plan.Id, ordinal: 1);
        harness.ResetTransportLog();

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("control-channel");
        thrown.Message.Should().Contain("NOTHING was written");

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
    }

    // ── 4b. Lifecycle guards against an operation that is IN FLIGHT ────────────────────────────────────

    [Fact]
    public async Task RevertAsync_WhenTwoAttemptsRaceFromTheSameAppliedRow_TheSecondIsRejectedByRowVersion()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));
        harness.ResetTransportLog();

        // The race is interleaved deterministically rather than hoped for. The first attempt is held at the
        // exact moment it CLAIMS the plan — after a full pre-flight sweep, before a single restoring write —
        // so the second attempt runs start to finish against a row that still reads Applied to it too. Both
        // are genuinely holding the same concurrency token, which is the two-Blazor-circuits shape.
        var winner = harness.NewExecutor();
        harness.Store.GateWhen = (row, actions) =>
            row.Status == ChangePlanStatus.Reverting && actions.Count == 0;

        var held = harness.Executor.RevertAsync(plan.Id);
        await harness.Store.ReachedGate.Task;

        var receipt = await winner.RevertAsync(plan.Id);
        receipt.Actions.Should().ContainSingle();

        harness.Store.ReleaseGate.SetResult();

        // The status check alone could not have stopped this: the loser read the row while it still said
        // Applied. Only the conditional UPDATE could, and this is the assertion that says it did.
        var loser = async () => await held;
        (await loser.Should().ThrowAsync<ChangePlanConcurrencyException>())
            .Which.PlanId.Should().Be(plan.Id);

        // EXACTLY ONE restore reached the server. Two reverts writing the same pre-image over each other is
        // the outcome the claim exists to make impossible, and a count is the only thing that can see it.
        harness.Compose.WriteCount.Should().Be(1);
        harness.RawFile(".env").Should().Equal(Utf8NoBom.GetBytes(Env));

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Reverted);
        stored.Actions.Should().OnlyContain(a => a.Status == ChangePlanActionStatus.Reverted);
    }

    [Fact]
    public async Task RevertAsync_WhileAnotherRevertIsAlreadyInFlight_RefusesWithoutWritingAnything()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));
        harness.ResetTransportLog();

        // Held at the first per-action write-ahead: AFTER the claim has been persisted, so storage really
        // does read Reverting, and BEFORE any restoring write has been attempted, so a second sweep starting
        // here would be the first thing to touch the file.
        harness.Store.GateWhen = (_, actions) =>
            actions.Count == 1 && actions[0].Status == ChangePlanActionStatus.Reverting;

        var inFlight = harness.Executor.RevertAsync(plan.Id);
        await harness.Store.ReachedGate.Task;

        (await harness.ReadBackAsync(plan.Id)).Plan.Status.Should().Be(ChangePlanStatus.Reverting);

        var second = async () => await harness.NewExecutor().RevertAsync(plan.Id);

        (await second.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already being reverted");

        // Refused before the pre-flight sweep, let alone before a write. Two sweeps writing the same
        // pre-images over each other would leave the server in a state neither of them can describe.
        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);

        harness.Store.ReleaseGate.SetResult();
        await inFlight;

        harness.Compose.WriteCount.Should().Be(1, "the revert that was already running still finished normally");
        (await harness.ReadBackAsync(plan.Id)).Plan.Status.Should().Be(ChangePlanStatus.Reverted);
    }

    [Fact]
    public async Task RevertAsync_WhileTheApplyIsStillInFlight_RefusesRatherThanRacingItOnTheSameFiles()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // Held at the apply's final ledger write, which is the state that defeats every OTHER guard on the
        // revert path at once: action #0's row already says WriteReachedServer (so the revert set is not
        // empty), the plan has no RevertedAt and is not Reverting — and the apply is still running.
        harness.Store.GateWhen = (row, actions) =>
            row.Status == ChangePlanStatus.Applied && actions.Count == 0;

        var applying = harness.Executor.ApplyAsync(plan.Id);
        await harness.Store.ReachedGate.Task;

        var midApply = await harness.ReadBackAsync(plan.Id);
        midApply.Plan.Status.Should().Be(ChangePlanStatus.Applying);
        midApply.Actions[0].WriteReachedServer.Should().BeTrue();

        harness.ResetTransportLog();

        var revert = async () => await harness.NewExecutor().RevertAsync(plan.Id);

        (await revert.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("being applied right now");

        // RowVersion cannot cover this one. It would make the APPLY fail at its next ledger write — which is
        // after the revert's conflicting bytes had already reached a running game server. Nothing may be
        // written here at all.
        harness.Compose.WriteCount.Should().Be(0);
        harness.Compose.Deletes.Should().BeEmpty();
        harness.Data.WriteCount.Should().Be(0);

        harness.Store.ReleaseGate.SetResult();
        await applying;

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Applied);
        stored.Plan.RevertedAt.Should().BeNull();
        harness.FileContent(".env").Should().Contain("SERVER_NAME=A New Name");
    }

    // ── 5. PreImageExisted == false means DELETE, not an empty write ───────────────────────────────────

    [Fact]
    public async Task RevertAsync_ForAnActionWhoseFileDidNotExistBeforehand_DeletesIt_RatherThanWritingEmptyBytes()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        // The row a create-if-absent write path will produce: no pre-image content, no pre-image digest, and
        // PreImageExisted saying plainly that there was no file. Without that flag this row is
        // indistinguishable from a purged one, which is exactly why the flag exists.
        await harness.MarkFileDidNotExistAsync(plan.Id, ordinal: 0);
        harness.ResetTransportLog();

        var receipt = await harness.Executor.RevertAsync(plan.Id);

        // A DELETE, and specifically NOT a write. Writing zero bytes would leave the workload reading a valid,
        // empty configuration file it never had — a state that looks like a clean revert from every column in
        // the ledger and is not one.
        harness.Compose.Deletes.Should().ContainSingle().Which.Should().Be(".env");
        harness.Compose.WriteCount.Should().Be(0);
        harness.Compose.Writes.Should().NotContain(w => w.Bytes.Length == 0);
        harness.Exists(".env").Should().BeFalse();

        receipt.Actions.Should().ContainSingle();
        receipt.Actions[0].WriteReachedServer.Should().BeTrue();
        receipt.Actions[0].Verification.Should().Be(PostWriteVerification.Verified);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Reverted);
        stored.Actions[0].Status.Should().Be(ChangePlanActionStatus.Reverted);
        stored.Actions[0].RevertVerification.Should().Be(PostWriteVerification.Verified);

        // Nothing was read back, because there is nothing left to read. Echoing a digest here would read as a
        // measurement of a file that no longer exists.
        stored.Actions[0].RevertObservedImageHash.Should().BeNull();
    }

    [Fact]
    public async Task RevertAsync_WhenADeleteReturnsButTheFileIsStillThere_RecordsMismatchedRatherThanSuccess()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        await harness.MarkFileDidNotExistAsync(plan.Id, ordinal: 0);
        harness.ResetTransportLog();

        // A transport whose delete returns cleanly and leaves the file in place. The read-back is the only
        // thing that can see this; a receipt-shaped check has nothing to compare.
        harness.Compose.SwallowDeleteOnPath = ".env";

        var receipt = await harness.Executor.RevertAsync(plan.Id);

        receipt.Actions[0].Verification.Should().Be(PostWriteVerification.Mismatched);
        receipt.FullyVerified.Should().BeFalse();

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyReverted);
        stored.Plan.Status.Should().NotBe(ChangePlanStatus.Reverted);
        stored.Actions[0].RevertVerification.Should().Be(PostWriteVerification.Mismatched);
        stored.Actions[0].Status.Should().Be(ChangePlanActionStatus.Failed);
    }

    [Fact]
    public async Task RevertAsync_WhenTheFileToDeleteDriftsBetweenTheSweepAndTheDelete_RefusesRatherThanRemovingIt()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        await harness.MarkFileDidNotExistAsync(plan.Id, ordinal: 0);
        harness.ResetTransportLog();

        // Somebody edits the file in the window between the pre-flight sweep reading it and the restore
        // acting on what it read. The WRITE branch survives this because it hands the sweep's digest to the
        // transport as FileWriteOptions.ExpectedPreImageHash and lets it refuse; IExecutionTarget.DeleteAsync
        // takes no such parameter, so nothing but an explicit re-check stands between that edit and a file
        // removed outright off a live server — the one restore that leaves nothing behind to inspect.
        harness.Compose.ChangeAfterReadOnPath = ".env";

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.InnerException.Should().BeOfType<TargetDriftException>();
        thrown.Message.Should().Contain("drifted");
        thrown.AnyWriteReachedServer.Should().BeFalse();

        // NOT DELETED, and the edit somebody made is still there. Both halves matter: the first says the
        // refusal happened, the second says it happened before the destructive call rather than after it.
        harness.Compose.Deletes.Should().BeEmpty();
        harness.Compose.WriteCount.Should().Be(0);
        harness.Exists(".env").Should().BeTrue();
        harness.FileContent(".env").Should().Be(WritableTarget.ChangedUnderneath);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.RevertFailed);
        stored.Plan.Status.Should().NotBe(ChangePlanStatus.Reverted);
        stored.Actions[0].RevertWriteReachedServer.Should().BeFalse();
        stored.Actions[0].Status.Should().Be(ChangePlanActionStatus.Failed);
    }

    [Fact]
    public async Task RevertAsync_WhenTheFileToDeleteIsUnchangedSinceTheSweep_StillDeletesIt()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        await harness.MarkFileDidNotExistAsync(plan.Id, ordinal: 0);
        harness.ResetTransportLog();

        // The near-miss of the test above: the same re-read, on a file nobody touched. A drift check that
        // refused here — by comparing against a recorded digest instead of the sweep's own reading, say —
        // would make every revert-by-delete permanently impossible while looking careful.
        var receipt = await harness.Executor.RevertAsync(plan.Id);

        receipt.Actions.Should().ContainSingle().Which.WriteReachedServer.Should().BeTrue();
        harness.Compose.Deletes.Should().ContainSingle().Which.Should().Be(".env");
        harness.Exists(".env").Should().BeFalse();
    }

    // ── 6. Read-back mismatch on a restore: recorded, not thrown ───────────────────────────────────────

    [Fact]
    public async Task RevertAsync_WhenTheRestoredFileDoesNotReadBackAsThePreImage_RecordsMismatched_AndDoesNotThrow()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        var stored = await harness.ReadBackAsync(plan.Id);
        var expectedPreImage = stored.Actions[0].PreImageHash!;

        harness.ResetTransportLog();

        // The transport accepts the pre-image, returns an honest receipt over the bytes it was handed, and
        // puts something else on disk. Structurally invisible to a receipt comparison — which is why the
        // revert path re-reads rather than trusting the receipt, exactly as apply does.
        harness.Compose.MangleOnPath = ".env";

        var receipt = await harness.Executor.RevertAsync(plan.Id);

        // NOT an exception. The remaining action restores a DIFFERENT surface whose own pre-image is still
        // good, and aborting would leave more of the server holding applied content than continuing does.
        receipt.Actions.Should().HaveCount(2);
        receipt.Actions[0].Verification.Should().Be(PostWriteVerification.Mismatched);
        receipt.Actions[1].Verification.Should().Be(PostWriteVerification.Verified);
        receipt.FullyVerified.Should().BeFalse();

        // The second surface really was restored — the mismatch is a per-action fact, not a switch the whole
        // revert fell through.
        harness.RawFile("server.properties").Should().Equal(Utf8NoBom.GetBytes(Properties));

        var after = await harness.ReadBackAsync(plan.Id);
        after.Plan.Status.Should().Be(ChangePlanStatus.PartiallyReverted);
        after.Plan.Status.Should().NotBe(ChangePlanStatus.Reverted);

        var byOrdinal = after.Actions.OrderBy(a => a.Ordinal).ToList();

        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[0].RevertWriteReachedServer.Should().BeTrue();
        byOrdinal[0].RevertVerification.Should().Be(PostWriteVerification.Mismatched);
        byOrdinal[0].RevertVerification.Should().NotBe(PostWriteVerification.NotAttempted);
        byOrdinal[0].RevertedAt.Should().BeNull("the surface was not actually restored");
        byOrdinal[0].RevertFailureReason.Should().NotBeNullOrWhiteSpace();

        // BOTH digests, in their own columns. PreImageHash is what the revert restored FROM and must survive
        // untouched; RevertObservedImageHash is what is really on disk.
        byOrdinal[0].PreImageHash.Should().Be(expectedPreImage);
        byOrdinal[0].RevertObservedImageHash.Should().Be(Sha256(harness.RawFile(".env")));
        byOrdinal[0].RevertObservedImageHash.Should().NotBe(expectedPreImage);

        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Reverted);
        byOrdinal[1].RevertVerification.Should().Be(PostWriteVerification.Verified);

        // No repair and no retry: exactly one restoring write per surface.
        harness.Compose.WriteCount.Should().Be(1);
        harness.Data.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task RevertAsync_WhenTheRestoreCannotBeReadBack_RecordsItRevertedButExplicitlyUnverified()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"));

        harness.ResetTransportLog();
        harness.Compose.FailReadAfterWriteOnPath = ".env";

        var receipt = await harness.Executor.RevertAsync(plan.Id);

        // The restore succeeded; only the confirmation did not. Failing it would report a change that really
        // did land as one that did not.
        receipt.Actions.Should().ContainSingle();
        receipt.Actions[0].Verification.Should().Be(PostWriteVerification.Unverifiable);
        receipt.FullyVerified.Should().BeFalse("nobody looked, so 'fully verified' would be a claim nobody made");

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Reverted);
        stored.Actions[0].Status.Should().Be(ChangePlanActionStatus.Reverted);
        stored.Actions[0].RevertVerification.Should().Be(PostWriteVerification.Unverifiable);
        stored.Actions[0].RevertVerification.Should().NotBe(PostWriteVerification.Verified);
        stored.Actions[0].RevertObservedImageHash.Should().BeNull();
    }

    // ── 7. A restore that fails mid-sequence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RevertAsync_WhenTheSecondOfThreeRestoresFails_TheResultNamesExactlyWhichOnesReachedTheServer()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(
            ("SERVER_NAME", "A New Name"), ("MOTD", "Hello"), ("TICK_RATE", "60"));

        var applied = await harness.ReadBackAsync(plan.Id);
        applied.Actions.Select(a => a.SurfaceId).Should().Equal("env", "json", "props");

        harness.ResetTransportLog();
        harness.Data.FailOnPath = "settings.json";

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.InnerException.Should().BeOfType<IOException>();
        thrown.AnyWriteReachedServer.Should().BeTrue();
        thrown.Message.Should().Contain("#1").And.Contain("'json'");
        thrown.Message.Should().Contain("NOT rolled back");

        // THE PER-ACTION DISCLOSURE. A bare failure would leave an operator with a server whose state is
        // unknowable from the exception, and a revert is no more undoable than the apply it undoes.
        thrown.Actions.Select(a => a.Ordinal).Should().Equal(0, 1, 2);
        thrown.Actions[0].WriteReachedServer.Should().BeTrue();
        thrown.Actions[0].Verification.Should().Be(PostWriteVerification.Verified);
        thrown.Actions[1].WriteReachedServer.Should().BeFalse("the write call itself threw");
        thrown.Actions[1].Verification.Should().BeNull();
        thrown.Actions[2].WriteReachedServer.Should().BeFalse("it was never attempted");
        thrown.Actions[2].Verification.Should().BeNull();

        // And that account matches the server exactly.
        harness.RawFile(".env").Should().Equal(Utf8NoBom.GetBytes(Env), "action #0 was restored");
        harness.FileContent("settings.json").Should().Contain("60", "action #1's restore never landed");
        harness.FileContent("server.properties").Should().Contain("motd=Hello", "action #2 was never attempted");

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyReverted);
        stored.Plan.Status.Should().NotBe(ChangePlanStatus.Reverted);

        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Reverted);
        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[1].RevertFailureReason.Should().NotBeNullOrWhiteSpace();
        byOrdinal[1].RevertWriteReachedServer.Should().BeFalse();

        // Action #2 keeps the APPLY's account of itself. Marking it Skipped to record a fact about the revert
        // would destroy a true statement about the apply that really happened.
        byOrdinal[2].Status.Should().Be(ChangePlanActionStatus.Applied);
        byOrdinal[2].RevertWriteReachedServer.Should().BeFalse();
        byOrdinal[2].RevertVerification.Should().BeNull();
    }

    [Fact]
    public async Task RevertAsync_WhenTheVeryFirstRestoreFails_RecordsRevertFailedRatherThanPartiallyReverted()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        harness.ResetTransportLog();
        harness.Compose.FailOnPath = ".env";

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;
        thrown.AnyWriteReachedServer.Should().BeFalse();

        // Nothing was put back, so PartiallyReverted would overstate the progress and understate the damage:
        // every applied change is still in force.
        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.RevertFailed);
        stored.Plan.Status.Should().NotBe(ChangePlanStatus.PartiallyReverted);
        stored.Actions.Should().NotContain(a => a.Status == ChangePlanActionStatus.Reverted);
    }

    [Fact]
    public async Task RevertAsync_AfterAFailedRevert_RefusesASecondAttemptRatherThanRetryingBlindly()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        harness.Data.FailOnPath = "server.properties";
        var first = async () => await harness.Executor.RevertAsync(plan.Id);
        await first.Should().ThrowAsync<PlanRevertException>();

        harness.Data.FailOnPath = null;
        harness.ResetTransportLog();

        // After a partial revert the server holds a mixture of pre-apply and applied content. A blind second
        // sweep would rewrite pre-images over surfaces nobody has re-examined since the first attempt stopped;
        // recovery is a human's decision, exactly as it is after a partial apply.
        var again = async () => await harness.Executor.RevertAsync(plan.Id);

        (await again.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("PartiallyReverted");

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
    }

    // ── 8. Drift between the sweep and the restoring write ─────────────────────────────────────────────

    [Fact]
    public async Task RevertAsync_WhenASurfaceDriftsBetweenTheSweepAndTheWrite_StopsAndDisclosesWhatLanded()
    {
        using var harness = new Harness();
        var plan = await harness.ApplyAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        harness.ResetTransportLog();
        harness.Data.DriftOnPath = "server.properties";

        var revert = async () => await harness.Executor.RevertAsync(plan.Id);

        var thrown = (await revert.Should().ThrowAsync<PlanRevertException>()).Which;

        // PlanRevertException, not PlanStaleException: this plan WAS applied and its record is accurate, and
        // only this type can carry the per-action account an operator staring at a half-reverted server needs.
        // The drift itself travels intact as the inner exception.
        thrown.InnerException.Should().BeOfType<TargetDriftException>();
        thrown.Message.Should().Contain("drifted");
        thrown.Actions[0].WriteReachedServer.Should().BeTrue();
        thrown.Actions[1].WriteReachedServer.Should().BeFalse("drift is refused before any I/O");

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyReverted);
        stored.Actions.Single(a => a.Ordinal == 1).RevertWriteReachedServer.Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record RecordedWrite(string Path, byte[] Bytes, FileWriteOptions Options);

    /// <summary>
    /// A writable session that also records DELETES, and that can be told to fail, drift, mangle or swallow on
    /// a specific path.
    /// </summary>
    /// <remarks>
    /// Deliberately a near-copy of <c>PlanExecutorApplyTests.WritableTarget</c> rather than a shared base
    /// class: the two suites pin two different engines against the same transport contract, and a shared
    /// double is a single point at which both could be made to pass by weakening it once.
    /// </remarks>
    private sealed class WritableTarget(Dictionary<string, byte[]> content) : IExecutionTarget
    {
        public List<RecordedWrite> Writes { get; } = [];

        public List<string> Deletes { get; } = [];

        public List<string> Executions { get; } = [];

        public int WriteCount => Writes.Count;

        public string? FailOnPath { get; set; }

        public string? DriftOnPath { get; set; }

        public string? MangleOnPath { get; set; }

        public string? FailReadAfterWriteOnPath { get; set; }

        /// <summary>Return successfully from a delete and leave the file exactly where it was.</summary>
        public string? SwallowDeleteOnPath { get; set; }

        /// <summary>
        /// Replace this path's content immediately after the next read of it — somebody editing the file in
        /// the window between the revert's pre-flight sweep and the restore that follows it.
        /// </summary>
        /// <remarks>
        /// One-shot, and applied AFTER the read returns, so the caller sees the original bytes and the file on
        /// disk is something else by the time it acts on them. That is the TOCTOU window itself, reproduced;
        /// the alternative (setting the content up front) tests nothing, because the sweep would simply read
        /// the new content and expect it.
        /// </remarks>
        public string? ChangeAfterReadOnPath { get; set; }

        /// <summary>What <see cref="ChangeAfterReadOnPath"/> leaves behind.</summary>
        public const string ChangedUnderneath = "# somebody edited this after the sweep looked at it\n";

        private readonly HashSet<string> _written = new(StringComparer.Ordinal);

        public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
        {
            if (string.Equals(FailReadAfterWriteOnPath, path.Value, StringComparison.Ordinal)
                && _written.Contains(path.Value))
            {
                throw new IOException($"'{path.Value}' cannot be read back on this session.");
            }

            if (!content.TryGetValue(path.Value, out var bytes))
            {
                throw new FileNotFoundException($"No such file on the target: '{path.Value}'.", path.Value);
            }

            if (string.Equals(ChangeAfterReadOnPath, path.Value, StringComparison.Ordinal))
            {
                ChangeAfterReadOnPath = null;
                content[path.Value] = Utf8NoBom.GetBytes(ChangedUnderneath);
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
            Task.FromResult(content.ContainsKey(path.Value));

        public async Task<FileWriteReceipt> WriteFileAsync(
            TargetPath path, Stream stream, FileWriteOptions options, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            var bytes = buffer.ToArray();

            var existing = content.TryGetValue(path.Value, out var prior) ? prior : null;
            var preImageHash = existing is null ? null : Convert.ToHexStringLower(SHA256.HashData(existing));

            if (string.Equals(DriftOnPath, path.Value, StringComparison.Ordinal))
            {
                // Recorded as an attempt but NOT as a mutation: the real transports refuse before placing
                // anything, and a double that wrote anyway would let a bug through.
                Writes.Add(new RecordedWrite(path.Value, bytes, options));
                throw new TargetDriftException(
                    $"Content at '{path.Value}' has drifted since it was last observed.",
                    path,
                    options.ExpectedPreImageHash,
                    "0000000000000000000000000000000000000000000000000000000000000000");
            }

            if (string.Equals(FailOnPath, path.Value, StringComparison.Ordinal))
            {
                Writes.Add(new RecordedWrite(path.Value, bytes, options));
                throw new IOException($"Failed to write '{path.Value}'. The target file is unchanged.");
            }

            if (options.ExpectedPreImageHash is { } expected
                && !string.Equals(expected, preImageHash, StringComparison.OrdinalIgnoreCase))
            {
                Writes.Add(new RecordedWrite(path.Value, bytes, options));
                throw new TargetDriftException(
                    $"Content at '{path.Value}' has drifted since it was last observed.",
                    path,
                    expected,
                    preImageHash);
            }

            Writes.Add(new RecordedWrite(path.Value, bytes, options));

            content[path.Value] = string.Equals(MangleOnPath, path.Value, StringComparison.Ordinal)
                ? Utf8NoBom.GetBytes("# this is not what was approved\n")
                : bytes;

            _written.Add(path.Value);

            // Hashed over the INPUT buffer, exactly as every real transport does — which is why a mangled
            // write still produces a "correct" receipt and only a read-back can see it.
            return new FileWriteReceipt(
                preImageHash, Convert.ToHexStringLower(SHA256.HashData(bytes)), DateTimeOffset.UnixEpoch);
        }

        public Task DeleteAsync(TargetPath path, CancellationToken ct = default)
        {
            Deletes.Add(path.Value);

            if (!string.Equals(SwallowDeleteOnPath, path.Value, StringComparison.Ordinal))
            {
                content.Remove(path.Value);
                _written.Remove(path.Value);
            }

            return Task.CompletedTask;
        }

        public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
        {
            Executions.Add(spec.Executable);
            throw new InvalidOperationException($"Reverting a change plan must never run '{spec.Executable}'.");
        }

        public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default)
        {
            Executions.Add(spec.Executable);
            throw new InvalidOperationException("Reverting a change plan must never stream a command.");
        }

        public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
            throw new InvalidOperationException("Reverting a change plan must never call StatAsync.");

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
            throw new InvalidOperationException("Reverting a change plan must never call ListDirectoryAsync.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableWriteModes : IWriteModeResolver
    {
        public WriteMode Mode { get; set; } = WriteMode.Enabled;

        public WriteMode Resolve(TargetDescriptor target) => Mode;
    }

    /// <summary>
    /// The real store, with a one-shot gate that can hold the FIRST <see cref="IChangePlanStore.UpdateAsync"/>
    /// call matching a predicate, so a test can freeze an apply or a revert at a chosen moment of its ledger
    /// sequence and let something else happen while it is parked there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A near-copy of <c>PlanExecutorApplyTests.GatedChangePlanStore</c> for the same reason
    /// <see cref="WritableTarget"/> is a near-copy of that suite's transport double, and widened from "the
    /// first update" to "the first update matching <see cref="GateWhen"/>" because a revert's interesting
    /// moments are not all its first one: the claim, and the point just after the claim has been persisted,
    /// are different states of the world and each has its own guard.
    /// </para>
    /// <para>
    /// Only the interleaving is faked. The concurrency check itself remains the real conditional <c>UPDATE</c>
    /// underneath — a double that raised <see cref="ChangePlanConcurrencyException"/> itself would be testing
    /// the double.
    /// </para>
    /// </remarks>
    private sealed class GatedChangePlanStore(IChangePlanStore inner) : IChangePlanStore
    {
        private int _tripped;

        /// <summary>Completes when the gated update has been reached and is being held.</summary>
        public TaskCompletionSource ReachedGate { get; } = new();

        /// <summary>Set by the test to let the held update proceed.</summary>
        public TaskCompletionSource ReleaseGate { get; } = new();

        /// <summary>Which update to hold, or <see langword="null"/> to hold none.</summary>
        public Func<ChangePlanRecord, IReadOnlyList<ChangePlanActionRecord>, bool>? GateWhen { get; set; }

        public Task SaveAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default) =>
            inner.SaveAsync(plan, actions, ct);

        public Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default) =>
            inner.TryGetAsync(id, ct);

        public async Task UpdateAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default)
        {
            if (GateWhen is { } gate && gate(plan, actions) && Interlocked.Exchange(ref _tripped, 1) == 0)
            {
                ReachedGate.SetResult();
                await ReleaseGate.Task.ConfigureAwait(false);
            }

            await inner.UpdateAsync(plan, actions, ct).ConfigureAwait(false);
        }

        public Task<ChangePlanImagePurgeResult> PurgeImagesAsync(
            DateTimeOffset now, TimeSpan imageRetention, CancellationToken ct = default) =>
            inner.PurgeImagesAsync(now, imageRetention, ct);

        public Task<IReadOnlyList<ChangePlanSummary>> ListRecentAsync(
            ServerId serverId, int limit, CancellationToken ct = default) =>
            inner.ListRecentAsync(serverId, limit, ct);
    }

    private sealed class StubSessions(ServerConfigSessions sessions) : IServerConfigSessionSource
    {
        public Task<ServerConfigSessions?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<ServerConfigSessions?>(sessions);
    }

    private sealed class MutableCatalog(IReadOnlyList<SettingDescriptor> settings) : IServerPlanCatalogSource
    {
        public string DefinitionVersion { get; set; } = "sha256:test";

        public Task<ServerPlanCatalog?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<ServerPlanCatalog?>(new ServerPlanCatalog("palworld", DefinitionVersion, settings));
    }

    private sealed class MappedContexts : ISurfaceResolutionContextSource
    {
        private readonly Dictionary<IExecutionTarget, SurfaceResolutionContext> _byTarget = [];

        public SurfaceResolutionContext this[IExecutionTarget target]
        {
            set => _byTarget[target] = value;
        }

        public Task<SurfaceResolutionContext?> GetAsync(
            string serverId, IExecutionTarget target, CancellationToken ct = default) =>
            Task.FromResult(_byTarget.TryGetValue(target, out var context) ? context : null);
    }

    /// <summary>
    /// A real migrated SQLite database, a real <see cref="EfChangePlanStore"/>, a real
    /// <see cref="EfServerRepository"/>, and two writable sessions behind real write guards.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IDbContextFactory<ServyxDbContext> _factory;
        private readonly Dictionary<string, byte[]> _content;
        private readonly MutableWriteModes _writeModes = new();
        private readonly IServerConfigSessionSource _sessions;
        private readonly MutableCatalog _catalog;
        private readonly SurfaceResolver _resolver;
        private readonly IServerSettingsService _settings;
        private readonly IConfigMerger _merger;
        private readonly IConfigAdapter[] _adapters;
        private readonly IConfigValueCodec[] _codecs;
        private readonly IServerRepository _servers;
        private readonly TimeProvider? _time;

        public Harness(TimeProvider? time = null)
        {
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();
            _factory = new PooledFactory(_connection);

            var serverRowId = ServerId.New();
            using (var context = _factory.CreateDbContext())
            {
                context.Database.Migrate();
                context.Servers.Add(new Server
                {
                    Id = serverRowId,
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

            _content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [".env"] = Utf8NoBom.GetBytes(Env),
                ["server.properties"] = Utf8NoBom.GetBytes(Properties),
                ["settings.json"] = Utf8NoBom.GetBytes(Json),
            };

            Data = new WritableTarget(_content);
            Compose = new WritableTarget(_content);

            var descriptor = new TargetDescriptor("docker", "npipe://test", null, null,
                new Dictionary<string, string>(StringComparer.Ordinal));
            var guardedData = new WriteGuardedExecutionTarget(Data, _writeModes, descriptor, "the container");
            var guardedCompose = new WriteGuardedExecutionTarget(Compose, _writeModes, descriptor, "the compose directory");

            var contexts = new MappedContexts
            {
                [guardedData] = new SurfaceResolutionContext(
                    TransportCapabilities.FileRead | TransportCapabilities.FileWrite
                        | TransportCapabilities.ContainerScopedFiles,
                    SessionRoot: DataDirectory,
                    DataDirectory: DataDirectory,
                    ComposeDirectory: null,
                    DataDirectoryIsContainerScoped: true),
                [guardedCompose] = new SurfaceResolutionContext(
                    TransportCapabilities.FileRead | TransportCapabilities.FileWrite,
                    SessionRoot: ComposeDirectory,
                    DataDirectory: null,
                    ComposeDirectory: ComposeDirectory,
                    DataDirectoryIsContainerScoped: false),
            };

            IConfigAdapter[] adapters =
            [
                new DotEnvConfigAdapter(),
                new IniConfigAdapter(),
                new PropertiesConfigAdapter(),
                new JsonConfigAdapter(),
                new YamlConfigAdapter(),
            ];
            IConfigValueCodec[] codecs = [new UnrealOptionSettingsCodec()];

            var sessions = new StubSessions(new ServerConfigSessions(
                [
                    new ConfigSession(guardedData, "the deployment's data directory"),
                    new ConfigSession(guardedCompose, "the host compose directory"),
                ],
                Surfaces()));

            _sessions = sessions;
            _catalog = new MutableCatalog(Settings());
            _resolver = new SurfaceResolver(contexts, adapters);
            _settings = new EfServerSettingsService(_factory);
            _merger = new ConfigMerger(codecs);
            _adapters = adapters;
            _codecs = codecs;
            _servers = new EfServerRepository(_factory);
            _time = time;

            Store = new GatedChangePlanStore(new EfChangePlanStore(_factory));
            Executor = NewExecutor();
        }

        public WritableTarget Data { get; }

        public WritableTarget Compose { get; }

        public PlanExecutor Executor { get; }

        public GatedChangePlanStore Store { get; }

        /// <summary>A second executor over the same storage and the same sessions — a second Blazor circuit.</summary>
        public PlanExecutor NewExecutor() => new(
            _sessions,
            _catalog,
            _resolver,
            _settings,
            _merger,
            Store,
            _adapters,
            _codecs,
            _time,
            logger: null,
            actor: null,
            _servers);

        public WriteMode WriteMode
        {
            get => _writeModes.Mode;
            set => _writeModes.Mode = value;
        }

        public Task<ConfigChangePlan> PreviewAsync(params (string Key, string Value)[] desired)
        {
            WriteMode = WriteMode.Enabled;
            return Executor.PreviewAsync(
                ContainerId,
                desired.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal));
        }

        /// <summary>Previews and really applies a plan, so what a revert undoes is a real apply's work.</summary>
        public async Task<ConfigChangePlan> ApplyAsync(params (string Key, string Value)[] desired)
        {
            var plan = await PreviewAsync(desired).ConfigureAwait(false);
            await Executor.ApplyAsync(plan.Id).ConfigureAwait(false);
            return plan;
        }

        /// <summary>Applies with one path mangled, producing the Failed-but-landed row a revert must include.</summary>
        public async Task<ConfigChangePlan> ApplyAsync(
            (WritableTarget Target, string Path) mangle, params (string Key, string Value)[] desired)
        {
            var plan = await PreviewAsync(desired).ConfigureAwait(false);
            mangle.Target.MangleOnPath = mangle.Path;

            var apply = async () => await Executor.ApplyAsync(plan.Id);
            await apply.Should().ThrowAsync<PlanApplyFidelityException>().ConfigureAwait(false);

            return plan;
        }

        public async Task<StoredChangePlan> ReadBackAsync(string planId) =>
            await Store.TryGetAsync(ChangePlanId.Parse(planId)).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No stored plan '{planId}'.");

        public string FileContent(string path) => Utf8NoBom.GetString(_content[path]);

        public byte[] RawFile(string path) => _content[path];

        public bool Exists(string path) => _content.ContainsKey(path);

        public void SetFile(string path, string text) => _content[path] = Utf8NoBom.GetBytes(text);

        /// <summary>
        /// Forgets every write and delete recorded so far, so a revert test can assert absolute counts.
        /// </summary>
        /// <remarks>
        /// Called after the apply that sets the scene. Asserting <c>WriteCount == 0</c> reads as the
        /// all-or-nothing contract itself; asserting "the same count as before" reads as arithmetic, and a
        /// mutation that dropped the whole pre-flight sweep would be far easier to miss in the second form.
        /// </remarks>
        public void ResetTransportLog()
        {
            Compose.Writes.Clear();
            Compose.Deletes.Clear();
            Data.Writes.Clear();
            Data.Deletes.Clear();
        }

        /// <summary>
        /// Nulls one action's pre-image CONTENT while leaving its digest, exactly as
        /// <c>IChangePlanStore.PurgeImagesAsync</c> does.
        /// </summary>
        /// <remarks>
        /// Reproduced here rather than by calling the sweep so the test can choose which ordinal loses its
        /// image; the sweep is per-plan by design. The shape it leaves behind — hash present, content gone —
        /// is the exact row that is indistinguishable from "the file never existed" without
        /// <see cref="ChangePlanActionRecord.PreImageExisted"/>.
        /// </remarks>
        public async Task PurgePreImageAsync(string planId, int ordinal)
        {
            var id = ChangePlanId.Parse(planId);
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            var action = context.ChangePlanActions.Single(a => a.ChangePlanId == id && a.Ordinal == ordinal);
            action.PreImageContent = null;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <summary>Rewrites one action's pre-image content while leaving its digest — a truncated column.</summary>
        public async Task CorruptPreImageContentAsync(string planId, int ordinal)
        {
            var id = ChangePlanId.Parse(planId);
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            var action = context.ChangePlanActions.Single(a => a.ChangePlanId == id && a.Ordinal == ordinal);
            action.PreImageContent += "\n# not what the file held\n";
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <summary>Records that this action's file did not exist before the plan wrote it.</summary>
        /// <remarks>
        /// Done in storage because no current write path creates a file — <c>PreviewAsync</c> refuses to plan
        /// against a surface it cannot read. This is the row a create-if-absent path will produce, and the
        /// revert branch it selects (a delete) is real code with a real consequence on a live server.
        /// </remarks>
        public async Task MarkFileDidNotExistAsync(string planId, int ordinal)
        {
            var id = ChangePlanId.Parse(planId);
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            var action = context.ChangePlanActions.Single(a => a.ChangePlanId == id && a.Ordinal == ordinal);
            action.PreImageExisted = false;
            action.PreImageContent = null;
            action.PreImageHash = null;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task MarkNotReversibleAsync(string planId, int ordinal)
        {
            var id = ChangePlanId.Parse(planId);
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            var action = context.ChangePlanActions.Single(a => a.ChangePlanId == id && a.Ordinal == ordinal);
            action.Reversible = false;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        public async Task RetagActionAsControlChannelAsync(string planId, int ordinal)
        {
            var id = ChangePlanId.Parse(planId);
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            var action = context.ChangePlanActions.Single(a => a.ChangePlanId == id && a.Ordinal == ordinal);
            action.Kind = PlannedActionKind.WriteControlChannel;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        public void Dispose() => _connection.Dispose();

        private sealed class PooledFactory(SqliteConnection connection) : IDbContextFactory<ServyxDbContext>
        {
            public ServyxDbContext CreateDbContext() =>
                new(new DbContextOptionsBuilder<ServyxDbContext>().UseSqlite(connection).Options);
        }
    }

    // ── Surfaces and settings ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DeclaredConfigSurface> Surfaces() =>
    [
        new(
            "env",
            SurfaceRole.Authoritative,
            SurfaceFormat.Dotenv,
            Codec: null,
            CodecPath: null,
            new SurfaceLocator.HostFile("${COMPOSE_DIR}/.env"),
            ManagedSubtree: null,
            MergePolicy.PreserveUnknown,
            DerivedFrom: [],
            Regeneration: null),
        new(
            "props",
            SurfaceRole.Authoritative,
            SurfaceFormat.Properties,
            Codec: null,
            CodecPath: null,
            new SurfaceLocator.HostFile("${DATA_DIR}/server.properties"),
            ManagedSubtree: null,
            MergePolicy.PreserveUnknown,
            DerivedFrom: [],
            Regeneration: null),
        new(
            "json",
            SurfaceRole.Authoritative,
            SurfaceFormat.Json,
            Codec: null,
            CodecPath: null,
            new SurfaceLocator.HostFile("${DATA_DIR}/settings.json"),
            ManagedSubtree: null,
            MergePolicy.PreserveUnknown,
            DerivedFrom: [],
            Regeneration: null),
    ];

    private static readonly SettingConstraints NoConstraints =
        new(null, null, null, null, null, null, null, null, null);

    private static IReadOnlyList<SettingDescriptor> Settings() =>
    [
        Describe("SERVER_NAME", SettingType.String, [new SettingBinding.ByKey("env", BindingDirection.Write, false, "SERVER_NAME")]),
        Describe("PORT", SettingType.Port, [new SettingBinding.ByKey("env", BindingDirection.Write, false, "PORT")], requiresRecreate: true),
        Describe("MOTD", SettingType.String, [new SettingBinding.ByKey("props", BindingDirection.Write, false, "motd")]),
        Describe("TICK_RATE", SettingType.Int, [new SettingBinding.ByPointer("json", BindingDirection.Write, false, "/tickRate", null)]),
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
}
