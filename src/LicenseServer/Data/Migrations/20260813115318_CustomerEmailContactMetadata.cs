using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerEmailContactMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Licenses"
                    ALTER COLUMN "MetadataJson" TYPE jsonb USING "MetadataJson"::jsonb;
                ALTER TABLE "Customers"
                    ADD COLUMN "Email" character varying(320),
                    ADD COLUMN "NormalizedEmail" character varying(320);

                UPDATE "Customers"
                SET "Email" = CASE
                        WHEN "ExternalId" = 'POC-CUSTOMER' THEN 'licensing@example.com'
                        ELSE 'customer-' || replace("Id"::text, '-', '') || '@example.invalid'
                    END,
                    "NormalizedEmail" = CASE
                        WHEN "ExternalId" = 'POC-CUSTOMER' THEN 'licensing@example.com'
                        ELSE 'customer-' || replace("Id"::text, '-', '') || '@example.invalid'
                    END;

                UPDATE "Licenses" AS license
                SET "MetadataJson" = jsonb_set(
                    license."MetadataJson" - 'contactEmail',
                    '{contactEmail}',
                    to_jsonb(customer."NormalizedEmail"),
                    true)
                FROM "Customers" AS customer
                WHERE customer."Id" = license."CustomerId";

                ALTER TABLE "Customers"
                    ALTER COLUMN "Email" SET NOT NULL,
                    ALTER COLUMN "NormalizedEmail" SET NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Licenses_ContactEmail",
                table: "Licenses",
                sql: "COALESCE(jsonb_typeof(\"MetadataJson\" -> 'contactEmail') = 'string' AND (\"MetadataJson\" ->> 'contactEmail') = lower(btrim(\"MetadataJson\" ->> 'contactEmail')) AND (\"MetadataJson\" ->> 'contactEmail') ~ '^[^[:space:]@]+@[^[:space:]@]+\\.[^[:space:]@]+$', FALSE)");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NormalizedEmail",
                table: "Customers",
                column: "NormalizedEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Licenses_ContactEmail",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Customers_NormalizedEmail",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "Customers");

            migrationBuilder.Sql("""
                ALTER TABLE "Licenses"
                    ALTER COLUMN "MetadataJson" TYPE text USING "MetadataJson"::text;
                """);
        }
    }
}
