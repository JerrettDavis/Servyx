using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostConnectionDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CredentialUrn",
                table: "Hosts",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "Hosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Endpoint",
                table: "Hosts",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PinnedFingerprints",
                table: "Hosts",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredBy",
                table: "Hosts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustPolicy",
                table: "Hosts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_Name",
                table: "Hosts",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Hosts_Name",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "CredentialUrn",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "Endpoint",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "PinnedFingerprints",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RegisteredBy",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "TrustPolicy",
                table: "Hosts");
        }
    }
}
