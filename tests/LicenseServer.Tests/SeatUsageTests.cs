using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftwareLicensing;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class SeatUsageTests(PostgresWebFixture fixture)
{
    [Fact]
    public async Task ActiveAndAvailableSeatCountsAreCorrect()
    {
        var (licenseId, _, code) = await IssueLicenseAsync(seats: 5);
        await ActivateAsync(licenseId, code, DeviceId(1));
        await ActivateAsync(licenseId, code, DeviceId(2));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var data = scope.ServiceProvider.GetRequiredService<AdminDataService>();
        var detail = await data.GetLicenseAsync(licenseId);

        Assert.NotNull(detail);
        Assert.Equal(5, detail!.SeatLimit);
        Assert.Equal(2, detail.ActiveSeatCount);
        Assert.Equal(3, detail.AvailableSeats);
        Assert.Equal(0, detail.HistoricalActivationCount);
    }

    [Fact]
    public async Task IncreasingSeatsAllowsAdditionalActivationImmediately()
    {
        var (licenseId, _, code) = await IssueLicenseAsync(seats: 1);
        await ActivateAsync(licenseId, code, DeviceId(1));

        await using var blockedScope = fixture.Factory.Services.CreateAsyncScope();
        var blockedStore = blockedScope.ServiceProvider.GetRequiredService<LicenseStore>();
        var blocked = await blockedStore.ActivateAsync(licenseId, ActivateRequest(code, DeviceId(2)), DateTimeOffset.UtcNow);
        Assert.False(blocked.Success);
        Assert.Equal(409, blocked.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var version = await db.Licenses.Where(x => x.LicenseId == licenseId).Select(x => x.Version).SingleAsync();
        var amended = await store.AmendTermsAsync(
            licenseId, new AmendTermsRequest(null, 2, null, "Customer approaching limit", version),
            "seat-usage-test", DateTimeOffset.UtcNow, null);
        Assert.True(amended.Success, amended.Error);

        await using var allowedScope = fixture.Factory.Services.CreateAsyncScope();
        var allowedStore = allowedScope.ServiceProvider.GetRequiredService<LicenseStore>();
        var allowed = await allowedStore.ActivateAsync(licenseId, ActivateRequest(code, DeviceId(2)), DateTimeOffset.UtcNow);
        Assert.True(allowed.Success, allowed.Error);
    }

    [Fact]
    public async Task NormalUpdateCannotLowerSeatsBelowActiveActivationCount()
    {
        var (licenseId, _, code) = await IssueLicenseAsync(seats: 3);
        await ActivateAsync(licenseId, code, DeviceId(1));
        await ActivateAsync(licenseId, code, DeviceId(2));
        await ActivateAsync(licenseId, code, DeviceId(3));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var version = await db.Licenses.Where(x => x.LicenseId == licenseId).Select(x => x.Version).SingleAsync();

        var rejected = await store.AmendTermsAsync(
            licenseId, new AmendTermsRequest(null, 2, null, "Attempted over-reduction", version),
            "seat-usage-test", DateTimeOffset.UtcNow, null);
        Assert.False(rejected.Success);
        Assert.Equal(409, rejected.StatusCode);
        Assert.Contains("cannot be reduced", rejected.Error, StringComparison.OrdinalIgnoreCase);

        var seats = await db.Entitlements.Where(x => x.License.LicenseId == licenseId).Select(x => x.Seats).SingleAsync();
        Assert.Equal(3, seats);
    }

    [Fact]
    public async Task IncreasingSeatsPreservesLicenseIdAndExistingActivations()
    {
        var (licenseId, licenseRecordId, code) = await IssueLicenseAsync(seats: 500);
        for (var i = 0; i < 5; i++) await ActivateAsync(licenseId, code, DeviceId(i));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var version = await db.Licenses.Where(x => x.LicenseId == licenseId).Select(x => x.Version).SingleAsync();

        var amended = await store.AmendTermsAsync(
            licenseId, new AmendTermsRequest(null, 600, null, "Customer purchased additional seats", version),
            "seat-usage-test", DateTimeOffset.UtcNow, null);
        Assert.True(amended.Success, amended.Error);

        var stored = await db.Licenses.Include(x => x.Entitlements).Include(x => x.Activations)
            .SingleAsync(x => x.Id == licenseRecordId);
        Assert.Equal(licenseId, stored.LicenseId);
        Assert.Equal(600, stored.Entitlements.Single().Seats);
        Assert.Equal(5, stored.Activations.Count(x => x.DeactivatedAt == null));
        Assert.Equal(version + 1, stored.Version);
    }

    [Fact]
    public async Task AuditRecordCapturesSeatCountUpdateSafely()
    {
        var (licenseId, _, _) = await IssueLicenseAsync(seats: 10);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var version = await db.Licenses.Where(x => x.LicenseId == licenseId).Select(x => x.Version).SingleAsync();

        var amended = await store.AmendTermsAsync(
            licenseId, new AmendTermsRequest(null, 15, null, "Support ticket #4821", version),
            "operator@example.com", DateTimeOffset.UtcNow, "corr-1");
        Assert.True(amended.Success, amended.Error);

        var record = await db.AuditRecords
            .Where(x => x.TargetId == licenseId && x.Action == "license.terms-amended")
            .OrderByDescending(x => x.TimestampUtc)
            .FirstAsync();
        Assert.Equal("operator@example.com", record.Actor);
        Assert.Equal("success", record.Result);
        using var context = JsonDocument.Parse(record.ContextJson);
        Assert.Equal(10, context.RootElement.GetProperty("old").GetProperty("Seats").GetInt32());
        Assert.Equal(15, context.RootElement.GetProperty("new").GetProperty("Seats").GetInt32());
        Assert.Contains("Support ticket", record.ContextJson);
        Assert.DoesNotContain("ActivationToken", record.ContextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TokenHash", record.ContextJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentSeatDecreaseAndActivationNeverProduceAnInvalidOverAllocatedState()
    {
        var (licenseId, licenseRecordId, code) = await IssueLicenseAsync(seats: 2);
        await ActivateAsync(licenseId, code, DeviceId(1));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var version = await db.Licenses.Where(x => x.LicenseId == licenseId).Select(x => x.Version).SingleAsync();

        var lowerToOne = Task.Run(async () =>
        {
            await using var innerScope = fixture.Factory.Services.CreateAsyncScope();
            var store = innerScope.ServiceProvider.GetRequiredService<LicenseStore>();
            return await store.AmendTermsAsync(
                licenseId, new AmendTermsRequest(null, 1, null, "Concurrent reduction attempt", version),
                "operator-a", DateTimeOffset.UtcNow, null);
        });
        var secondActivation = Task.Run(async () =>
        {
            await using var innerScope = fixture.Factory.Services.CreateAsyncScope();
            var store = innerScope.ServiceProvider.GetRequiredService<LicenseStore>();
            return await store.ActivateAsync(licenseId, ActivateRequest(code, DeviceId(2)), DateTimeOffset.UtcNow);
        });
        await Task.WhenAll(lowerToOne, secondActivation);

        var stored = await db.Licenses.Include(x => x.Entitlements).Include(x => x.Activations)
            .AsNoTracking().SingleAsync(x => x.Id == licenseRecordId);
        var activeCount = stored.Activations.Count(x => x.DeactivatedAt == null);
        var seats = stored.Entitlements.Single().Seats;
        Assert.True(activeCount <= seats, $"Invalid state: {activeCount} active activations against {seats} seats.");
    }

    private async Task<(string LicenseId, Guid LicenseRecordId, string ActivationCode)> IssueLicenseAsync(int seats)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await db.ProductDefinitions.FirstAsync(x => x.IsActive);
        var issued = await store.IssueAsync(new IssueLicenseRequest(
            $"Seat Usage Test {Guid.NewGuid():N}", $"seat-usage-{Guid.NewGuid():N}@example.com",
            product.Id, "business", "perpetual", null, seats, null, null),
            new IssuanceContext("seat-usage-test", "seat-usage-test", Guid.NewGuid().ToString(), null));
        Assert.True(issued.Success, issued.Error);
        var record = await db.Licenses.SingleAsync(x => x.LicenseId == issued.Value!.LicenseId);
        return (issued.Value!.LicenseId, record.Id, issued.Value!.ActivationCode);
    }

    private async Task ActivateAsync(string licenseId, string activationCode, string deviceId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var result = await store.ActivateAsync(licenseId, ActivateRequest(activationCode, deviceId), DateTimeOffset.UtcNow);
        Assert.True(result.Success, result.Error);
    }

    private static ActivateRequest ActivateRequest(string activationCode, string deviceId) => new(
        Guid.NewGuid().ToString("D"),
        activationCode,
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        "offline",
        new DeviceRequest(DeviceIdentity.Scheme, deviceId, "seat-usage-test-device"));

    private static string DeviceId(int index) => index.ToString("X", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0');
}
