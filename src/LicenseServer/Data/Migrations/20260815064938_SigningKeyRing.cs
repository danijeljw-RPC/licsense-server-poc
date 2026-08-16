using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class SigningKeyRing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DiscoveredAt",
                table: "SigningKeys",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "SigningKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "SigningKeys",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "SigningKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "SigningKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "SigningKeys",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_IsDefault",
                table: "SigningKeys",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SigningKeys_IsDefault",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "DiscoveredAt",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "SigningKeys");
        }
    }
}
