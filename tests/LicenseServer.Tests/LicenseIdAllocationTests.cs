using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Phase0Roadmap")]
public sealed class LicenseIdAllocationTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "04")]
    public void DefaultBusinessTimeZoneRollsTheDateAtAdelaideMidnight()
    {
        var resolver = new ConfiguredLicenseBusinessDateResolver(Options.Create(new LicensingOptions()));

        Assert.Equal(new DateOnly(2025, 12, 31), resolver.Resolve(new DateTimeOffset(2025, 12, 31, 13, 29, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 1, 1), resolver.Resolve(new DateTimeOffset(2025, 12, 31, 13, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "04")]
    public async Task RolledBackAllocationDoesNotConsumeAValue()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var allocator = scope.ServiceProvider.GetRequiredService<LicenseIdAllocator>();
        var resolver = scope.ServiceProvider.GetRequiredService<ILicenseBusinessDateResolver>();
        var now = DateTimeOffset.UtcNow;
        var date = resolver.Resolve(now);
        var before = await db.LicenseIdCounters.AsNoTracking()
            .Where(item => item.BusinessDate == date)
            .Select(item => (int?)item.LastValue)
            .SingleOrDefaultAsync();

        string rolledBackId;
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            rolledBackId = await allocator.AllocateAsync(now);
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        var after = await db.LicenseIdCounters.AsNoTracking()
            .Where(item => item.BusinessDate == date)
            .Select(item => (int?)item.LastValue)
            .SingleOrDefaultAsync();
        Assert.Equal(before, after);

        await using var committed = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var reusedId = await allocator.AllocateAsync(now);
        Assert.Equal(rolledBackId, reusedId);
        await committed.RollbackAsync();
    }

    [Fact]
    [Trait("ExpectedGreenStage", "04")]
    public async Task AllocationFailsClearlyAtTheDailyMaximum()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var allocator = scope.ServiceProvider.GetRequiredService<LicenseIdAllocator>();
        var resolver = scope.ServiceProvider.GetRequiredService<ILicenseBusinessDateResolver>();
        var instant = new DateTimeOffset(2200, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var date = resolver.Resolve(instant);
        db.LicenseIdCounters.Add(new LicenseIdCounter
        {
            BusinessDate = date,
            LastValue = LicenseIdAllocator.MaximumDailyValue
        });
        await db.SaveChangesAsync();

        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            var exception = await Assert.ThrowsAsync<LicenseIdExhaustedException>(() => allocator.AllocateAsync(instant));
            Assert.Contains("0xFFFFFF", exception.Message, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        db.LicenseIdCounters.Remove(await db.LicenseIdCounters.SingleAsync(item => item.BusinessDate == date));
        await db.SaveChangesAsync();
    }

    [Fact]
    [Trait("ExpectedGreenStage", "04")]
    public async Task CallerSuppliedLicenseIdIsIgnoredAndPersistedIdIsImmutable()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var valid = RoadmapTestSupport.ValidIssueRequest("forged-id");
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            licenseId = "LIC-2000-0101FFFFFF",
            valid.CustomerName,
            valid.CustomerEmail,
            valid.ProductId,
            valid.Edition,
            valid.LicenseType,
            valid.ExpiresAt,
            valid.Seats,
            valid.UpdatesUntil,
            valid.Metadata
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");
        Assert.NotEqual("LIC-2000-0101FFFFFF", issued.LicenseId);
        Assert.Null(typeof(IssueLicenseRequest).GetProperty("LicenseId"));
        Assert.Null(typeof(CreateLicenseInput).GetProperty("LicenseId"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        const string forgedId = "LIC-2000-0101FFFFFF";
        var databaseException = await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Licenses\" SET \"LicenseId\" = {forgedId} WHERE \"LicenseId\" = {issued.LicenseId}"));
        Assert.Contains("immutable", databaseException.Message, StringComparison.OrdinalIgnoreCase);

        var license = await db.Licenses.SingleAsync(item => item.LicenseId == issued.LicenseId);
        license.LicenseId = forgedId;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
