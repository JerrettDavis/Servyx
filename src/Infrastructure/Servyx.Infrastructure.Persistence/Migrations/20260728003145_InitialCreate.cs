using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ConnectorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProvisionedByJobId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderResourceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProviderAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderAccounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DefaultRegion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CredentialUrnsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeHint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProvisionedResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProvisionerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderResourceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedResources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GameDefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdoptionMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WriteMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WriteModeChangedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WriteModeChangedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_ConnectorId",
                table: "Hosts",
                column: "ConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_ProviderAccountId_ProviderResourceId",
                table: "Hosts",
                columns: new[] { "ProviderAccountId", "ProviderResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_ProviderId",
                table: "ProviderAccounts",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedResources_ProvisionerId_ProviderResourceId",
                table: "ProvisionedResources",
                columns: new[] { "ProvisionerId", "ProviderResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedResources_State",
                table: "ProvisionedResources",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_GameDefinitionId",
                table: "Servers",
                column: "GameDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_HostId",
                table: "Servers",
                column: "HostId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Hosts");

            migrationBuilder.DropTable(
                name: "ProviderAccounts");

            migrationBuilder.DropTable(
                name: "ProvisionedResources");

            migrationBuilder.DropTable(
                name: "Servers");
        }
    }
}
