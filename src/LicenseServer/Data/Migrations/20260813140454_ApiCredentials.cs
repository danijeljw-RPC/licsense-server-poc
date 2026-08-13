using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    HashVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastFour = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    ScopesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReplacedByCredentialId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCredentials", x => x.Id);
                    table.CheckConstraint("CK_ApiCredentials_Lifecycle", "\"ExpiresAt\" IS NULL OR \"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_ApiCredentials_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentials_ExpiresAt",
                table: "ApiCredentials",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentials_OwnerUserId",
                table: "ApiCredentials",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentials_PublicId",
                table: "ApiCredentials",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiCredentials");
        }
    }
}
