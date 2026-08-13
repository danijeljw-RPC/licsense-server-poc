using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AtomicLicenseIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LicenseId",
                table: "Licenses",
                type: "character varying(19)",
                maxLength: 19,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateTable(
                name: "LicenseIdCounters",
                columns: table => new
                {
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastValue = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseIdCounters", x => x.BusinessDate);
                    table.CheckConstraint("CK_LicenseIdCounters_LastValue", "\"LastValue\" BETWEEN 0 AND 16777215");
                });

            migrationBuilder.Sql("""
                WITH parsed AS
                (
                    SELECT
                        to_date(
                            substring("LicenseId" FROM 5 FOR 4)
                            || substring("LicenseId" FROM 10 FOR 4),
                            'YYYYMMDD') AS "BusinessDate",
                        get_byte(decode(right("LicenseId", 6), 'hex'), 0) * 65536
                            + get_byte(decode(right("LicenseId", 6), 'hex'), 1) * 256
                            + get_byte(decode(right("LicenseId", 6), 'hex'), 2) AS "LastValue"
                    FROM "Licenses"
                    WHERE "LicenseId" ~ '^LIC-[0-9]{4}-[0-9]{4}[A-F0-9]{6}$'
                )
                INSERT INTO "LicenseIdCounters" ("BusinessDate", "LastValue")
                SELECT "BusinessDate", max("LastValue")
                FROM parsed
                GROUP BY "BusinessDate";
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION "PreventLicenseIdUpdate"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW."LicenseId" IS DISTINCT FROM OLD."LicenseId" THEN
                        RAISE EXCEPTION 'LicenseId is immutable after insertion.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_Licenses_ImmutableLicenseId"
                BEFORE UPDATE OF "LicenseId" ON "Licenses"
                FOR EACH ROW EXECUTE FUNCTION "PreventLicenseIdUpdate"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Licenses_ImmutableLicenseId" ON "Licenses";
                DROP FUNCTION IF EXISTS "PreventLicenseIdUpdate"();
                """);

            migrationBuilder.DropTable(
                name: "LicenseIdCounters");

            migrationBuilder.AlterColumn<string>(
                name: "LicenseId",
                table: "Licenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(19)",
                oldMaxLength: 19);
        }
    }
}
