using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductEditionCatalog : Migration
    {
        private static readonly string[] LegacyEntitlementIndexColumns = ["LicenseRecordId", "Product"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entitlements_LicenseRecordId_Product",
                table: "Entitlements");

            migrationBuilder.CreateTable(
                name: "ProductDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductDefinitions", x => x.Id);
                    table.CheckConstraint("CK_ProductDefinitions_Code", "\"Code\" ~ '^[a-z0-9][a-z0-9-]{0,99}$'");
                });

            migrationBuilder.AddColumn<Guid>(
                name: "ProductDefinitionId",
                table: "Entitlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO "ProductDefinitions"
                    ("Id", "Code", "DisplayName", "Description", "IsActive", "CreatedAt", "UpdatedAt")
                SELECT
                    CASE WHEN lower(entitlement."Product") = 'gcexp'
                        THEN '11111111-1111-1111-1111-111111111111'::uuid
                        ELSE (
                            substr(md5(lower(entitlement."Product")), 1, 8) || '-' ||
                            substr(md5(lower(entitlement."Product")), 9, 4) || '-' ||
                            substr(md5(lower(entitlement."Product")), 13, 4) || '-' ||
                            substr(md5(lower(entitlement."Product")), 17, 4) || '-' ||
                            substr(md5(lower(entitlement."Product")), 21, 12))::uuid
                    END,
                    lower(entitlement."Product"),
                    CASE WHEN lower(entitlement."Product") = 'gcexp' THEN 'GCE Experience' ELSE min(entitlement."Product") END,
                    'Migrated from an existing immutable entitlement snapshot.',
                    TRUE,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Entitlements" AS entitlement
                GROUP BY lower(entitlement."Product");

                UPDATE "Entitlements" AS entitlement
                SET "ProductDefinitionId" = product."Id"
                FROM "ProductDefinitions" AS product
                WHERE product."Code" = lower(entitlement."Product");

                ALTER TABLE "Entitlements"
                    ALTER COLUMN "ProductDefinitionId" SET NOT NULL;

                UPDATE "Entitlements"
                SET "Edition" = 'business'
                WHERE "Edition" = 'professional';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_LicenseRecordId",
                table: "Entitlements",
                column: "LicenseRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_ProductDefinitionId",
                table: "Entitlements",
                column: "ProductDefinitionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Entitlements_Edition",
                table: "Entitlements",
                sql: "\"Edition\" IN ('community', 'project', 'education', 'consumer', 'business', 'smb', 'enterprise', 'corporate')");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDefinitions_Code",
                table: "ProductDefinitions",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Entitlements_ProductDefinitions_ProductDefinitionId",
                table: "Entitlements",
                column: "ProductDefinitionId",
                principalTable: "ProductDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entitlements_ProductDefinitions_ProductDefinitionId",
                table: "Entitlements");

            migrationBuilder.DropTable(
                name: "ProductDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Entitlements_LicenseRecordId",
                table: "Entitlements");

            migrationBuilder.DropIndex(
                name: "IX_Entitlements_ProductDefinitionId",
                table: "Entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Entitlements_Edition",
                table: "Entitlements");

            migrationBuilder.DropColumn(
                name: "ProductDefinitionId",
                table: "Entitlements");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_LicenseRecordId_Product",
                table: "Entitlements",
                columns: LegacyEntitlementIndexColumns,
                unique: true);
        }
    }
}
