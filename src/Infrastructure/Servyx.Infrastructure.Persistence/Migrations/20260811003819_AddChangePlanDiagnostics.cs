using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangePlanDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is "[]", NOT the "" the scaffolder emits for a required string. This column holds
            // a JSON document, and an empty string is not valid JSON — a pre-existing plan row backfilled
            // with "" would throw on deserialization rather than yielding an empty diagnostics list. The
            // apply phase will be this column's first reader, so the failure would surface there, on exactly
            // the oldest rows, long after anyone remembered this migration. "[]" backfills to the same empty
            // list the producer already writes when a plan has no diagnostics.
            migrationBuilder.AddColumn<string>(
                name: "DiagnosticsJson",
                table: "ChangePlans",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiagnosticsJson",
                table: "ChangePlans");
        }
    }
}
