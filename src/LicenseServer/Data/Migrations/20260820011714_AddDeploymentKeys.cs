using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeploymentKeyId",
                table: "Activations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeploymentKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublicId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    SecretHashVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastFour = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedByDeploymentKeyId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentKeys", x => x.Id);
                    table.CheckConstraint("CK_DeploymentKeys_Lifecycle", "\"ExpiresAt\" IS NULL OR \"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_DeploymentKeys_Licenses_LicenseRecordId",
                        column: x => x.LicenseRecordId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_DeploymentKeyId",
                table: "Activations",
                column: "DeploymentKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentKeys_ExpiresAt",
                table: "DeploymentKeys",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentKeys_LicenseRecordId",
                table: "DeploymentKeys",
                column: "LicenseRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentKeys_PublicId",
                table: "DeploymentKeys",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentKeys_RevokedAt",
                table: "DeploymentKeys",
                column: "RevokedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_Activations_DeploymentKeys_DeploymentKeyId",
                table: "Activations",
                column: "DeploymentKeyId",
                principalTable: "DeploymentKeys",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activations_DeploymentKeys_DeploymentKeyId",
                table: "Activations");

            migrationBuilder.DropTable(
                name: "DeploymentKeys");

            migrationBuilder.DropIndex(
                name: "IX_Activations_DeploymentKeyId",
                table: "Activations");

            migrationBuilder.DropColumn(
                name: "DeploymentKeyId",
                table: "Activations");
        }
    }
}
