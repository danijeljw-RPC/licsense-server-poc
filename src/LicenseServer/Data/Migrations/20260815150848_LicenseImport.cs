using System;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861 // Generated migration uses EF's conventional inline index arrays.

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class LicenseImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entitlements_LicenseRecordId",
                table: "Entitlements");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ImportedAt",
                table: "Licenses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedBy",
                table: "Licenses",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ImportedSignedEnvelope",
                table: "Licenses",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ImportedSignedEnvelopeSha256",
                table: "Licenses",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "Licenses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "issued");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Licenses_ImportProvenance",
                table: "Licenses",
                sql: "(\"Provenance\" = 'imported') = (\"ImportedSignedEnvelope\" IS NOT NULL AND \"ImportedSignedEnvelopeSha256\" IS NOT NULL AND \"ImportedAt\" IS NOT NULL AND \"ImportedBy\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Licenses_Provenance",
                table: "Licenses",
                sql: "\"Provenance\" IN ('issued', 'imported')");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_LicenseRecordId_Product",
                table: "Entitlements",
                columns: new[] { "LicenseRecordId", "Product" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Licenses_ImportProvenance",
                table: "Licenses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Licenses_Provenance",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Entitlements_LicenseRecordId_Product",
                table: "Entitlements");

            migrationBuilder.DropColumn(
                name: "ImportedAt",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ImportedBy",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ImportedSignedEnvelope",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ImportedSignedEnvelopeSha256",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "Licenses");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_LicenseRecordId",
                table: "Entitlements",
                column: "LicenseRecordId",
                unique: true);
        }
    }
}
