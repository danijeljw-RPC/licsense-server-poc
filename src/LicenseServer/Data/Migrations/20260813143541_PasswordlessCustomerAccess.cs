using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration uses constant composite-index column arrays.

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class PasswordlessCustomerAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerAccessChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    IdentifierHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    RemoteAddressHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAccessChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAccessChallenges_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccessChallenges_CustomerId",
                table: "CustomerAccessChallenges",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccessChallenges_ExpiresAt",
                table: "CustomerAccessChallenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccessChallenges_IdentifierHash_CreatedAt",
                table: "CustomerAccessChallenges",
                columns: new[] { "IdentifierHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccessChallenges_RemoteAddressHash_CreatedAt",
                table: "CustomerAccessChallenges",
                columns: new[] { "RemoteAddressHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccessChallenges_TokenHash",
                table: "CustomerAccessChallenges",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerAccessChallenges");
        }
    }
}
