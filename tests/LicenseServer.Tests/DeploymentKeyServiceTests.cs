using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class DeploymentKeyServiceTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task CreatedDeploymentKeyIsShownOnceAndOnlyItsHashIsPersisted()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 3);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();

        var created = await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Intune", null), "stage11-test");

        Assert.True(created.Success);
        Assert.StartsWith("dpk_live_", created.Value!.Secret, StringComparison.Ordinal);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.DeploymentKeys.SingleAsync(x => x.Id == created.Value.DeploymentKey.Id);
        var json = JsonSerializer.Serialize(stored);
        Assert.Equal(DeploymentKeyHasher.CurrentVersion, stored.SecretHashVersion);
        Assert.Equal(32, stored.SecretHash.Length);
        Assert.DoesNotContain(created.Value.Secret, json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task MultipleActiveDeploymentKeysCanCoexistOnOneLicense()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 3);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();

        await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Intune", null), "stage11-test");
        await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("RMM", null), "stage11-test");
        await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Servers", null), "stage11-test");

        var listed = await service.ListAsync(licenseId);
        Assert.True(listed.Success);
        Assert.Equal(3, listed.Value!.Count);
        Assert.All(listed.Value, item => Assert.Null(item.RevokedAt));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task RotatingADeploymentKeyInvalidatesThePreviousSecretButKeepsTheKeyUsable()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 3);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();
        var created = await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Intune", null), "stage11-test");

        var rotated = await service.RotateAsync(created.Value!.DeploymentKey.Id, "stage11-test");

        Assert.True(rotated.Success);
        Assert.NotEqual(created.Value.Secret, rotated.Value!.Secret);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var oldKey = await db.DeploymentKeys.SingleAsync(x => x.Id == created.Value.DeploymentKey.Id);
        Assert.NotNull(oldKey.RevokedAt);
        Assert.Equal(rotated.Value.DeploymentKey.Id, oldKey.ReplacedByDeploymentKeyId);

        var enroll = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();
        var withOldSecret = await enroll.EnrollAsync(EnrollRequest(created.Value.Secret, "AA11"), DateTimeOffset.UtcNow);
        Assert.False(withOldSecret.Success);
        var withNewSecret = await enroll.EnrollAsync(EnrollRequest(rotated.Value.Secret, "BB22"), DateTimeOffset.UtcNow);
        Assert.True(withNewSecret.Success);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task RevokingADeploymentKeyDoesNotDeactivateMachinesPreviouslyEnrolledThroughIt()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 3);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();
        var created = await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Intune", null), "stage11-test");
        var enrolled = await service.EnrollAsync(EnrollRequest(created.Value!.Secret, "CC33"), DateTimeOffset.UtcNow);
        Assert.True(enrolled.Success);

        var revoked = await service.RevokeAsync(created.Value.DeploymentKey.Id, "compromised", "stage11-test");
        Assert.True(revoked.Success);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var activation = await db.Activations.SingleAsync(x => x.ActivationId == enrolled.Value!.ActivationId);
        Assert.Null(activation.DeactivatedAt);

        var secondEnroll = await service.EnrollAsync(EnrollRequest(created.Value.Secret, "DD44"), DateTimeOffset.UtcNow);
        Assert.False(secondEnroll.Success);
        Assert.Equal(401, secondEnroll.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task DeploymentKeyEnrollmentIsRejectedWhenTheLicenseSeatPoolIsExhausted()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 1);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();
        var created = await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Intune", null), "stage11-test");

        var first = await service.EnrollAsync(EnrollRequest(created.Value!.Secret, "EE55"), DateTimeOffset.UtcNow);
        Assert.True(first.Success, first.Error);

        var second = await service.EnrollAsync(EnrollRequest(created.Value.Secret, "FF66"), DateTimeOffset.UtcNow);
        Assert.False(second.Success);
        Assert.Equal(409, second.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task AuditRecordsForDeploymentKeyEventsNeverContainThePlaintextSecret()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 3);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();
        var created = await service.CreateAsync(licenseId, new CreateDeploymentKeyRequest("Intune", null), "stage11-test");
        var rotated = await service.RotateAsync(created.Value!.DeploymentKey.Id, "stage11-test");
        await service.EnrollAsync(EnrollRequest(rotated.Value!.Secret, "GG77"), DateTimeOffset.UtcNow);
        await service.RevokeAsync(rotated.Value.DeploymentKey.Id, "compromised", "stage11-test");

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audits = await db.AuditRecords.Where(x => x.TargetType == "deployment-key").ToListAsync();
        var joined = string.Join('\n', audits.Select(x => x.ContextJson));
        Assert.DoesNotContain(created.Value.Secret, joined, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.Value.Secret, joined, StringComparison.Ordinal);
    }

    private async Task<(string LicenseId, Guid LicenseRecordId)> IssueLicenseAsync(int seats)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await db.ProductDefinitions.FirstAsync(x => x.IsActive);
        var issued = await store.IssueAsync(new IssueLicenseRequest(
            $"Deployment Key Test {Guid.NewGuid():N}", $"dpk-{Guid.NewGuid():N}@example.com",
            product.Id, "business", "perpetual", null, seats, null, null),
            new IssuanceContext("stage11-test", "stage11-test", Guid.NewGuid().ToString(), null));
        Assert.True(issued.Success, issued.Error);
        var record = await db.Licenses.SingleAsync(x => x.LicenseId == issued.Value!.LicenseId);
        return (issued.Value!.LicenseId, record.Id);
    }

    private static EnrollDeploymentKeyRequest EnrollRequest(string deploymentKey, string deviceSuffix) => new(
        deploymentKey,
        Guid.NewGuid().ToString(),
        Convert.ToBase64String(RandomToken()),
        "offline",
        new DeviceRequest("os-machine-id-sha256-v1", new string(deviceSuffix[0], 60) + deviceSuffix, "test-device"));

    private static byte[] RandomToken() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
