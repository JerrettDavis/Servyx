using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangePlanActionPostWriteVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is "NotAttempted", NOT the scaffolder's "". This column stores PostWriteVerification
            // by name, and "" is not one of its members — every pre-existing action row would throw on read.
            // Same correction AddChangePlanDiagnostics had to make for its JSON columns; see
            // ServyxDbContextFactory's remarks. "NotAttempted" is also the truthful backfill: no row written
            // before this migration was ever read back and verified.
            migrationBuilder.AddColumn<string>(
                name: "PostWriteVerification",
                table: "ChangePlanActions",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotAttempted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostWriteVerification",
                table: "ChangePlanActions");
        }
    }
}
