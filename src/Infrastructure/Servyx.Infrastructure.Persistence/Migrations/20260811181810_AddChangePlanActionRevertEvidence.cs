using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangePlanActionRevertEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The discriminator that makes a null PreImageContent unambiguous: without it, "the file did not
            // exist before we wrote it" (revert = delete it) and "the retention sweep discarded the bytes"
            // (revert = refuse) are the same row, and RevertAsync would have to guess between deleting a live
            // configuration file and refusing every file-creating plan forever.
            //
            // defaultValue: TRUE, and this one IS load-bearing — the scaffolder's `false` was corrected here
            // by hand. Backfilling `true` says of every pre-existing row "a file was there", which combined
            // with those rows having no captured PreImageContent makes them REFUSE their revert. That is the
            // conservative direction: a refusal an operator can read is recoverable, a spurious delete off a
            // live game server is not. Backfilling `false` would have told the revert engine that every
            // configuration file every historical plan ever touched had been created by that plan.
            //
            // Deliberately NOT paired with HasDefaultValue(true) in ChangePlanActionRecordConfiguration: EF
            // omits a property sitting at its CLR default from an INSERT when the column carries a store
            // default, so a genuine `false` would silently be written as `true`. This is a one-off statement
            // about history, not a standing rule about writes.
            migrationBuilder.AddColumn<bool>(
                name: "PreImageExisted",
                table: "ChangePlanActions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Why a revert of this action failed, kept apart from FailureReason (which belongs to the apply).
            // A failed recovery must not overwrite the account of the failure it was recovering from.
            migrationBuilder.AddColumn<string>(
                name: "RevertFailureReason",
                table: "ChangePlanActions",
                type: "TEXT",
                nullable: true);

            // What the revert's read-back FOUND, kept apart from PreImageHash, which is what it restored FROM
            // — the same expected/observed split ObservedPostImageHash already has with PostImageHash.
            migrationBuilder.AddColumn<string>(
                name: "RevertObservedImageHash",
                table: "ChangePlanActions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            // PostWriteVerification by name, and NULLABLE with no backfill: null means "no revert was ever
            // attempted for this row", which is a different statement from NotAttempted ("a revert ran and
            // never looked at the file"). Only a nullable column can hold both, and every pre-existing row
            // wants the first one.
            migrationBuilder.AddColumn<string>(
                name: "RevertVerification",
                table: "ChangePlanActions",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            // The revert-phase counterpart of WriteReachedServer. defaultValue: false is the truthful value
            // for every existing row — no revert has ever run — so no backfill is needed or wanted.
            migrationBuilder.AddColumn<bool>(
                name: "RevertWriteReachedServer",
                table: "ChangePlanActions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreImageExisted",
                table: "ChangePlanActions");

            migrationBuilder.DropColumn(
                name: "RevertFailureReason",
                table: "ChangePlanActions");

            migrationBuilder.DropColumn(
                name: "RevertObservedImageHash",
                table: "ChangePlanActions");

            migrationBuilder.DropColumn(
                name: "RevertVerification",
                table: "ChangePlanActions");

            migrationBuilder.DropColumn(
                name: "RevertWriteReachedServer",
                table: "ChangePlanActions");
        }
    }
}
