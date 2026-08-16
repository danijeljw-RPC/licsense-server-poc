using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class AdminApiContractTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task RouteInventoryUsesBoundedDtosAndActionPermissions()
    {
        using var client = fixture.CreateAuthenticatedClient(true,
            Permissions.LicensesRead, Permissions.CustomersRead, Permissions.UsersRead,
            Permissions.ProductsRead, Permissions.AuditRead);
        using var licenses = await client.GetAsync("/api/v1/admin/licenses?page=1&pageSize=500&sort=licenseId");
        licenses.EnsureSuccessStatusCode();
        var page = await licenses.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(page.GetProperty("pageSize").GetInt32(), 1, 100);
        Assert.True(page.TryGetProperty("items", out _));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/customers?search=licensing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/audit?action=license.issued&take=10")).StatusCode);

        using var reader = fixture.CreateAuthenticatedClient(false, Permissions.LicensesRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/customers")).StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task DetailEmitsEtagAndPatchHonorsIfMatchConcurrency()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "stage11-etag", DateTimeOffset.UtcNow.AddDays(30));
        using var client = fixture.CreateAuthenticatedClient(true, Permissions.LicensesRead, Permissions.LicensesUpdate);
        using var detail = await client.GetAsync($"/api/v1/admin/licenses/{license.LicenseId}");
        detail.EnsureSuccessStatusCode();
        Assert.Equal($"\"{license.Version}\"", detail.Headers.ETag?.Tag);

        using var stale = await SendWithAntiforgeryAsync(client, HttpMethod.Patch,
            $"/api/v1/admin/licenses/{license.LicenseId}/terms",
            new { expiresAt = DateTimeOffset.UtcNow.AddDays(60), reason = "Contract extension", version = license.Version },
            etag: $"\"{license.Version + 1}\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var updated = await SendWithAntiforgeryAsync(client, HttpMethod.Patch,
            $"/api/v1/admin/licenses/{license.LicenseId}/terms",
            new { expiresAt = DateTimeOffset.UtcNow.AddDays(60), reason = "Contract extension", version = license.Version },
            etag: $"\"{license.Version}\"");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task ActivationCodeRotationRevealsOnceAndInvalidatesPreviousCode()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "stage11-rotate");
        using var client = fixture.CreateAuthenticatedClient(true, Permissions.LicensesUpdate);
        using var response = await SendWithAntiforgeryAsync(client, HttpMethod.Post,
            $"/api/v1/admin/licenses/{license.LicenseId}/activation-code/rotate",
            new { version = license.Version });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var secret = payload.GetProperty("activationCode").GetString() ?? throw new InvalidOperationException();
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(await ReadStoredLicenseAsync(license.LicenseId)), StringComparison.Ordinal);

        using var oldCode = await fixture.Factory.CreateClient().PostAsJsonAsync(
            $"/api/v1/licenses/{license.LicenseId}/activate",
            new ActivateRequest(Guid.NewGuid().ToString("D"), "PHASE0-stage11-rotate-ACTIVATION-CODE",
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), "offline",
                new DeviceRequest("os-machine-id-sha256-v1", new string('A', 64), "stage11")));
        Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task CorrelationAndProblemContractsAreConsistent()
    {
        using var client = fixture.CreateAuthenticatedClient(true, Permissions.LicensesIssue);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/licenses")
        {
            Content = JsonContent.Create(RoadmapTestSupport.ValidIssueRequest("stage11-invalid") with { Edition = "free-text" })
        };
        request.Headers.Add("X-Correlation-ID", "stage11-correlation");
        request.Headers.Add("Idempotency-Key", "stage11-invalid-key");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("stage11-correlation", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public async Task OpenApiPublishesSecuritySchemesAndCompleteAdminPaths()
    {
        using var client = fixture.Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("IdentityCookie", out _));
        Assert.True(schemes.TryGetProperty("ApiKeyBearer", out _));
        var paths = document.GetProperty("paths");
        foreach (var path in new[]
        {
            "/api/v1/admin/licenses", "/api/v1/admin/licenses/{licenseId}",
            "/api/v1/admin/licenses/import",
            "/api/v1/admin/licenses/{licenseId}/activation-code/rotate",
            "/api/v1/admin/products", "/api/v1/admin/customers",
            "/api/v1/admin/users", "/api/v1/admin/audit"
        })
            Assert.True(paths.TryGetProperty(path, out _), $"OpenAPI is missing {path}.");
    }

    private async Task<LicenseRecord> ReadStoredLicenseAsync(string licenseId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Licenses.AsNoTracking()
            .SingleAsync(item => item.LicenseId == licenseId);
    }

    private static async Task<HttpResponseMessage> SendWithAntiforgeryAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        T body,
        string? etag = null)
    {
        var token = await RoadmapTestSupport.AntiforgeryTokenAsync(client);
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
}
