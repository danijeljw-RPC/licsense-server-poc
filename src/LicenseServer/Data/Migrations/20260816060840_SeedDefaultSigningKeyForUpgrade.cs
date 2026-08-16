using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultSigningKeyForUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A database upgrading from before the key-ring feature already has exactly one
            // "SigningKeys" row: the old DatabaseInitializer unconditionally seeded "primary-2026"
            // on every startup prior to the key-ring rewrite. That pre-existing row means
            // SigningKeyRingService.ReloadAsync's bootstrap-seed check (which only fires when the
            // table has never had any row at all) is already false the first time this version
            // runs, so no key is ever elected default and every activation/refresh signing request
            // fails until an administrator manually picks one. Elect that sole pre-existing row as
            // the default here instead. A brand-new install still has zero "SigningKeys" rows at
            // migration time (this runs before SigningKeyRingService's first reload), so it is
            // unaffected and continues to rely on the runtime bootstrap seed. A database with more
            // than one pre-existing row never went through the old single-key initializer, so it is
            // left for an administrator to pick a default explicitly rather than guessed at here.
            migrationBuilder.Sql("""
                UPDATE "SigningKeys"
                SET "IsDefault" = true
                WHERE (SELECT COUNT(*) FROM "SigningKeys") = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
