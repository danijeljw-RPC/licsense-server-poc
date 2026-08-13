using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration uses a constant composite-index column array.

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableTransactionalEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailDeliveryEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDeliveryEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    RecipientHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: false),
                    IdempotencyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.Id);
                    table.CheckConstraint("CK_EmailOutbox_Status", "\"Status\" IN ('pending','leased','retry','sent','delivered','bounced','complained','failed','uncertain')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailDeliveryEvents_ProviderEventId",
                table: "EmailDeliveryEvents",
                column: "ProviderEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailDeliveryEvents_ProviderMessageId",
                table: "EmailDeliveryEvents",
                column: "ProviderMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_IdempotencyHash",
                table: "EmailOutbox",
                column: "IdempotencyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_ProviderMessageId",
                table: "EmailOutbox",
                column: "ProviderMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_RetainUntil",
                table: "EmailOutbox",
                column: "RetainUntil");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Status_NextAttemptAt",
                table: "EmailOutbox",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailDeliveryEvents");

            migrationBuilder.DropTable(
                name: "EmailOutbox");
        }
    }
}
