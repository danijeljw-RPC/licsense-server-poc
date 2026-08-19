using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeatAwareMultiMachineActivations : Migration
    {
        private static readonly string[] LicenseRecordIdAndDeactivatedAt = ["LicenseRecordId", "DeactivatedAt"];
        private static readonly string[] LicenseRecordIdAndDeviceIdHash = ["LicenseRecordId", "DeviceIdHash"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activations_LicenseRecordId",
                table: "Activations");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_LicenseRecordId_DeactivatedAt",
                table: "Activations",
                columns: LicenseRecordIdAndDeactivatedAt);

            migrationBuilder.CreateIndex(
                name: "IX_Activations_LicenseRecordId_DeviceIdHash",
                table: "Activations",
                columns: LicenseRecordIdAndDeviceIdHash,
                unique: true,
                filter: "\"DeactivatedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activations_LicenseRecordId_DeactivatedAt",
                table: "Activations");

            migrationBuilder.DropIndex(
                name: "IX_Activations_LicenseRecordId_DeviceIdHash",
                table: "Activations");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_LicenseRecordId",
                table: "Activations",
                column: "LicenseRecordId",
                unique: true,
                filter: "\"DeactivatedAt\" IS NULL");
        }
    }
}
