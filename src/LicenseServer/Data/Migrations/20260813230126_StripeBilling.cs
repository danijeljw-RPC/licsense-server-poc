using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class StripeBilling : Migration
    {
        private static readonly string[] WebhookInboxStatusIndexColumns = ["Status", "NextAttemptAt"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LicenseRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LicenseType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Edition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GraceUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelAtPeriodEnd = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SuspendedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingContracts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingContracts_Licenses_LicenseRecordId",
                        column: x => x.LicenseRecordId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingContracts_ProductDefinitions_ProductDefinitionId",
                        column: x => x.ProductDefinitionId,
                        principalTable: "ProductDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StripeCustomerMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeCustomerMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeCustomerMappings_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StripePriceMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripePriceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProductDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Edition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LicenseType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripePriceMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripePriceMappings_ProductDefinitions_ProductDefinitionId",
                        column: x => x.ProductDefinitionId,
                        principalTable: "ProductDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StripeProductMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeProductId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProductDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeProductMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeProductMappings_ProductDefinitions_ProductDefinitionId",
                        column: x => x.ProductDefinitionId,
                        principalTable: "ProductDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebhookInbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderObjectId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookInbox", x => x.Id);
                    table.CheckConstraint("CK_WebhookInbox_Status", "\"Status\" IN ('pending','leased','retry','categorized','completed','ignored','quarantined','dead_letter')");
                });

            migrationBuilder.CreateTable(
                name: "LicenseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LicenseRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicenseOrders_BillingContracts_BillingContractId",
                        column: x => x.BillingContractId,
                        principalTable: "BillingContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseOrders_Licenses_LicenseRecordId",
                        column: x => x.LicenseRecordId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseOrders_ProductDefinitions_ProductDefinitionId",
                        column: x => x.ProductDefinitionId,
                        principalTable: "ProductDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StripeSubscriptionMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BillingContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeSubscriptionMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeSubscriptionMappings_BillingContracts_BillingContract~",
                        column: x => x.BillingContractId,
                        principalTable: "BillingContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StripeCheckoutSessionMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LicenseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeCheckoutSessionMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeCheckoutSessionMappings_LicenseOrders_LicenseOrderId",
                        column: x => x.LicenseOrderId,
                        principalTable: "LicenseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StripeInvoiceMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeInvoiceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LicenseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppliedPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AppliedEventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeInvoiceMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeInvoiceMappings_BillingContracts_BillingContractId",
                        column: x => x.BillingContractId,
                        principalTable: "BillingContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StripeInvoiceMappings_LicenseOrders_LicenseOrderId",
                        column: x => x.LicenseOrderId,
                        principalTable: "LicenseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingContracts_CustomerId",
                table: "BillingContracts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingContracts_LicenseRecordId",
                table: "BillingContracts",
                column: "LicenseRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingContracts_ProductDefinitionId",
                table: "BillingContracts",
                column: "ProductDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseOrders_BillingContractId",
                table: "LicenseOrders",
                column: "BillingContractId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseOrders_CustomerId",
                table: "LicenseOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseOrders_LicenseRecordId",
                table: "LicenseOrders",
                column: "LicenseRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseOrders_ProductDefinitionId",
                table: "LicenseOrders",
                column: "ProductDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_StripeCheckoutSessionMappings_LicenseOrderId",
                table: "StripeCheckoutSessionMappings",
                column: "LicenseOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeCheckoutSessionMappings_StripeCheckoutSessionId",
                table: "StripeCheckoutSessionMappings",
                column: "StripeCheckoutSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeCustomerMappings_CustomerId",
                table: "StripeCustomerMappings",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeCustomerMappings_StripeCustomerId",
                table: "StripeCustomerMappings",
                column: "StripeCustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeInvoiceMappings_AppliedEventId",
                table: "StripeInvoiceMappings",
                column: "AppliedEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeInvoiceMappings_BillingContractId",
                table: "StripeInvoiceMappings",
                column: "BillingContractId");

            migrationBuilder.CreateIndex(
                name: "IX_StripeInvoiceMappings_LicenseOrderId",
                table: "StripeInvoiceMappings",
                column: "LicenseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_StripeInvoiceMappings_StripeInvoiceId",
                table: "StripeInvoiceMappings",
                column: "StripeInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripePriceMappings_ProductDefinitionId",
                table: "StripePriceMappings",
                column: "ProductDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_StripePriceMappings_StripePriceId",
                table: "StripePriceMappings",
                column: "StripePriceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeProductMappings_ProductDefinitionId",
                table: "StripeProductMappings",
                column: "ProductDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_StripeProductMappings_StripeProductId",
                table: "StripeProductMappings",
                column: "StripeProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeSubscriptionMappings_BillingContractId",
                table: "StripeSubscriptionMappings",
                column: "BillingContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeSubscriptionMappings_StripeSubscriptionId",
                table: "StripeSubscriptionMappings",
                column: "StripeSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInbox_ProviderCreatedAt",
                table: "WebhookInbox",
                column: "ProviderCreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInbox_ProviderEventId",
                table: "WebhookInbox",
                column: "ProviderEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInbox_Status_NextAttemptAt",
                table: "WebhookInbox",
                columns: WebhookInboxStatusIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeCheckoutSessionMappings");

            migrationBuilder.DropTable(
                name: "StripeCustomerMappings");

            migrationBuilder.DropTable(
                name: "StripeInvoiceMappings");

            migrationBuilder.DropTable(
                name: "StripePriceMappings");

            migrationBuilder.DropTable(
                name: "StripeProductMappings");

            migrationBuilder.DropTable(
                name: "StripeSubscriptionMappings");

            migrationBuilder.DropTable(
                name: "WebhookInbox");

            migrationBuilder.DropTable(
                name: "LicenseOrders");

            migrationBuilder.DropTable(
                name: "BillingContracts");
        }
    }
}
