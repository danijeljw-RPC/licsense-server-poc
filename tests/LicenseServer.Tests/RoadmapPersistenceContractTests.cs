using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Phase0Roadmap")]
public sealed class RoadmapPersistenceContractTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "04")]
    public async Task DatabaseContainsAtomicLicenseIdCounterAndIssuanceInputHasNoIdOverride()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var counter = db.Model.GetEntityTypes().SingleOrDefault(type => type.ClrType.Name == "LicenseIdCounter");
        Assert.NotNull(counter);
        Assert.NotNull(counter!.FindPrimaryKey());
        Assert.NotNull(counter.FindProperty("BusinessDate"));
        Assert.NotNull(counter.FindProperty("LastValue"));
        Assert.Null(typeof(CreateLicenseInput).GetProperty("LicenseId"));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "06")]
    public async Task CustomerEmailAndJsonbContactMetadataAreDatabaseInvariants()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var customer = db.Model.FindEntityType(typeof(Customer))!;
        var email = customer.FindProperty("Email");
        var normalizedEmail = customer.FindProperty("NormalizedEmail");
        Assert.NotNull(email);
        Assert.NotNull(normalizedEmail);
        Assert.False(email!.IsNullable);
        Assert.False(normalizedEmail!.IsNullable);
        Assert.Contains(customer.GetIndexes(), index => index.Properties.Any(property => property.Name == "NormalizedEmail"));

        var license = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(LicenseRecord))!;
        Assert.Equal("jsonb", license.FindProperty(nameof(LicenseRecord.MetadataJson))!.GetColumnType());
        Assert.Contains(license.GetCheckConstraints(), constraint =>
            constraint.Sql.Contains("contactEmail", StringComparison.Ordinal));

        var demo = await db.Licenses.AsNoTracking()
            .SingleAsync(item => item.Customer.ExternalId == "POC-CUSTOMER");
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Licenses\" SET \"MetadataJson\" = CAST({"{}"} AS jsonb) WHERE \"Id\" = {demo.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        var seededCustomer = await db.Customers.AsNoTracking()
            .SingleAsync(item => item.ExternalId == "POC-CUSTOMER");
        Assert.Equal("licensing@example.com", seededCustomer.Email);
        Assert.Equal("licensing@example.com", seededCustomer.NormalizedEmail);
        Assert.Equal("licensing@example.com", System.Text.Json.JsonDocument.Parse(demo.MetadataJson)
            .RootElement.GetProperty("contactEmail").GetString());
    }

    [Fact]
    [Trait("ExpectedGreenStage", "07")]
    public async Task ProductCatalogIsReferencedByEntitlements()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = db.Model.GetEntityTypes().SingleOrDefault(type => type.ClrType.Name == "ProductDefinition");
        Assert.NotNull(product);
        Assert.NotNull(product!.FindProperty("Code"));
        Assert.NotNull(product.FindProperty("IsActive"));
        var entitlement = db.Model.FindEntityType(typeof(Entitlement))!;
        Assert.Contains(entitlement.GetForeignKeys(), key => key.PrincipalEntityType == product);
        // A single-product-per-license unique index on LicenseRecordId alone predates license
        // import: an imported multi-product license legitimately has more than one Entitlement
        // row per LicenseRecord, so the constraint is composite - still one row per product.
        Assert.Contains(entitlement.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Count == 2
            && index.Properties[0].Name == nameof(Entitlement.LicenseRecordId)
            && index.Properties[1].Name == nameof(Entitlement.Product));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task CancellationIsPersistedSeparatelyFromRevocation()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var license = db.Model.FindEntityType(typeof(LicenseRecord))!;
        Assert.NotNull(license.FindProperty("CancelledAt"));
        Assert.NotNull(license.FindProperty("CancellationReason"));
        Assert.NotNull(license.FindProperty("CancelledBy"));
        Assert.NotNull(license.FindProperty(nameof(LicenseRecord.RevokedAt)));
    }
}
