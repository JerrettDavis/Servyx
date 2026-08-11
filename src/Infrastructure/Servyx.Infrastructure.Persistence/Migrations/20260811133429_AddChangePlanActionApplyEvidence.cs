using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangePlanActionApplyEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The digest apply FOUND, kept apart from PostImageHash, which is the digest the operator
            // APPROVED. Apply used to overwrite the latter with the former, which broke the invariant
            // PreflightAsync checks (PostImageHash must hash PostImageContent) and lost the approved value
            // for good once the retention sweep nulled the content. Nullable with no backfill: no row written
            // before this migration ever recorded an observation, and null is exactly that statement.
            migrationBuilder.AddColumn<string>(
                name: "ObservedPostImageHash",
                table: "ChangePlanActions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            // "A write for this action reached the server", set before verification is attempted so that a
            // write which landed and then failed its read-back still says so. The retention sweep reads it to
            // decide whether a plan's pre-images are still needed.
            //
            // defaultValue: false with no backfill, deliberately. It is the truthful value for a row that
            // never went through the new code, and it is not load-bearing for old rows either: the sweep's
            // predicate also treats Status == Applied as landed, precisely so pre-existing applied rows keep
            // their protection without this migration having to guess at their history.
            migrationBuilder.AddColumn<bool>(
                name: "WriteReachedServer",
                table: "ChangePlanActions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ObservedPostImageHash",
                table: "ChangePlanActions");

            migrationBuilder.DropColumn(
                name: "WriteReachedServer",
                table: "ChangePlanActions");
        }
    }
}
