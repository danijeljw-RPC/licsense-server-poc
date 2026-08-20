using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class DeploymentKeyEnrollmentTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task EnrollmentSharesTheSameSeatPoolAsManualActivation()
    {
        var (licenseId, activationCode) = await IssueLicenseAsync(seats: 2);
        var (secret, enrollClient) = await CreateDeploymentKeyAsync(licenseId, "Intune");

        var first = await enrollClient.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(secret, "AA11"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var activateClient = fixture.Factory.CreateClient();
        var second = await activateClient.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/activate", new
        {
            requestId = Guid.NewGuid().ToString(),
            activationCode,
            activationToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            mode = "offline",
            device = new { scheme = "os-machine-id-sha256-v1", deviceId = new string('B', 60) + "BB22", deviceName = "manual-device" }
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var third = await enrollClient.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(secret, "CC33"));
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task InvalidDeploymentKeyIsRejected()
    {
        using var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/deployment-keys/enroll",
            EnrollBody("dpk_live_0000000000000000_" + new string('A', 43), "DD44"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task RevokedDeploymentKeyIsRejected()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 2);
        var (secret, enrollClient) = await CreateDeploymentKeyAsync(licenseId, "Intune");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var key = await db.DeploymentKeys.SingleAsync(x => x.Name == "Intune" && x.LicenseRecordId ==
                db.Licenses.Single(l => l.LicenseId == licenseId).Id);
            key.RevokedAt = DateTimeOffset.UtcNow;
            key.RevokedBy = "stage11-test";
            await db.SaveChangesAsync();
        }

        var response = await enrollClient.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(secret, "EE55"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task ExpiredDeploymentKeyIsRejected()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 2);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentKeyService>();
        var created = await service.CreateAsync(licenseId,
            new CreateDeploymentKeyRequest("Expiring", DateTimeOffset.UtcNow.AddMinutes(1)), "stage11-test");
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var key = await db.DeploymentKeys.SingleAsync(x => x.Id == created.Value!.DeploymentKey.Id);
        // CK_DeploymentKeys_Lifecycle requires ExpiresAt IS NULL OR ExpiresAt > CreatedAt, so both
        // timestamps must move into the past together to simulate an already-expired key.
        key.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        key.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        using var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(created.Value!.Secret, "FF66"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task DeploymentKeyGrantsNoAccessToAdminOrCustomerApis()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 2);
        var (secret, _) = await CreateDeploymentKeyAsync(licenseId, "Intune");

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRead}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/admin/licenses/{licenseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/admin/licenses/{licenseId}/deployment-keys")).StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task EnrollmentRateLimitRejectsBurstsFromTheSamePublicId()
    {
        var (licenseId, _) = await IssueLicenseAsync(seats: 50);
        var (secret, client) = await CreateDeploymentKeyAsync(licenseId, "RateLimited");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(secret, $"{i:D4}"));
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    private async Task<(string LicenseId, string ActivationCode)> IssueLicenseAsync(int seats)
    {
        // Issued directly through LicenseStore rather than the admin HTTP endpoint or the shared
        // seeded demo license: LicensingFlowTests.cs mutates (and eventually revokes) that demo
        // license, and since every Postgres-backed test class shares one xUnit collection (so runs
        // sequentially against the same database), reusing it here would make these tests order-
        // dependent on what LicensingFlowTests left behind. A freshly issued license avoids that.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await db.ProductDefinitions.FirstAsync(x => x.IsActive);
        var issued = await store.IssueAsync(new IssueLicenseRequest(
            $"Enrollment Test {Guid.NewGuid():N}", $"enroll-{Guid.NewGuid():N}@example.com",
            product.Id, "business", "perpetual", null, seats, null, null),
            new IssuanceContext("stage11-test", "stage11-test", Guid.NewGuid().ToString(), null));
        Assert.True(issued.Success, issued.Error);
        return (issued.Value!.LicenseId, issued.Value.ActivationCode);
    }

    /// <summary>
    /// Creates the deployment key through the real admin HTTP endpoint and returns the same
    /// HttpClient used to create it (instead of a fresh `fixture.Factory.CreateClient()`). Each
    /// `fixture.CreateAuthenticatedClient(...)` call spins up its own WebApplicationFactory via
    /// `WithWebHostBuilder`, i.e. its own DI container with its own `DeploymentKeyHasher` singleton.
    /// Enrollment (`POST /api/v1/deployment-keys/enroll`) is anonymous, so reusing this same client
    /// for the follow-up enroll call is a legitimate real-world sequence (nothing about the endpoint
    /// requires a fresh, unauthenticated connection) and keeps hashing consistent between the host
    /// that minted the secret and the host that verifies it.
    /// </summary>
    private async Task<(string Secret, HttpClient Client)> CreateDeploymentKeyAsync(string licenseId, string name)
    {
        var client = fixture.CreateAuthenticatedClient(administrator: true, Permissions.DeploymentKeysManage);
        var response = await RoadmapTestSupport.PostAdminAsync(
            client, $"/api/v1/admin/licenses/{licenseId}/deployment-keys", new { name });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("secret").GetString()!, client);
    }

    private static object EnrollBody(string deploymentKey, string deviceSuffix) => new
    {
        deploymentKey,
        requestId = Guid.NewGuid().ToString(),
        activationToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        mode = "offline",
        device = new { scheme = "os-machine-id-sha256-v1", deviceId = new string('A', 60) + deviceSuffix, deviceName = "enrolled-device" }
    };
}
