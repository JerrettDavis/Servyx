using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangePlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AppliedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RevertedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevertedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConsequencesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SurfaceHashesJson = table.Column<string>(type: "TEXT", nullable: false),
                    BlockedJson = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangePlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangePlans_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChangePlanActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChangePlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SurfaceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ResolvedPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RequiredCapabilities = table.Column<int>(type: "INTEGER", nullable: false),
                    UnifiedDiff = table.Column<string>(type: "TEXT", nullable: false),
                    Reversible = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreImageHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PreImageContent = table.Column<string>(type: "TEXT", nullable: true),
                    PostImageContent = table.Column<string>(type: "TEXT", nullable: true),
                    PostImageHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ContainsSecrets = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevertedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangePlanActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangePlanActions_ChangePlans_ChangePlanId",
                        column: x => x.ChangePlanId,
                        principalTable: "ChangePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangePlanActions_ChangePlanId_Ordinal",
                table: "ChangePlanActions",
                columns: new[] { "ChangePlanId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangePlans_ServerId",
                table: "ChangePlans",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangePlans_Status",
                table: "ChangePlans",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangePlanActions");

            migrationBuilder.DropTable(
                name: "ChangePlans");
        }
    }
}
