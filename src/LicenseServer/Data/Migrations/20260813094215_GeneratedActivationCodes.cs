using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class GeneratedActivationCodes : Migration
    {
        private static readonly string[] IdempotencyIndexColumns = ["PrincipalId", "KeyHash"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivationCodeHashVersion",
                table: "Licenses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "sha256-v0");

            migrationBuilder.CreateTable(
                name: "IssuanceIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    RequestHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProtectedResult = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssuanceIdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceIdempotencyRecords_ExpiresAt",
                table: "IssuanceIdempotencyRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceIdempotencyRecords_PrincipalId_KeyHash",
                table: "IssuanceIdempotencyRecords",
                columns: IdempotencyIndexColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssuanceIdempotencyRecords");

            migrationBuilder.DropColumn(
                name: "ActivationCodeHashVersion",
                table: "Licenses");
        }
    }
}
