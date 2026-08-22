using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendStripeProductMappingForOneTimePurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Edition",
                table: "StripeProductMappings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "StripeProductMappings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenseType",
                table: "StripeProductMappings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Seats",
                table: "StripeProductMappings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "StripeProductMappings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateOnly>(
                name: "UpdatesUntil",
                table: "StripeProductMappings",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Edition",
                table: "StripeProductMappings");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "StripeProductMappings");

            migrationBuilder.DropColumn(
                name: "LicenseType",
                table: "StripeProductMappings");

            migrationBuilder.DropColumn(
                name: "Seats",
                table: "StripeProductMappings");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "StripeProductMappings");

            migrationBuilder.DropColumn(
                name: "UpdatesUntil",
                table: "StripeProductMappings");
        }
    }
}
