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
/// Covers <see cref="PlanExecutor.ApplyAsync"/> — the first code path in Servyx that writes to a live game
/// server.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the REAL <see cref="EfChangePlanStore"/> over a real migrated SQLite schema,
/// not an in-memory double. The double-apply guard is <see cref="ChangePlanRecord.RowVersion"/>, which only
/// exists as behaviour when a real provider issues a real conditional <c>UPDATE</c>; a hand-written fake
/// store would be asserting that the fake implements the guard, which proves nothing about production.
/// </para>
/// <para>
/// Plans are produced by really calling <see cref="PlanExecutor.PreviewAsync"/> rather than being
/// hand-constructed, so what is applied is what preview actually persists — including the exact post-image
/// bytes, whose identity is the whole point of the happy-path assertion.
/// </para>
/// </remarks>
public class PlanExecutorApplyTests
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

    // ── 1. Read-only mode refuses with ZERO writes attempted ───────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenTheServersWriteModeIsReadOnly_ThrowsWritesDisabled_AndAttemptsNoWriteAtAll()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        harness.WriteMode = WriteMode.ReadOnly;

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        (await apply.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("apply a configuration change");

        // The exact count, on the exact target the plan would have written through, with the exact path it
        // would have written to. A loose "no writes anywhere" assertion would still pass if the write went to
        // a different file, and an Any()-shaped one would pass if the count were wrong.
        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        harness.Compose.Writes.Should().NotContain(w => w.Path == ".env");
        harness.Compose.Writes.Should().BeEmpty();
        harness.Data.Writes.Should().BeEmpty();

        // And nothing was recorded as having happened either.
        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Previewed);
        stored.Actions.Should().OnlyContain(a => a.Status == ChangePlanActionStatus.Pending);
        harness.FileContent(".env").Should().Be(Env);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheServersWriteModeIsPreviewOnly_IsAlsoRefused()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        harness.WriteMode = WriteMode.PreviewOnly;

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        await apply.Should().ThrowAsync<WritesDisabledException>();
        harness.Compose.WriteCount.Should().Be(0);
    }

    // ── 2. Pre-flight staleness, before any write ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenABoundSurfaceChangedSincePreview_ThrowsPlanStale_BeforeAnyWriteHappens()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // Somebody edited the file by hand between preview and apply.
        harness.SetFile(".env", Env + "\nEXTRA=1");

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanStaleException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Message.Should().Contain("has changed since");
        thrown.Message.Should().Contain("NOTHING was written");

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        harness.FileContent(".env").Should().Be(Env + "\nEXTRA=1", "the drifted file must be left exactly as found");

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Stale);
    }

    [Fact]
    public async Task ApplyAsync_WhenAnUnwrittenButReadSurfaceDrifted_IsStillRefused()
    {
        using var harness = new Harness();

        // GHOST is bound to a key 'server.properties' does not contain, so preview READS and hashes that
        // surface and then blocks the change — leaving a plan that was validated against 'props' but writes
        // only '.env'. A change to 'props' still invalidates it.
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("GHOST", "x"));

        plan.SurfaceHashes.Should().ContainKey("props");
        plan.Actions.Should().OnlyContain(a => a.SurfaceId == "env");

        harness.SetFile("server.properties", Properties + "\nmotd2=x");

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        (await apply.Should().ThrowAsync<PlanStaleException>()).Which.Message.Should().Contain("'props'");
        harness.Data.WriteCount.Should().Be(0);
        harness.Compose.WriteCount.Should().Be(0);
    }

    // ── 3. Expiry ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_ForAPlanWhoseTtlHasElapsed_IsRefusedAndRecordedStale()
    {
        var previewedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(previewedAt);

        using var harness = new Harness(clock);
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // One tick past the recorded ExpiresAt, which PreviewAsync set to CreatedAt + DefaultTtl.
        clock.Now = previewedAt + ChangePlanRecord.DefaultTtl + TimeSpan.FromTicks(1);

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanStaleException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Message.Should().Contain("expired at");

        harness.Compose.WriteCount.Should().Be(0);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Stale, "an expired plan must never become applicable later");

        // And it stays refused on a second attempt, now on the status gate rather than the expiry gate.
        var again = async () => await harness.Executor.ApplyAsync(plan.Id);
        await again.Should().ThrowAsync<InvalidOperationException>();
        harness.Compose.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAsync_ForAPlanOneTickBeforeItsExpiry_IsStillApplied()
    {
        var previewedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(previewedAt);

        using var harness = new Harness(clock);
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // The boundary matters in both directions: an over-eager expiry check would make every plan
        // unapplicable, which is its own outage.
        clock.Now = previewedAt + ChangePlanRecord.DefaultTtl - TimeSpan.FromTicks(1);

        var receipt = await harness.Executor.ApplyAsync(plan.Id);

        receipt.PlanId.Should().Be(plan.Id);
        harness.Compose.WriteCount.Should().Be(1);
    }

    // ── 4. Double-apply is blocked by the RowVersion concurrency token ─────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenTwoAttemptsRaceFromTheSamePreviewedRow_TheSecondIsRejectedByRowVersion()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        // The race is interleaved deterministically rather than hoped for. The first attempt is held at the
        // exact moment it tries to CLAIM the plan — after it has loaded a Previewed row and passed every
        // pre-flight check, and before it has written anything. The second attempt then runs start to finish
        // against a row that still reads Previewed to it too, so both are genuinely holding the same
        // concurrency token, which is precisely the two-Blazor-circuits (or retried-request) shape.
        var winner = harness.NewExecutor();
        harness.Store.GateArmed = true;

        var held = harness.Executor.ApplyAsync(plan.Id);
        await harness.Store.ReachedFirstUpdate.Task;

        var receipt = await winner.ApplyAsync(plan.Id);
        receipt.Actions.Should().ContainSingle();

        harness.Store.ReleaseFirstUpdate.SetResult();

        var held2 = async () => await held;
        (await held2.Should().ThrowAsync<ChangePlanConcurrencyException>())
            .Which.PlanId.Should().Be(plan.Id);

        // The status check alone could not have stopped this: the losing attempt read the row while it was
        // still Previewed. Exactly one write reached the server.
        harness.Compose.WriteCount.Should().Be(1);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Applied);
        stored.Actions.Should().OnlyContain(a => a.Status == ChangePlanActionStatus.Applied);
    }

    [Fact]
    public async Task ApplyAsync_CalledTwiceInSequence_RefusesTheSecondBecauseThePlanIsNoLongerPreviewed()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        await harness.Executor.ApplyAsync(plan.Id);

        var again = async () => await harness.Executor.ApplyAsync(plan.Id);

        (await again.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("is Applied");

        harness.Compose.WriteCount.Should().Be(1, "the plan's single action must have been written exactly once");
    }

    // ── 5. Happy path ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WritesTheExactPreviewedBytes_ReturnsAReceipt_AndRecordsThePlanApplied()
    {
        var appliedAt = new DateTimeOffset(2026, 8, 9, 12, 5, 0, TimeSpan.Zero);
        var clock = new MutableClock(appliedAt);

        using var harness = new Harness(clock);
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        var previewed = await harness.ReadBackAsync(plan.Id);
        var expectedBytes = previewed.Actions
            .ToDictionary(a => a.SurfaceId, a => Utf8NoBom.GetBytes(a.PostImageContent!), StringComparer.Ordinal);

        var receipt = await harness.Executor.ApplyAsync(plan.Id);

        receipt.PlanId.Should().Be(plan.Id);
        receipt.AppliedAt.Should().Be(appliedAt);
        receipt.Actions.Select(a => a.SurfaceId).Should().BeEquivalentTo(["env", "props"]);

        // Byte-for-byte, against the persisted post-image — not a normalized string comparison, and not a
        // "contains the new value" check. Both of those pass for a write that also reflowed the file.
        harness.Compose.Writes.Should().ContainSingle(w => w.Path == ".env")
            .Which.Bytes.Should().Equal(expectedBytes["env"]);
        harness.Data.Writes.Should().ContainSingle(w => w.Path == "server.properties")
            .Which.Bytes.Should().Equal(expectedBytes["props"]);

        // Every write carried the recorded pre-image hash as its expectation, and the atomic strategy.
        foreach (var write in harness.Compose.Writes.Concat(harness.Data.Writes))
        {
            write.Options.Strategy.Should().Be(FileWriteStrategy.AtomicRename);
            write.Options.ExpectedPreImageHash.Should().NotBeNull();
        }

        harness.Compose.Writes.Single().Options.ExpectedPreImageHash
            .Should().Be(Sha256(Utf8NoBom.GetBytes(Env)));

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Applied);
        stored.Plan.AppliedAt.Should().Be(appliedAt);
        stored.Plan.AppliedBy.Should().Be(PlanExecutor.DefaultActor);
        stored.Actions.Should().OnlyContain(a => a.Status == ChangePlanActionStatus.Applied);
        stored.Actions.Should().OnlyContain(a => a.AppliedAt == appliedAt);

        // Applied AND confirmed. Asserted separately because they are separate claims: Applied means the write
        // call returned, Verified means the file was re-read afterwards and hashed to the approved digest.
        stored.Actions.Should().OnlyContain(a => a.PostWriteVerification == PostWriteVerification.Verified);

        // The approved digest — written at preview, never overwritten by apply — and, separately, the digest
        // the read-back actually found. On a clean apply they agree, and that agreement is the claim.
        foreach (var action in stored.Actions)
        {
            action.PostImageHash.Should().Be(Sha256(expectedBytes[action.SurfaceId]));
            action.ObservedPostImageHash.Should().Be(Sha256(expectedBytes[action.SurfaceId]));
            action.PostImageHash.Should().Be(action.ObservedPostImageHash);
        }

        // Every action recorded that its write reached the server. This is the column the retention sweep
        // reads; a plan whose writes landed must never look purgeable.
        stored.Actions.Should().OnlyContain(a => a.WriteReachedServer);

        harness.FileContent(".env").Should().Contain("SERVER_NAME=A New Name");
    }

    [Fact]
    public async Task ApplyAsync_DoesNotRestartOrRecreateAnything_EvenWhenThePlanSaysOneIsRequired()
    {
        using var harness = new Harness();

        // PORT is declared requiresRecreate, so the plan carries a RecreateRequired consequence.
        var plan = await harness.PreviewAsync(("PORT", "8300"));
        plan.RequiresRecreate.Should().BeTrue();

        await harness.Executor.ApplyAsync(plan.Id);

        // The file write happened; the consequence was deliberately not acted on. ExecuteAsync is the only
        // way this type could restart anything, and it is never called.
        harness.Compose.WriteCount.Should().Be(1);
        harness.Compose.Executions.Should().BeEmpty();
        harness.Data.Executions.Should().BeEmpty();
    }

    // ── 6. Failure at action N of M ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenTheSecondOfThreeWritesFails_TheLedgerSaysExactlyWhichActionsLanded()
    {
        var at = new DateTimeOffset(2026, 8, 9, 12, 5, 0, TimeSpan.Zero);
        var clock = new MutableClock(at);

        using var harness = new Harness(clock);
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"), ("TICK_RATE", "60"));

        // Ordinals follow the surface id ordering PreviewAsync fixes: env(0), json(1), props(2).
        var previewed = await harness.ReadBackAsync(plan.Id);
        previewed.Actions.Select(a => a.SurfaceId).Should().Equal("env", "json", "props");

        harness.Data.FailOnPath = "settings.json";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);
        await apply.Should().ThrowAsync<IOException>();

        var stored = await harness.ReadBackAsync(plan.Id);

        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);
        stored.Plan.Status.Should().NotBe(ChangePlanStatus.Applied);

        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();
        byOrdinal.Should().HaveCount(3);

        // #0 LANDED. Exact status, exact timestamp, exact hash of the exact bytes that were written.
        byOrdinal[0].SurfaceId.Should().Be("env");
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Applied);
        byOrdinal[0].AppliedAt.Should().Be(at);
        byOrdinal[0].PostImageHash.Should().Be(Sha256(Utf8NoBom.GetBytes(previewed.Actions[0].PostImageContent!)));
        byOrdinal[0].FailureReason.Should().BeNull();
        byOrdinal[0].WriteReachedServer.Should().BeTrue();
        byOrdinal[0].PostWriteVerification.Should().Be(PostWriteVerification.Verified);

        // #1 ATTEMPTED AND DID NOT LAND. The write call itself threw, so nothing reached the server and
        // nothing read the file back — both of those are asserted, not left to inference.
        byOrdinal[1].SurfaceId.Should().Be("json");
        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[1].AppliedAt.Should().BeNull();
        byOrdinal[1].FailureReason.Should().NotBeNullOrWhiteSpace();
        byOrdinal[1].PostImageHash.Should().Be(previewed.Actions[1].PostImageHash, "a failed write leaves the previewed digest untouched");
        byOrdinal[1].ObservedPostImageHash.Should().BeNull("nothing was ever observed for this action");
        byOrdinal[1].WriteReachedServer.Should().BeFalse();
        byOrdinal[1].PostWriteVerification.Should().Be(PostWriteVerification.NotAttempted);
        byOrdinal[1].PostWriteVerification.Should().NotBe(PostWriteVerification.Mismatched);

        // #2 NEVER ATTEMPTED — asserted as a positive fact, not inferred from Pending.
        byOrdinal[2].SurfaceId.Should().Be("props");
        byOrdinal[2].Status.Should().Be(ChangePlanActionStatus.Skipped);
        byOrdinal[2].AppliedAt.Should().BeNull();
        byOrdinal[2].WriteReachedServer.Should().BeFalse();
        byOrdinal[2].PostWriteVerification.Should().Be(PostWriteVerification.NotAttempted);

        // And that account matches the server: exactly one file changed.
        harness.Compose.WriteCount.Should().Be(1);
        harness.Data.WriteCount.Should().Be(1, "the failing write was attempted once and the third never was");
        harness.FileContent(".env").Should().Contain("SERVER_NAME=A New Name");
        harness.FileContent("settings.json").Should().Be(Json);
        harness.FileContent("server.properties").Should().Be(Properties);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheVeryFirstWriteFails_RecordsFailedRatherThanPartiallyApplied()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        harness.Compose.FailOnPath = ".env";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);
        await apply.Should().ThrowAsync<IOException>();

        var stored = await harness.ReadBackAsync(plan.Id);

        // Nothing landed, so PartiallyApplied would overstate the damage. Failed is that member's own
        // definition: "no action applied, or the attempt failed before any could".
        stored.Plan.Status.Should().Be(ChangePlanStatus.Failed);
        stored.Actions.Should().NotContain(a => a.Status == ChangePlanActionStatus.Applied);
    }

    // ── 7. TOCTOU drift mid-flight ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenATransportReportsDriftMidFlight_MarksPartiallyApplied_AndThrowsPlanStaleNamingTheAction()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // The drift arrives AFTER the pre-flight sweep passed — the case ExpectedPreImageHash exists to
        // catch, and the reason a single up-front check is not enough on its own.
        harness.Data.DriftOnPath = "server.properties";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanStaleException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Message.Should().Contain("#1");
        thrown.Message.Should().Contain("'props'");
        thrown.Message.Should().Contain("were NOT rolled back");
        thrown.InnerException.Should().BeOfType<TargetDriftException>();

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);

        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Applied);
        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[1].FailureReason.Should().Contain("drifted");

        // Drift is refused by the transport BEFORE it places anything, so this action must not claim to have
        // touched the server — the flag the retention sweep reads has to distinguish "refused" from "landed
        // wrongly", and only the second of those needs the pre-image kept on its own account.
        byOrdinal[1].WriteReachedServer.Should().BeFalse();
        byOrdinal[1].PostWriteVerification.Should().Be(PostWriteVerification.NotAttempted);
        byOrdinal[1].ObservedPostImageHash.Should().BeNull();
        byOrdinal[0].WriteReachedServer.Should().BeTrue();
    }

    // ── 7b. Post-write fidelity: the bytes that landed are not the bytes that were approved ────────────

    [Fact]
    public async Task ApplyAsync_WhenTheServerHoldsDifferentContentAfterTheWrite_FailsTheActionAndReportsBothDigests()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        var previewed = await harness.ReadBackAsync(plan.Id);
        var approved = previewed.Actions[0].PostImageHash!;

        // The realistic failure, and the one the write receipt structurally cannot see: the transport accepts
        // the stream, returns an honest digest OF ITS INPUT, and puts something else on disk. A reflow, a
        // re-encode or a truncation all look exactly like this from the caller's side.
        harness.Compose.MangleOnPath = ".env";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanApplyFidelityException>()).Which;
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Ordinal.Should().Be(0);
        thrown.SurfaceId.Should().Be("env");
        thrown.ApprovedHash.Should().Be(approved);
        thrown.ObservedHash.Should().Be(Sha256(harness.RawFile(".env")));
        thrown.ObservedHash.Should().NotBe(approved);
        thrown.Message.Should().Contain("NOT undone and NOT retried");

        var stored = await harness.ReadBackAsync(plan.Id);
        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();

        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[0].FailureReason.Should().Contain(approved).And.Contain(thrown.ObservedHash!);
        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Skipped);

        // BOTH digests, in their own columns, not merely inside the failure prose. The approved one is the
        // preview's and must be untouched — it is what PreflightAsync checks against PostImageContent, and
        // once the retention sweep nulls that content this column is the only surviving record of what the
        // operator said yes to. The observed one is what is really on disk.
        byOrdinal[0].PostImageHash.Should().Be(approved, "apply must never overwrite the approved digest");
        byOrdinal[0].PostImageHash.Should().NotBe(thrown.ObservedHash);
        byOrdinal[0].ObservedPostImageHash.Should().Be(thrown.ObservedHash, "the row must say what is really on disk");
        byOrdinal[0].PostImageHash.Should().Be(Sha256(Utf8NoBom.GetBytes(previewed.Actions[0].PostImageContent!)));

        // AND THE ROW MUST NOT CLAIM NOBODY LOOKED. A read-back happened and it disagreed; NotAttempted —
        // the default this path used to keep — documents the exact opposite of that.
        byOrdinal[0].PostWriteVerification.Should().Be(PostWriteVerification.Mismatched);
        byOrdinal[0].PostWriteVerification.Should().NotBe(PostWriteVerification.NotAttempted);
        byOrdinal[0].PostWriteVerification.Should().NotBe(PostWriteVerification.Verified);

        // And the row itself records that a write reached the server, even though its status says Failed and
        // no action in this plan says Applied. This is the fact the retention sweep needs; without it on the
        // row, the pre-image that is the only way back from this corruption gets purged.
        byOrdinal[0].WriteReachedServer.Should().BeTrue();
        byOrdinal[1].WriteReachedServer.Should().BeFalse();
        stored.Actions.Should().NotContain(a => a.Status == ChangePlanActionStatus.Applied);

        // The single most important assertion here. This is action #0, so nothing was in `applied` — but the
        // server WAS changed, and wrongly. Recording Failed ("no action applied") would tell an operator the
        // file is untouched when it is corrupted.
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);

        // And no repair or retry was attempted: exactly one write, and the bad content is left as found.
        harness.Compose.WriteCount.Should().Be(1);
        harness.FileContent(".env").Should().Be("# this is not what was approved\n");
    }

    /// <summary>
    /// The end-to-end consequence of the previous test's ledger row: the retention sweep must not destroy the
    /// pre-image of a plan whose only write landed corrupted.
    /// </summary>
    /// <remarks>
    /// Deliberately spans both components rather than asserting the row shape twice. The failure this guards
    /// only appears when the two are composed — apply records a plan with no Applied action, the sweep asks
    /// "did anything land", and a Status-only answer says no and discards the one copy of the bytes that
    /// could put the server back. Neither half is wrong on its own.
    /// </remarks>
    [Fact]
    public async Task PurgeImagesAsync_AfterAReadBackMismatchOnTheFirstAction_KeepsThePreImageThatIsTheOnlyWayBack()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        harness.Compose.MangleOnPath = ".env";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);
        await apply.Should().ThrowAsync<PlanApplyFidelityException>();

        var stored = await harness.ReadBackAsync(plan.Id);

        // The shape that defeats a Status-derived predicate: Failed then Skipped, and not one Applied.
        stored.Actions.OrderBy(a => a.Ordinal).Select(a => a.Status)
            .Should().Equal(ChangePlanActionStatus.Failed, ChangePlanActionStatus.Skipped);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);

        var preImages = stored.Actions.ToDictionary(a => a.Ordinal, a => a.PreImageContent);
        preImages[0].Should().NotBeNull();

        var result = await harness.Store.PurgeImagesAsync(
            stored.Plan.AppliedAt!.Value + TimeSpan.FromDays(1), TimeSpan.FromDays(30));

        result.Any.Should().BeFalse("a write reached the server, so this plan's images are still needed");

        var swept = await harness.ReadBackAsync(plan.Id);
        swept.Actions.Single(a => a.Ordinal == 0).PreImageContent
            .Should().Be(preImages[0], "the corrupted file's pre-image is the only way back");

        // The whole plan's images, not only the one action's — retention is a per-plan decision.
        swept.Actions.Should().OnlyContain(a => a.PreImageContent != null);
        swept.Actions.Should().OnlyContain(a => a.PostImageContent != null);
    }

    /// <summary>
    /// The receipt check, which requires a transport that misreports its own receipt.
    /// </summary>
    /// <remarks>
    /// NOT REACHABLE VIA ANY CURRENT PRODUCTION TRANSPORT. DockerExecutionTarget, SftpFileChannel,
    /// ShellFileChannel and LocalExecutionTarget all compute <c>PostImageSha256</c> over the buffer they were
    /// handed, so their receipt matches the approved digest by construction and this check is a tautology
    /// against them. The stub below lies on purpose. The test pins the handler — it does not claim the
    /// scenario can happen today.
    /// </remarks>
    [Fact]
    public async Task ApplyAsync_WhenADeliberatelyLyingStubTransportMisreportsItsReceiptDigest_FailsTheAction()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        var approved = (await harness.ReadBackAsync(plan.Id)).Actions[0].PostImageHash!;
        harness.Compose.LieAboutReceiptOnPath = ".env";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanApplyFidelityException>()).Which;
        thrown.ApprovedHash.Should().Be(approved);
        thrown.ObservedHash.Should().Be(new string('e', 64));
        thrown.Message.Should().Contain("does not agree about the bytes it was handed");

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);

        var failed = stored.Actions.OrderBy(a => a.Ordinal).First();
        failed.Status.Should().Be(ChangePlanActionStatus.Failed);

        // The approved digest survives untouched; the lie is recorded next to it, not on top of it.
        failed.PostImageHash.Should().Be(approved);
        failed.ObservedPostImageHash.Should().Be(new string('e', 64));

        // NotAttempted is the accurate answer HERE, and is asserted rather than left to the default: this
        // failure is the transport contradicting itself about its own input, raised before CHECK 2 runs, so
        // genuinely nothing read the file back.
        failed.PostWriteVerification.Should().Be(PostWriteVerification.NotAttempted);
        failed.PostWriteVerification.Should().NotBe(PostWriteVerification.Verified);
        failed.PostWriteVerification.Should().NotBe(PostWriteVerification.Mismatched);

        // The write still reached the server, so the pre-image must stay protected.
        failed.WriteReachedServer.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_WhenTheWriteCannotBeReadBack_RecordsTheActionAppliedButExplicitlyUnverified()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        harness.Compose.FailReadAfterWriteOnPath = ".env";

        var receipt = await harness.Executor.ApplyAsync(plan.Id);

        // The write succeeded, so failing it would report a change that really did land as one that did not.
        receipt.Actions.Should().ContainSingle();

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Applied);

        // But "nobody looked" is recorded explicitly, not left implicit or absent.
        stored.Actions.Single().Status.Should().Be(ChangePlanActionStatus.Applied);
        stored.Actions.Single().PostWriteVerification.Should().Be(PostWriteVerification.Unverifiable);
        stored.Actions.Single().PostWriteVerification.Should().NotBe(PostWriteVerification.Verified);
        stored.Actions.Single().PostWriteVerification.Should().NotBe(PostWriteVerification.Mismatched);

        // Nothing was observed, so the observed column stays null rather than echoing the approved digest —
        // which would read as a confirmation that never happened.
        stored.Actions.Single().ObservedPostImageHash.Should().BeNull();
        stored.Actions.Single().PostImageHash.Should().NotBeNull();
        stored.Actions.Single().WriteReachedServer.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_WhenAStoredActionsPostImageDisagreesWithItsRecordedDigest_RefusesBeforeAnyWrite()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // Corrupt the stored row so its content and its digest describe different things. Without the
        // pre-flight check the apply would write these bytes and then "verify" them against a digest that
        // was never theirs, so both post-write checks would be measuring against the wrong number.
        await harness.CorruptPostImageContentAsync(plan.Id, ordinal: 0);

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanApplyFidelityException>()).Which;
        thrown.Message.Should().Contain("disagrees with itself");
        thrown.PlanId.Should().Be(plan.Id);
        thrown.Ordinal.Should().Be(0);

        // Both numbers, so an operator can see which of the two the row's other column agrees with.
        thrown.ApprovedHash.Should().NotBeNullOrWhiteSpace();
        thrown.ObservedHash.Should().NotBe(thrown.ApprovedHash);

        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        harness.FileContent(".env").Should().Be(Env);

        // Not even the second, uncorrupted action ran: the sweep completes before the plan is claimed.
        harness.FileContent("server.properties").Should().Be(Properties);
        (await harness.ReadBackAsync(plan.Id)).Plan.Status.Should().Be(ChangePlanStatus.Previewed);
    }

    [Fact]
    public async Task ApplyAsync_WhenAReadBackMismatchHappensAtALaterOrdinal_LeavesTheEarlierWriteAppliedAndSkipsTheRest()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(
            ("SERVER_NAME", "A New Name"), ("MOTD", "Hello"), ("TICK_RATE", "60"));

        var previewed = await harness.ReadBackAsync(plan.Id);
        previewed.Actions.Select(a => a.SurfaceId).Should().Equal("env", "json", "props");
        var approved = previewed.Actions[1].PostImageHash!;

        // The same corruption as the ordinal-0 case, moved into the middle of the plan, so the reported shape
        // cannot be an accident of "the first action is special".
        harness.Data.MangleOnPath = "settings.json";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<PlanApplyFidelityException>()).Which;
        thrown.Ordinal.Should().Be(1);
        thrown.SurfaceId.Should().Be("json");
        thrown.ApprovedHash.Should().Be(approved);
        thrown.ObservedHash.Should().Be(Sha256(harness.RawFile("settings.json")));
        thrown.ObservedHash.Should().NotBe(approved);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);

        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();

        // #0 genuinely landed and was genuinely confirmed — the failure downstream must not smear onto it.
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Applied);
        byOrdinal[0].PostWriteVerification.Should().Be(PostWriteVerification.Verified);

        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Failed);
        byOrdinal[1].FailureReason.Should().Contain(approved).And.Contain(thrown.ObservedHash!);
        byOrdinal[1].PostImageHash.Should().Be(approved, "apply must never overwrite the approved digest");
        byOrdinal[1].ObservedPostImageHash.Should().Be(thrown.ObservedHash);
        byOrdinal[1].PostWriteVerification.Should().Be(PostWriteVerification.Mismatched);
        byOrdinal[1].PostWriteVerification.Should().NotBe(PostWriteVerification.NotAttempted);
        byOrdinal[1].WriteReachedServer.Should().BeTrue();

        byOrdinal[2].Status.Should().Be(ChangePlanActionStatus.Skipped);
        byOrdinal[2].WriteReachedServer.Should().BeFalse();
        byOrdinal[2].PostWriteVerification.Should().Be(PostWriteVerification.NotAttempted);

        // No repair, no retry, and the third file never touched.
        harness.Data.Writes.Count(w => w.Path == "settings.json").Should().Be(1);
        harness.Data.Writes.Should().NotContain(w => w.Path == "server.properties");
        harness.FileContent("server.properties").Should().Be(Properties);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheTargetDoesNotAdvertiseFileRead_RecordsTheActionAppliedButExplicitlyUnverified()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // Not a failing read — a session that never offered one. The distinction matters: this arm must not
        // fall through to "read it anyway and hope", and must not claim a verification that never happened.
        harness.SurfaceWithoutFileRead = "env";

        var receipt = await harness.Executor.ApplyAsync(plan.Id);
        receipt.Actions.Should().HaveCount(2);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Applied);

        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();

        // The unreadable one: applied, and explicitly marked as never confirmed.
        byOrdinal[0].SurfaceId.Should().Be("env");
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Applied);
        byOrdinal[0].PostWriteVerification.Should().Be(PostWriteVerification.Unverifiable);
        byOrdinal[0].PostWriteVerification.Should().NotBe(PostWriteVerification.Verified);
        byOrdinal[0].PostWriteVerification.Should().NotBe(PostWriteVerification.NotAttempted);
        byOrdinal[0].PostWriteVerification.Should().NotBe(PostWriteVerification.Mismatched);
        byOrdinal[0].ObservedPostImageHash.Should().BeNull("no bytes were read, so none can be reported");

        // Its neighbour on a fully capable session still gets a real answer, so "unverified" is a per-action
        // fact about that surface and not a switch the whole plan fell through.
        byOrdinal[1].SurfaceId.Should().Be("props");
        byOrdinal[1].PostWriteVerification.Should().Be(PostWriteVerification.Verified);
        byOrdinal[1].ObservedPostImageHash.Should().Be(byOrdinal[1].PostImageHash);
    }

    // ── 8. Control-channel actions are out of scope, and refuse the WHOLE plan ─────────────────────────

    [Fact]
    public async Task ApplyAsync_ForAPlanContainingAControlChannelAction_RefusesTheWholePlanAndWritesNothing()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // The one action kind this phase cannot carry out, injected onto an otherwise ordinary plan.
        await harness.RetagActionAsControlChannelAsync(plan.Id, ordinal: 1);

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        var thrown = (await apply.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("control-channel");
        thrown.Message.Should().Contain("NOTHING was written");

        // Crucially the OTHER action — an ordinary, perfectly applicable file write — did not happen either.
        harness.Compose.WriteCount.Should().Be(0);
        harness.Data.WriteCount.Should().Be(0);
        harness.FileContent(".env").Should().Be(Env);

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.Previewed);
    }

    // ── 9. A grant revoked mid-plan ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenTheWriteGrantIsRevokedMidPlan_MarksPartiallyApplied_AndLetsTheRefusalPropagate()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"), ("MOTD", "Hello"));

        // WriteGuardedExecutionTarget re-resolves the grant per call by design, so the up-front check cannot
        // eliminate this. Revoke it the instant the first write lands.
        harness.Compose.AfterWrite = () => harness.WriteMode = WriteMode.ReadOnly;

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        await apply.Should().ThrowAsync<WritesDisabledException>();

        var stored = await harness.ReadBackAsync(plan.Id);
        stored.Plan.Status.Should().Be(ChangePlanStatus.PartiallyApplied);

        var byOrdinal = stored.Actions.OrderBy(a => a.Ordinal).ToList();
        byOrdinal[0].Status.Should().Be(ChangePlanActionStatus.Applied);
        byOrdinal[1].Status.Should().Be(ChangePlanActionStatus.Failed);
    }

    // ── 10. Definition identity ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WhenTheGoverningDefinitionChangedSincePreview_IsRefusedAsStale()
    {
        using var harness = new Harness();
        var plan = await harness.PreviewAsync(("SERVER_NAME", "A New Name"));

        harness.DefinitionVersion = "sha256:something-else";

        var apply = async () => await harness.Executor.ApplyAsync(plan.Id);

        (await apply.Should().ThrowAsync<PlanStaleException>())
            .Which.Message.Should().Contain("changed underneath it");

        harness.Compose.WriteCount.Should().Be(0);
        (await harness.ReadBackAsync(plan.Id)).Plan.Status.Should().Be(ChangePlanStatus.Stale);
    }

    [Fact]
    public async Task ApplyAsync_ForAnUnknownPlanId_RefusesWithoutTouchingTheServer()
    {
        using var harness = new Harness();

        var apply = async () => await harness.Executor.ApplyAsync(ChangePlanId.New().ToString());

        (await apply.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("No change plan");
        harness.Compose.WriteCount.Should().Be(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>One write a <see cref="WritableTarget"/> was asked to perform.</summary>
    private sealed record RecordedWrite(string Path, byte[] Bytes, FileWriteOptions Options);

    /// <summary>
    /// A session that can actually be written to, and that can be told to fail, drift, or pause on a
    /// specific path.
    /// </summary>
    /// <remarks>
    /// Models the transports' own ordering faithfully: the drift check happens before anything is placed, so
    /// a drifting write leaves the file untouched, exactly as <c>DockerExecutionTarget</c> and the SFTP
    /// channel do.
    /// </remarks>
    private sealed class WritableTarget(Dictionary<string, byte[]> content) : IExecutionTarget
    {
        public List<RecordedWrite> Writes { get; } = [];

        public List<string> Executions { get; } = [];

        public int WriteCount => Writes.Count;

        public string? FailOnPath { get; set; }

        public string? DriftOnPath { get; set; }

        /// <summary>
        /// Accept the write and return an honest receipt over the bytes handed in — then store SOMETHING
        /// ELSE. Simulates a transport that reflowed, re-encoded or truncated the content between the stream
        /// it accepted and the file it produced, which is the failure the receipt check cannot see and the
        /// read-back check can.
        /// </summary>
        public string? MangleOnPath { get; set; }

        /// <summary>
        /// Store the correct bytes but report a receipt digest that is not theirs. Only a transport with a
        /// broken receipt computation could do this; no real one in this repo can.
        /// </summary>
        public string? LieAboutReceiptOnPath { get; set; }

        /// <summary>Make reads of this path fail once it has been written, to exercise the unverifiable path.</summary>
        public string? FailReadAfterWriteOnPath { get; set; }

        private readonly HashSet<string> _written = new(StringComparer.Ordinal);

        public TaskCompletionSource? PauseFirstWrite { get; set; }

        public Action? AfterWrite { get; set; }

        public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
        {
            if (string.Equals(FailReadAfterWriteOnPath, path.Value, StringComparison.Ordinal)
                && _written.Contains(path.Value))
            {
                throw new IOException($"'{path.Value}' cannot be read back on this session.");
            }

            return content.TryGetValue(path.Value, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException($"No such file on the target: '{path.Value}'.", path.Value);
        }

        public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
            Task.FromResult(content.ContainsKey(path.Value));

        public async Task<FileWriteReceipt> WriteFileAsync(
            TargetPath path, Stream stream, FileWriteOptions options, CancellationToken ct = default)
        {
            if (PauseFirstWrite is { } gate)
            {
                PauseFirstWrite = null;
                await gate.Task.ConfigureAwait(false);
            }

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

            // What actually lands. Every real transport stores exactly what it was handed; a mangling one
            // stores something else while still reporting an honest digest of its input, below.
            content[path.Value] = string.Equals(MangleOnPath, path.Value, StringComparison.Ordinal)
                ? Utf8NoBom.GetBytes("# this is not what was approved\n")
                : bytes;

            _written.Add(path.Value);
            AfterWrite?.Invoke();

            // Hashed over the INPUT buffer, before/independently of placement — exactly what
            // DockerExecutionTarget, SftpFileChannel, ShellFileChannel and LocalExecutionTarget all do. That
            // fidelity is the point: it is why a mangled write still produces a "correct" receipt.
            var reported = string.Equals(LieAboutReceiptOnPath, path.Value, StringComparison.Ordinal)
                ? new string('e', 64)
                : Convert.ToHexStringLower(SHA256.HashData(bytes));

            return new FileWriteReceipt(preImageHash, reported, DateTimeOffset.UnixEpoch);
        }

        public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
        {
            Executions.Add(spec.Executable);
            throw new InvalidOperationException($"Applying a change plan must never run '{spec.Executable}'.");
        }

        public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default)
        {
            Executions.Add(spec.Executable);
            throw new InvalidOperationException("Applying a change plan must never stream a command.");
        }

        public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
            throw new InvalidOperationException("Applying a change plan must never call StatAsync.");

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
            throw new InvalidOperationException("Applying a change plan must never call ListDirectoryAsync.");

        public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
            throw new InvalidOperationException("Applying a change plan must never call DeleteAsync.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A resolver whose answer a test can change between calls, so a grant can be revoked mid-plan.</summary>
    private sealed class MutableWriteModes : IWriteModeResolver
    {
        public WriteMode Mode { get; set; } = WriteMode.Enabled;

        public WriteMode Resolve(TargetDescriptor target) => Mode;
    }

    /// <summary>
    /// The real store, with a one-shot gate on the FIRST <see cref="IChangePlanStore.UpdateAsync"/> call so a
    /// test can hold one apply attempt at the exact instant it claims the plan and let another overtake it.
    /// </summary>
    /// <remarks>
    /// Only the interleaving is faked; the concurrency check itself is the real conditional <c>UPDATE</c>
    /// underneath. A hand-written store that raised the exception itself would be testing the double.
    /// </remarks>
    private sealed class GatedChangePlanStore(IChangePlanStore inner) : IChangePlanStore
    {
        private int _tripped;

        public TaskCompletionSource ReachedFirstUpdate { get; } = new();

        public TaskCompletionSource ReleaseFirstUpdate { get; } = new();

        public bool GateArmed { get; set; }

        public Task SaveAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default) =>
            inner.SaveAsync(plan, actions, ct);

        public Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default) =>
            inner.TryGetAsync(id, ct);

        public async Task UpdateAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default)
        {
            if (GateArmed && Interlocked.Exchange(ref _tripped, 1) == 0)
            {
                ReachedFirstUpdate.SetResult();
                await ReleaseFirstUpdate.Task.ConfigureAwait(false);
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

    /// <summary>
    /// The real resolver, with <see cref="TransportCapabilities.FileRead"/> removed from one named surface's
    /// requirements so the read-back gate's capability arm can be reached at all.
    /// </summary>
    /// <remarks>
    /// A double is the only way in. <see cref="SurfaceResolver"/> puts <see cref="TransportCapabilities.FileRead"/>
    /// into EVERY resolved surface's requirements and refuses the surface when the session does not advertise
    /// it, so no production configuration can currently produce a surface that resolved yet may not be read
    /// back. <c>PlanExecutor</c> checks the capability anyway rather than relying on that property of another
    /// class, and this is how that branch is exercised — the branch is real code with a real consequence
    /// (an action recorded unverified instead of verified), so it is pinned rather than left uncovered.
    /// </remarks>
    private sealed class CapabilityStrippingResolver(ISurfaceResolver inner) : ISurfaceResolver
    {
        public string? WithoutFileRead { get; set; }

        public async Task<SurfaceResolution> ResolveAsync(
            string serverId,
            IExecutionTarget target,
            IReadOnlyList<DeclaredConfigSurface> surfaces,
            CancellationToken ct = default)
        {
            var resolution = await inner.ResolveAsync(serverId, target, surfaces, ct).ConfigureAwait(false);

            if (WithoutFileRead is null)
            {
                return resolution;
            }

            var adjusted = resolution.Resolved
                .Select(surface => string.Equals(surface.Id, WithoutFileRead, StringComparison.Ordinal)
                    ? surface with
                    {
                        RequiredCapabilities = surface.RequiredCapabilities & ~TransportCapabilities.FileRead,
                    }
                    : surface)
                .ToList();

            return resolution with { Resolved = adjusted };
        }
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
        private readonly MutableCatalog _catalog;
        private readonly GatedChangePlanStore _store;
        private readonly IServerConfigSessionSource _sessions;
        private readonly CapabilityStrippingResolver _resolver;
        private readonly IServerSettingsService _settings;
        private readonly IConfigMerger _merger;
        private readonly IServerRepository _servers;
        private readonly IConfigAdapter[] _adapters;
        private readonly IConfigValueCodec[] _codecs;
        private readonly TimeProvider? _time;

        public Harness(TimeProvider? time = null)
        {
            _time = time;
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

            // The real guard, resolver-backed, so a grant revoked between two writes is honoured on the
            // second — the shape production actually runs in.
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

            _adapters =
            [
                new DotEnvConfigAdapter(),
                new IniConfigAdapter(),
                new PropertiesConfigAdapter(),
                new JsonConfigAdapter(),
                new YamlConfigAdapter(),
            ];
            _codecs = [new UnrealOptionSettingsCodec()];

            _sessions = new StubSessions(new ServerConfigSessions(
                [
                    new ConfigSession(guardedData, "the deployment's data directory"),
                    new ConfigSession(guardedCompose, "the host compose directory"),
                ],
                Surfaces()));

            _catalog = new MutableCatalog(Settings());
            _resolver = new CapabilityStrippingResolver(new SurfaceResolver(contexts, _adapters));
            _settings = new EfServerSettingsService(_factory);
            _merger = new ConfigMerger(_codecs);
            _store = new GatedChangePlanStore(new EfChangePlanStore(_factory));
            _servers = new EfServerRepository(_factory);

            Executor = NewExecutor();
        }

        public WritableTarget Data { get; }

        public WritableTarget Compose { get; }

        public PlanExecutor Executor { get; }

        public GatedChangePlanStore Store => _store;

        public WriteMode WriteMode
        {
            get => _writeModes.Mode;
            set => _writeModes.Mode = value;
        }

        public string DefinitionVersion
        {
            get => _catalog.DefinitionVersion;
            set => _catalog.DefinitionVersion = value;
        }

        /// <summary>A second executor over the same storage and the same sessions — a second Blazor circuit.</summary>
        public PlanExecutor NewExecutor() => new(
            _sessions,
            _catalog,
            _resolver,
            _settings,
            _merger,
            _store,
            _adapters,
            _codecs,
            _time,
            logger: null,
            actor: null,
            _servers);

        public Task<ConfigChangePlan> PreviewAsync(params (string Key, string Value)[] desired)
        {
            WriteMode = WriteMode.Enabled;
            return Executor.PreviewAsync(
                ContainerId,
                desired.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal));
        }

        public async Task<StoredChangePlan> ReadBackAsync(string planId) =>
            await _store.TryGetAsync(ChangePlanId.Parse(planId)).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No stored plan '{planId}'.");

        public string FileContent(string path) => Utf8NoBom.GetString(_content[path]);

        public void SetFile(string path, string text) => _content[path] = Utf8NoBom.GetBytes(text);

        /// <summary>The bytes actually sitting at <paramref name="path"/> on the fake server, undecoded.</summary>
        /// <remarks>
        /// Bytes rather than text because the digests under test are taken over raw bytes. Going through a
        /// string would re-encode, and a test that round-trips through text cannot tell a faithful write from
        /// one that normalized line endings or dropped a BOM — exactly the corruption the read-back check
        /// exists to catch.
        /// </remarks>
        public byte[] RawFile(string path) => _content[path];

        /// <summary>
        /// Names the one surface whose resolved requirements should omit
        /// <see cref="TransportCapabilities.FileRead"/>, or <see langword="null"/> to leave them alone.
        /// </summary>
        public string? SurfaceWithoutFileRead
        {
            get => _resolver.WithoutFileRead;
            set => _resolver.WithoutFileRead = value;
        }

        /// <summary>
        /// Rewrites one stored action's post-image CONTENT while leaving its recorded digest untouched, so
        /// the row describes two different files.
        /// </summary>
        /// <remarks>
        /// The content is corrupted rather than the digest on purpose: that is the shape a truncated column,
        /// a botched migration or a half-committed write produces, and it is the direction that actually
        /// endangers a server. Apply would write THESE bytes and then check them against a number that was
        /// never theirs, so both post-write comparisons would pass while unapproved content sat on disk.
        /// </remarks>
        public async Task CorruptPostImageContentAsync(string planId, int ordinal)
        {
            var id = ChangePlanId.Parse(planId);
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            var action = context.ChangePlanActions.Single(a => a.ChangePlanId == id && a.Ordinal == ordinal);
            action.PostImageContent += "\n# smuggled in after the operator approved the plan\n";
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Rewrites one stored action's kind to <see cref="PlannedActionKind.WriteControlChannel"/>.
        /// </summary>
        /// <remarks>
        /// Done in storage rather than by declaring a control-channel surface, because
        /// <c>PlanExecutor.PreviewAsync</c> refuses to bind one at all — which is correct, and which means
        /// the only way to exercise apply's own refusal is to hand it the row a later, RCON-capable preview
        /// will produce.
        /// </remarks>
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

        // Bound to a key 'props' does not contain: preview reads and hashes the surface, then blocks the
        // change. Exists so a test can produce a plan validated against a surface it does not write.
        Describe("GHOST", SettingType.String, [new SettingBinding.ByKey("props", BindingDirection.Write, false, "ghost-key")]),
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
