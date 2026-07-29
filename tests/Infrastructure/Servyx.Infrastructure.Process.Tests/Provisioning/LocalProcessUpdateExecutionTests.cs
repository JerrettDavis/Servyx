using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="LocalProcessProvisioner"/>'s <see cref="IUpdateApplier"/> half — the only code in
/// the local-process assembly that changes an install which already exists.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal test asserts <em>zero mutating operations</em> against three independent witnesses rather than
/// against one: the recording host's command log, its interleaved write/delete log, and a byte-and-timestamp
/// snapshot of the whole temp directory. The third is what makes the assertion genuine — a mutation that
/// bypassed the transport entirely (this adapter's <c>ensure-dir</c> verb is a
/// <see cref="Directory.CreateDirectory(string)"/> call, and the update path creates the data directory the
/// same way) would leave the first two clean and still show up in the snapshot.
/// </para>
/// <para>
/// Nothing here needs <c>steamcmd</c> to exist: command execution is recorded rather than performed, so the
/// suite runs the same on a Windows workstation and on a Linux CI runner.
/// </para>
/// </remarks>
public class LocalProcessUpdateExecutionTests
{
    /// <summary>
    /// A directory name carrying the exact punctuation an injection attempt would use. It is legal on both
    /// Windows and Linux — no <c>/</c>, <c>\</c>, <c>:</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>,
    /// <c>*</c> or <c>?</c> — so the same test proves the same thing on both.
    /// </summary>
    private const string HostileDirectoryName = "pal; rm -rf tmp && echo pwned";

    /// <summary>Installs, then plans an executable change — the smallest genuinely Preserved update.</summary>
    private static async Task<(ProvisionedResource Resource, UpdatePlan Plan)> PlanExecutableChangeAsync(
        LocalInstallFixture fixture)
    {
        var resource = await fixture.InstallAsync();
        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(LocalInstallFixture.With("executable", "./PalServer-Linux-Shipping")));

