using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDerivedSurfaceMirroring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MirrorToDerived",
                table: "ServerSettingValues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MirrorDerivedSurfaces",
                table: "Servers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MirrorDerivedSurfacesChangedAt",
                table: "Servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MirrorDerivedSurfacesChangedBy",
                table: "Servers",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MirrorToDerived",
                table: "ServerSettingValues");

            migrationBuilder.DropColumn(
                name: "MirrorDerivedSurfaces",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MirrorDerivedSurfacesChangedAt",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MirrorDerivedSurfacesChangedBy",
                table: "Servers");
        }
    }
}
