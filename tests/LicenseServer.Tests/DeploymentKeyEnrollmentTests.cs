using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        Assert.Contains("has been revoked", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("has expired", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
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
        // Every request here shares one credential (and, incidentally, one IP, since it's all one
        // isolated host/client). The fixture sets the credential permit limit (10) strictly below
        // the IP permit limit (20), so the 429 this test asserts on can only be explained by the
        // credential-dimension limiter tripping first - the IP-dimension limiter alone could not
        // yet account for it at that point in the burst.
        var (licenseId, _) = await IssueLicenseAsync(seats: 50);
        var (secret, client) = await CreateDeploymentKeyAsync(licenseId, "RateLimited");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(secret, $"{i:D4}"));
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.True(statuses.Take(10).All(s => s != HttpStatusCode.TooManyRequests),
            "The credential permit limit is 10, so the first 429 must not appear before the 11th request.");
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses.Skip(10).Take(10));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task EnrollmentRateLimitUsesTrustedForwardedClientAddressForItsIpDimension()
    {
        // The credential partition is deliberately made too large to trip here, and each request uses
        // a distinct fake public ID, so any 429 must come from the coarse IP limiter. Trusting the
        // loopback proxy in this test lets X-Forwarded-For drive that limiter the same way a real
        // trusted ingress would in production.
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:DeploymentKeyEnrollPermitLimit"] = "100",
                ["RateLimits:DeploymentKeyEnrollIpPermitLimit"] = "2",
                ["Security:ForwardedHeaders:KnownProxies:0"] = IPAddress.Loopback.ToString(),
                ["Security:ForwardedHeaders:KnownProxies:1"] = IPAddress.IPv6Loopback.ToString()
            })));
        using var client = factory.CreateClient();

        async Task<HttpStatusCode> EnrollAsync(string forwardedFor, int requestNumber)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deployment-keys/enroll")
            {
                Content = JsonContent.Create(EnrollBody(
                    $"dpk_live_{requestNumber:X16}_{new string('A', 43)}",
                    $"{requestNumber:D4}"))
            };
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            return (await client.SendAsync(request)).StatusCode;
        }

        var firstIpStatuses = new[]
        {
            await EnrollAsync("198.51.100.10", 1),
            await EnrollAsync("198.51.100.10", 2),
            await EnrollAsync("198.51.100.10", 3)
        };
        var secondIpStatuses = new[]
        {
            await EnrollAsync("198.51.100.20", 4),
            await EnrollAsync("198.51.100.20", 5)
        };

        Assert.Equal(HttpStatusCode.Unauthorized, firstIpStatuses[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, firstIpStatuses[1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, firstIpStatuses[2]);
        Assert.All(secondIpStatuses, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task SpoofedForwardedForIsIgnoredWhenNoTrustedProxyIsConfigured()
    {
        // No Security:ForwardedHeaders:* config is set on this isolated host, matching the fixture's
        // (and production's blank-.env) default. If X-Forwarded-For were honored regardless of
        // configured trust, an anonymous caller could present a different spoofed address on every
        // request and never accumulate against the real IP-dimension partition - reproducing exactly
        // the P1 finding this fix closes. Asserting the IP limiter still trips proves the opposite:
        // the forwarded header is ignored entirely, and every request is attributed to the one real
        // connection address regardless of what it claims.
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:DeploymentKeyEnrollPermitLimit"] = "100",
                ["RateLimits:DeploymentKeyEnrollIpPermitLimit"] = "10"
            })));
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 15; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deployment-keys/enroll")
            {
                Content = JsonContent.Create(EnrollBody($"dpk_live_{i:X16}_{new string('A', 43)}", $"{i:D4}"))
            };
            // A distinct, entirely fabricated source per request - if trusted, each would land in
            // its own partition and this limiter could never trip within only 15 requests.
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{i}");
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task EnrollmentRateLimitAppliesPerIpEvenWhenThePresentedPublicIdVariesPerRequest()
    {
        // A caller who varies the well-formed-but-fake public ID on every request gets a brand-new
        // credential-partition window each time, so the credential-only limiter alone would never
        // trip. The IP-dimension limiter enforced independently in the enrollment middleware must
        // still bound this caller - this is the regression test for that requirement.
        //
        // Runs against an isolated host (matching the isolation CreateAuthenticatedClient already
        // uses for the same reason) rather than fixture.Factory directly: this test deliberately
        // exhausts a whole rate-limit window, and fixture.Factory is shared by every other test in
        // this run - burning its IP-dimension window here would spuriously 429 unrelated tests that
        // also hit this endpoint afterward within the same one-minute fixed window.
        await using var factory = fixture.Factory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var fakeKey = $"dpk_live_{i:X16}_{new string('A', 43)}";
            var response = await client.PostAsJsonAsync("/api/v1/deployment-keys/enroll", EnrollBody(fakeKey, $"{i:D4}"));
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
