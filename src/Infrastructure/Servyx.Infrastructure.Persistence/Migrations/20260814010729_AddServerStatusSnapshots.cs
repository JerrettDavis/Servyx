using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServerStatusSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServerStatusSnapshots",
                columns: table => new
                {
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Game = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Health = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    HealthDetail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    HostKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    BindingStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AmbiguousCandidateGameIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PortsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: true),
                    MemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerStatusSnapshots", x => x.ContainerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerStatusSnapshots_UpdatedAt",
                table: "ServerStatusSnapshots",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerStatusSnapshots");
        }
    }
}
