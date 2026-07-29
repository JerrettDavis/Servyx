using Servyx.Domain.Backups;

using Servyx.Infrastructure.Azure.Backups;

namespace Servyx.Infrastructure.Azure.Tests.Backups;

/// <summary>
/// The classifier, exercised directly. Every one of the four marks gets a test that removes exactly that mark
/// and asserts the answer flips to <see cref="BackupOwnership.Foreign"/> — because "all four must hold" is only
/// a real claim if each is independently load-bearing, and a conjunction where one operand is dead code reads
/// identically to one where it is not.
/// </summary>
public sealed class AzureSnapshotOwnershipTests
{
    private static readonly DateTimeOffset TakenAt = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    private static string SetName => AzureSnapshotOwnership.FormatSetName(AzureSnapshotScenario.ServerId, TakenAt);

    private static Dictionary<string, string> ServyxTags() =>
        new(
            AzureSnapshotScenario.ServyxSnapshotTags(SetName, AzureSnapshotScenario.OsDiskName),
            StringComparer.Ordinal);

    private static BackupOwnership Classify(IReadOnlyDictionary<string, string>? tags) =>
        AzureSnapshotOwnership.Classify(
            tags,
            AzureSnapshotScenario.ServerId,
            AzureSnapshotScenario.ResourceGroup,
            AzureSnapshotScenario.VmName);

    [Fact]
    public void All_four_marks_present_classifies_as_servyx_owned() =>
        Classify(ServyxTags()).Should().Be(BackupOwnership.Servyx);