        plan!.DataImpact.Should().Be(DataImpact.Preserved);
        fixture.Host.ClearRecordings();
        return (resource, plan);
    }

    /// <summary>Asserts that nothing at all happened to the machine.</summary>
    private static void AssertNothingHappened(LocalInstallFixture fixture, IReadOnlyList<string> before)
    {
        fixture.Host.Commands.Should().BeEmpty("a refusal must not run a single install verb");
        fixture.Host.Order.Should().BeEmpty("a refusal must not write or delete a single file");
        fixture.Temp.Snapshot().Should().Equal(before, "a refusal must not create a directory either");
    }

    // ── The one case this adapter executes ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_preserved_plan_executes_rerunning_the_verbs_and_rewriting_the_marker()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        Directory.Delete(fixture.ConfigDirectory, recursive: true);

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>()
            .Which.Resource.Handle.ProviderResourceId.Should().Be(fixture.MarkerPath);

        // The marker was rewritten, marker first, and then every install verb re-ran.
        fixture.Host.Order.Should().Equal($"write:{fixture.MarkerPath}", "exec:steamcmd");
        (await fixture.ReadMarkerAsync())[ServyxProcessMarker.ExecutableTag]
            .Should().Be("./PalServer-Linux-Shipping");

        // The ensure-dir verb genuinely re-ran: the directory removed above is back, without a process starting.
        Directory.Exists(fixture.ConfigDirectory).Should().BeTrue();
        fixture.Host.Commands.Should().ContainSingle().Which.Executable.Should().Be("steamcmd");
    }

    [Fact]
    public async Task A_preserved_plan_leaves_every_file_in_the_data_directory_where_it_was()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        var save = Path.Combine(fixture.DataDirectory, "world.sav");
        await File.WriteAllTextAsync(save, "precious");

        await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        (await File.ReadAllTextAsync(save)).Should().Be("precious");
        fixture.Host.Order.Should().NotContain(o => o.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_update_preserves_the_original_creation_timestamp_and_any_tag_the_plan_never_mentioned()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        // A tag some other tool added to the marker after Servyx wrote it.
        var seeded = new Dictionary<string, string>(await fixture.ReadMarkerAsync(), StringComparer.Ordinal)
        {
            ["ops.owner"] = "alice",
        };
        await File.WriteAllBytesAsync(fixture.MarkerPath, ServyxProcessMarker.Serialize(seeded));
        var createdAt = seeded[ServyxProcessMarker.CreatedAtTag];

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(LocalInstallFixture.With("executable", "./Other.sh")));

        await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan!, plan!.PlanHash);

        var after = await fixture.ReadMarkerAsync();
        after["ops.owner"].Should().Be("alice", "an update does only what the plan describes");
        after[ServyxProcessMarker.CreatedAtTag].Should().Be(createdAt, "an update must not relabel the install as newly created");
        after[ServyxProcessMarker.ExecutableTag].Should().Be("./Other.sh");
    }

    [Fact]
    public async Task A_hostile_path_stays_one_inert_argv_element_when_the_verbs_are_re_run()
    {
        using var fixture = new LocalInstallFixture(HostileDirectoryName);
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);

        await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        var steamcmd = fixture.Host.Commands.Should().ContainSingle().Subject;
        steamcmd.Executable.Should().Be("steamcmd");
        steamcmd.Arguments.Should().Equal(
            "+force_install_dir", fixture.DataDirectory, "+login", "anonymous", "+app_update", "2394010", "validate", "+quit");

        // It is either the whole element or absent — nothing merged it into a larger token, and there is no
        // command line for it to escape out of.
        steamcmd.Arguments.Should().ContainSingle(a => a == fixture.DataDirectory);
        steamcmd.Arguments.Should().OnlyContain(a => a == fixture.DataDirectory || (!a.Contains("rm -rf") && !a.Contains("&&")));

        // The hostile name really did land as one directory, not as a chain of them.
        Directory.Exists(fixture.DataDirectory).Should().BeTrue();
        Directory.GetDirectories(fixture.Temp.Root).Should().Contain(fixture.DataDirectory);
    }

    // ── Refusals: every one of them mutates nothing ──────────────────────────────────────────────

    [Fact]
    public async Task A_non_preserved_plan_is_refused_with_zero_mutating_operations()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        var moved = fixture.Temp.At("palworld-v2");

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataDir"] = moved,
                ["install:1:path"] = Path.Combine(moved, "Pal", "Saved", "Config"),
            }));

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        fixture.Host.ClearRecordings();
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("AtRisk").And.Contain("Nothing on this machine was touched");

        AssertNothingHappened(fixture, before);
        Directory.Exists(moved).Should().BeFalse("the directory the refused plan named was never created");
    }

    [Fact]
    public async Task A_stale_plan_hash_is_refused_with_zero_mutating_operations()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(
            resource.Handle,
            plan,
            "0000000000000000000000000000000000000000000000000000000000000000");

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not the plan").And.Contain(plan.PlanHash);

        AssertNothingHappened(fixture, before);
    }

    [Fact]
    public async Task A_plan_whose_inputs_moved_between_preview_and_apply_is_refused_before_the_first_mutation()
    {
        // The hash handed in still matches the plan object, so the first guard passes. What catches this is the
        // revalidation immediately before the first mutating step: the plan is recomputed from the marker as it
        // is now, and no longer hashes to the approved value.
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);

        var edited = new Dictionary<string, string>(await fixture.ReadMarkerAsync(), StringComparer.Ordinal)
        {
            [ServyxProcessMarker.JobIdTag] = "job-99",
        };
        await File.WriteAllBytesAsync(fixture.MarkerPath, ServyxProcessMarker.Serialize(edited));
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("changed between the preview and now");

        AssertNothingHappened(fixture, before);
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task A_read_only_target_is_refused_cleanly_before_anything_runs(WriteMode mode)
    {
        // The structural write guard gates WriteFileAsync and DeleteAsync but deliberately NOT ExecuteAsync, and
        // this adapter's install verbs are commands (and one of them is a bare Directory.CreateDirectory that
        // never reaches a transport at all). Left to the guard alone, a read-only server would run steamcmd
        // against a live install and only then be refused at the marker write.
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        fixture.Host.GuardMode = mode;
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain(mode.ToString()).And.Contain("no install verb ran");

        AssertNothingHappened(fixture, before);
        (await fixture.ReadMarkerAsync())[ServyxProcessMarker.ExecutableTag]
            .Should().Be(LocalInstallFixture.Executable, "the marker is exactly as it was");
    }

    [Fact]
    public async Task A_target_whose_write_mode_is_enabled_is_not_refused()
    {
        // The guard is consulted, not merely present: the same wrapper in Enabled mode lets the update through.
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        fixture.Host.GuardMode = WriteMode.Enabled;

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();
        fixture.Host.Order.Should().Equal($"write:{fixture.MarkerPath}", "exec:steamcmd");
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_with_zero_mutating_operations()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        var before = fixture.Temp.Snapshot();

        var foreign = new UpdatePlan(
            planId: plan.PlanId,
            planHash: plan.PlanHash,
            provisionerId: "ssh-process",
            strategy: plan.Strategy,
            dataImpact: plan.DataImpact,
            changes: plan.Changes,
            stages: plan.Stages,
            expiresAt: plan.ExpiresAt);

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, foreign, foreign.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>().Which.Message.Should().Contain("ssh-process");
        AssertNothingHappened(fixture, before);
    }

    [Fact]
    public async Task A_resource_belonging_to_another_provisioner_is_refused_with_zero_mutating_operations()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(
            new ResourceHandle("docker-container", fixture.MarkerPath, null, resource.Handle.Tags),
            plan,
            plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>().Which.Message.Should().Contain("docker-container");
        AssertNothingHappened(fixture, before);
    }

    [Fact]
    public async Task A_plan_reporting_no_change_required_is_refused_with_zero_mutating_operations()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var plan = await fixture.Provisioner.PlanUpdateAsync(resource.Handle, fixture.Request());
        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        fixture.Host.ClearRecordings();
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain(nameof(UpdateStrategy.NoChangeRequired));

        AssertNothingHappened(fixture, before);
    }

    [Fact]
    public async Task A_plan_this_adapter_never_computed_is_refused_with_zero_mutating_operations()
    {
        // Hand-built rather than planned: it satisfies every structural guard, and is still refused, because
        // the install verbs its stages promise cannot be recovered from a plan this adapter did not produce.
        // Executing the marker rewrite and skipping those stages would report a half-applied update as applied.
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        var before = fixture.Temp.Snapshot();

        var forged = new UpdatePlan(
            planId: "local-process:update:srv-0001:000000000000",
            planHash: "000000000000000000000000000000000000000000000000000000000000beef",
            provisionerId: "local-process",
            strategy: UpdateStrategy.InPlace,
            dataImpact: DataImpact.Preserved,
            changes: [new PlannedChange("executable", LocalInstallFixture.Executable, "./Other.sh", RequiresRecreate: false)],
            stages: [new ProvisioningStage("update-marker", "local-process", "Rewrite the marker.")],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, forged, forged.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("was not computed by this adapter");

        AssertNothingHappened(fixture, before);
    }

    [Fact]
    public async Task An_install_that_vanished_between_preview_and_apply_is_refused_with_zero_mutating_operations()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        File.Delete(fixture.MarkerPath);
        var before = fixture.Temp.Snapshot();

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not a Servyx-managed marker file");

        AssertNothingHappened(fixture, before);
    }

    [Fact]
    public async Task A_null_argument_is_the_only_thing_that_throws()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);

        await ((Func<Task>)(() => fixture.Provisioner.ApplyUpdateAsync(null!, plan, plan.PlanHash)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => fixture.Provisioner.ApplyUpdateAsync(resource.Handle, null!, plan.PlanHash)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, "  ")))
            .Should().ThrowAsync<ArgumentException>();
    }

    // ── Failure, not refusal ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_install_verb_reports_failed_and_never_removes_the_data_directory()
    {
        using var fixture = new LocalInstallFixture();
        var (resource, plan) = await PlanExecutableChangeAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.DataDirectory, "world.sav"), "precious");

        fixture.Host.ExecHandler = command => command.Executable == "steamcmd"
            ? new CommandResult(8, string.Empty, "steamcmd: disk full", TimeSpan.Zero)
            : new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);

        var result = await fixture.Provisioner.ApplyUpdateAsync(resource.Handle, plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain("disk full").And.Contain("was not deleted");

        // A failure is not a data loss: the directory and everything in it survive, and the marker is still
        // there for a sweep to find.
        (await File.ReadAllTextAsync(Path.Combine(fixture.DataDirectory, "world.sav"))).Should().Be("precious");
        File.Exists(fixture.MarkerPath).Should().BeTrue();
        fixture.Host.Order.Should().NotContain(o => o.StartsWith("delete:", StringComparison.Ordinal));
    }
}
