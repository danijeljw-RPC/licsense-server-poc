using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Phase0Roadmap")]
public sealed class ProductCatalogContractTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "07")]
    public async Task KnownProductSeedIsIdempotentAndReportsReferences()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.ProductDefinitions.CountAsync(item => item.Code == "gcexp"));

        var catalog = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
        var product = Assert.Single(await catalog.SearchAsync("gcexp"));
        Assert.Equal(RoadmapTestSupport.KnownProductId, product.Id);
        Assert.True(product.IsActive);
        Assert.True(product.LicenseReferenceCount >= 1);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "07")]
    public async Task ArchivedProductRemainsReadableButCannotIssue()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
        var created = await catalog.CreateAsync(
            $"archive-{Guid.NewGuid():N}", "Archive Test", null, "test-operator");
        await catalog.SetActiveAsync(created.Id, false, "test-operator");

        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var request = RoadmapTestSupport.ValidIssueRequest("archived") with { ProductId = created.Id };
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", request);
        await RoadmapTestSupport.AssertProblemAsync(response, System.Net.HttpStatusCode.BadRequest);

        var archived = Assert.Single(await catalog.SearchAsync(created.Code));
        Assert.False(archived.IsActive);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "07")]
    public async Task ProductCodeAndIssuedSnapshotAreImmutable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await db.ProductDefinitions.SingleAsync(item => item.Id == RoadmapTestSupport.KnownProductId);
        product.Code = "forged-code";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "07")]
    public async Task ControlledEditionCanBeAmendedWithVersionAndAudit()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue", "licenses.update");
        using var issuedResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/licenses", RoadmapTestSupport.ValidIssueRequest("edition-amend"));
        issuedResponse.EnsureSuccessStatusCode();
        var issued = await issuedResponse.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var version = await db.Licenses.Where(item => item.LicenseId == issued.LicenseId)
            .Select(item => item.Version).SingleAsync();
        using var amended = await RoadmapTestSupport.PostAdminAsync(client,
            $"/api/v1/admin/licenses/{issued.LicenseId}/terms",
            new { edition = "enterprise", reason = "contract upgrade", version });
        amended.EnsureSuccessStatusCode();

        db.ChangeTracker.Clear();
        var entitlement = await db.Entitlements.SingleAsync(item => item.License.LicenseId == issued.LicenseId);
        Assert.Equal("enterprise", entitlement.Edition);
        Assert.True(await db.AuditRecords.AnyAsync(item =>
            item.TargetId == issued.LicenseId
            && item.Action == "license.terms-amended"
            && item.ContextJson.Contains("enterprise")));
    }
}