    [Fact]
    public void Missing_managed_mark_is_foreign()
    {
        var tags = ServyxTags();
        tags.Remove("servyx.managed");

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Managed_mark_with_a_truthy_but_different_value_is_foreign()
    {
        var tags = ServyxTags();
        tags["servyx.managed"] = "True";

        Classify(tags).Should().Be(
            BackupOwnership.Foreign,
            "the managed mark is an exact ordinal match, not a truthiness test — this classifier's output feeds a "
            + "delete list");
    }

    [Fact]
    public void Missing_instance_id_mark_is_foreign()
    {
        var tags = ServyxTags();
        tags.Remove("servyx.instance-id");

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Instance_id_naming_a_different_servyx_server_is_foreign()
    {
        var tags = ServyxTags();
        tags["servyx.instance-id"] = "srv-9999";

        Classify(tags).Should().Be(
            BackupOwnership.Foreign,
            "this is what stops one server's retention deleting another server's backups");
    }

    [Fact]
    public void Missing_source_vm_mark_is_foreign()
    {
        var tags = ServyxTags();
        tags.Remove(AzureSnapshotOwnership.SourceVirtualMachineTag);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Source_vm_naming_a_different_machine_is_foreign()
    {
        var tags = ServyxTags();
        tags[AzureSnapshotOwnership.SourceVirtualMachineTag] =
            AzureSnapshotOwnership.FormatSourceVirtualMachine(
                AzureSnapshotScenario.ResourceGroup,
                AzureSnapshotScenario.OtherVmName);

        Classify(tags).Should().Be(
            BackupOwnership.Foreign,
            "a snapshot of the machine this server used to run on is not a backup of the machine it runs on now");
    }

    [Fact]
    public void Source_vm_naming_the_same_machine_name_in_a_different_resource_group_is_foreign()
    {
        var tags = ServyxTags();
        tags[AzureSnapshotOwnership.SourceVirtualMachineTag] =
            AzureSnapshotOwnership.FormatSourceVirtualMachine("rg-somebody-else", AzureSnapshotScenario.VmName);

        Classify(tags).Should().Be(
            BackupOwnership.Foreign,
            "the resource group is half of the machine mark precisely so two identically-named machines cannot be "
            + "confused");
    }

    [Fact]
    public void Missing_set_mark_is_foreign()
    {
        var tags = ServyxTags();
        tags.Remove(AzureSnapshotOwnership.SetTag);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Set_mark_that_this_adapter_did_not_write_is_foreign()
    {
        var tags = ServyxTags();
        tags[AzureSnapshotOwnership.SetTag] = "nightly-backup-2026-07-27";

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Set_mark_naming_a_different_server_is_foreign()
    {
        var tags = ServyxTags();
        tags[AzureSnapshotOwnership.SetTag] = AzureSnapshotOwnership.FormatSetName("srv-9999", TakenAt);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void No_tags_at_all_is_foreign()
    {
        Classify(null).Should().Be(BackupOwnership.Foreign);
        Classify(new Dictionary<string, string>(StringComparer.Ordinal)).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void A_hand_taken_snapshot_named_like_a_servyx_set_is_still_foreign()
    {
        // The exact forgery the ARM-name world makes possible and the EBS world did not: a human can name a
        // snapshot anything, including a Servyx set name. The name is not a mark; the tags are.
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzureSnapshotOwnership.SetTag] = SetName,
        };

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Set_names_round_trip()
    {
        AzureSnapshotOwnership.TryParseSetName(SetName, out var serverId, out var takenAt).Should().BeTrue();
        serverId.Should().Be(AzureSnapshotScenario.ServerId);
        takenAt.Should().Be(TakenAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-servyx-name")]
    [InlineData("servyx-snapshot-")]
    [InlineData("servyx-snapshot-srv-0001-notatimestamp")]
    [InlineData("servyx-snapshot-srv 0001-20260727T100000Z")]
    public void Set_names_this_adapter_did_not_write_do_not_parse(string? candidate) =>
        AzureSnapshotOwnership.TryParseSetName(candidate, out _, out _).Should().BeFalse();

    [Fact]
    public void Read_set_name_returns_null_for_a_name_this_adapter_did_not_write() =>
        AzureSnapshotOwnership
            .ReadSetName(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AzureSnapshotOwnership.SetTag] = "somebody-elses-set",
            })
            .Should()
            .BeNull();

    [Theory]
    [InlineData("srv-0001")]
    [InlineData("srv_0001")]
    [InlineData("srv.0001")]
    [InlineData("SRV0001")]
    public void Supported_server_ids_are_accepted(string serverId) =>
        AzureSnapshotOwnership.IsSupportedServerId(serverId).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("srv 0001")]
    [InlineData("srv/0001")]
    [InlineData("srv:0001")]
    public void Unsupported_server_ids_are_rejected(string? serverId) =>
        AzureSnapshotOwnership.IsSupportedServerId(serverId).Should().BeFalse();

    [Fact]
    public void A_server_id_too_long_for_an_arm_snapshot_name_is_rejected()
    {
        var tooLong = new string('a', AzureSnapshotOwnership.MaxServerIdLength + 1);

        AzureSnapshotOwnership.IsSupportedServerId(tooLong).Should().BeFalse();

        var act = () => AzureSnapshotOwnership.FormatSetName(tooLong, TakenAt);
        act.Should().Throw<ArgumentException>().WithMessage("*bill*");
    }

    [Fact]
    public void The_longest_supported_set_member_name_fits_an_arm_snapshot_name()
    {
        var longest = new string('a', AzureSnapshotOwnership.MaxServerIdLength);
        var name = AzureSnapshotOwnership.FormatMemberName(
            AzureSnapshotOwnership.FormatSetName(longest, TakenAt),
            99);

        name.Length.Should().BeLessThanOrEqualTo(
            AzureSnapshotOwnership.MaxSnapshotNameLength,
            "a snapshot ARM refuses to name is a snapshot that never exists — but a snapshot whose name Servyx "
            + "silently truncated would be one it could never recognise afterwards");
    }

    [Fact]
    public void Member_names_are_zero_padded_so_a_set_sorts_in_capture_order()
    {
        AzureSnapshotOwnership.FormatMemberName(SetName, 0).Should().EndWith("-00");
        AzureSnapshotOwnership.FormatMemberName(SetName, 9).Should().EndWith("-09");
        AzureSnapshotOwnership.FormatMemberName(SetName, 10).Should().EndWith("-10");
    }

    [Fact]
    public void Building_tags_for_a_set_name_this_adapter_did_not_write_is_refused()
    {
        var act = () => AzureSnapshotOwnership.BuildTags(
            AzureSnapshotScenario.ServerId,
            AzureSnapshotScenario.ResourceGroup,
            AzureSnapshotScenario.VmName,
            AzureSnapshotScenario.JobId,
            AzureSnapshotScenario.ConnectorId,
            "somebody-elses-set",
            AzureSnapshotScenario.OsDiskName);

        act.Should().Throw<ArgumentException>().WithMessage("*could never be classified as Servyx's*");
    }

    [Fact]
    public void Tags_built_for_a_capture_classify_back_as_servyx_owned()
    {
        var tags = AzureSnapshotOwnership.BuildTags(
            AzureSnapshotScenario.ServerId,
            AzureSnapshotScenario.ResourceGroup,
            AzureSnapshotScenario.VmName,
            AzureSnapshotScenario.JobId,
            AzureSnapshotScenario.ConnectorId,
            SetName,
            AzureSnapshotScenario.OsDiskName);

        Classify(tags).Should().Be(BackupOwnership.Servyx);

        tags.Should().Contain(new KeyValuePair<string, string>("servyx.managed", "true"));
        tags.Should().Contain(new KeyValuePair<string, string>("servyx.job-id", AzureSnapshotScenario.JobId));
        tags.Should().Contain(new KeyValuePair<string, string>(
            AzureSnapshotOwnership.SourceDiskTag,
            AzureSnapshotScenario.OsDiskName));
    }

    [Fact]
    public void The_source_vm_mark_always_fits_an_arm_tag_value()
    {
        // The reason the mark is a group and a name rather than an ARM resource id: the id reaches 265
        // characters in the worst case and an ARM tag value stops at 256, so the mark would be unrecordable for
        // exactly the machines with the longest names.
        var value = AzureSnapshotOwnership.FormatSourceVirtualMachine(new string('g', 90), new string('v', 64));

        value.Length.Should().BeLessThanOrEqualTo(
            Servyx.Infrastructure.Azure.Provisioning.ServyxAzureTags.MaxTagValueLength);
    }

    [Fact]
    public void Classifying_without_a_server_a_resource_group_or_a_machine_is_refused()
    {
        var tags = ServyxTags();

        var noServer = () => AzureSnapshotOwnership.Classify(tags, "  ", "rg", "vm");
        var noGroup = () => AzureSnapshotOwnership.Classify(tags, "srv-0001", "  ", "vm");
        var noMachine = () => AzureSnapshotOwnership.Classify(tags, "srv-0001", "rg", "  ");

        noServer.Should().Throw<ArgumentException>();
        noGroup.Should().Throw<ArgumentException>();
        noMachine.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Backup_ids_distinguish_a_set_from_a_snapshot_of_the_same_name()
    {
        var setId = AzureSnapshotBackupId.FormatSet(AzureSnapshotScenario.ServerId, SetName);
        var snapshotId = AzureSnapshotBackupId.FormatSnapshot(AzureSnapshotScenario.ServerId, SetName);

        setId.Should().NotBe(
            snapshotId,
            "both halves are ARM names here, so a human can name a hand-taken snapshot exactly like a Servyx set");

        AzureSnapshotBackupId.TryGetServerId(setId, out var fromSet).Should().BeTrue();
        AzureSnapshotBackupId.TryGetServerId(snapshotId, out var fromSnapshot).Should().BeTrue();
        fromSet.Should().Be(AzureSnapshotScenario.ServerId);
        fromSnapshot.Should().Be(AzureSnapshotScenario.ServerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("::trailing")]
    [InlineData("leading::")]
    public void Malformed_backup_ids_do_not_yield_a_server(string? backupId) =>
        AzureSnapshotBackupId.TryGetServerId(backupId, out _).Should().BeFalse();
}
