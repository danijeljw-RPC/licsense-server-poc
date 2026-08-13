using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        var license = db.Model.FindEntityType(typeof(LicenseRecord))!;
        Assert.Equal("jsonb", license.FindProperty(nameof(LicenseRecord.MetadataJson))!.GetColumnType());
        Assert.Contains(license.GetCheckConstraints(), constraint =>
            constraint.Sql.Contains("contactEmail", StringComparison.Ordinal));
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
