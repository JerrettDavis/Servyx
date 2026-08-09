using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServerContainerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "HostId",
                table: "Servers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "ContainerId",
                table: "Servers",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_ContainerId",
                table: "Servers",
                column: "ContainerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Servers_ContainerId",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "ContainerId",
                table: "Servers");

            migrationBuilder.AlterColumn<Guid>(
                name: "HostId",
                table: "Servers",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
