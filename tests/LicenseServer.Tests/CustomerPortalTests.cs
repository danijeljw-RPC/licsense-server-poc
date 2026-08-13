using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class CustomerPortalTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "13")]
    public async Task RequestAlwaysReturnsGenericResponseAndStoresOnlyTokenHash()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "stage13-request");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var email = await db.Licenses.Where(item => item.LicenseId == license.LicenseId)
            .Select(item => item.Customer.Email).SingleAsync();
        using var client = fixture.Factory.CreateClient();

        var matched = await client.PostAsJsonAsync("/api/v1/customer-access/request", new { email, licenseId = license.LicenseId });
        var missing = await client.PostAsJsonAsync("/api/v1/customer-access/request", new { email = "nobody@example.com", licenseId = license.LicenseId });
        Assert.Equal(HttpStatusCode.Accepted, matched.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, missing.StatusCode);
        Assert.Equal(await matched.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());

        var challenge = await db.CustomerAccessChallenges.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        Assert.Equal(32, challenge.TokenHash.Length);
        Assert.InRange(challenge.ExpiresAt - challenge.CreatedAt, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));
        Assert.DoesNotContain(email, JsonSerializer.Serialize(challenge), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "13")]
    public async Task TokenIsSingleUseAndCustomerSessionCannotCrossRecords()
    {
        var own = await RoadmapTestSupport.AddLicenseAsync(fixture, "stage13-own");
        var other = await RoadmapTestSupport.AddLicenseAsync(fixture, "stage13-other");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CustomerAccessService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var email = await db.Licenses.Where(item => item.LicenseId == own.LicenseId)
            .Select(item => item.Customer.Email).SingleAsync();
        var issued = await service.RequestAsync(email, own.LicenseId, "127.0.0.1");
        Assert.NotNull(issued.DevelopmentToken);

        using var client = fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });
        var consumed = await client.GetAsync($"/customer/access/consume?token={Uri.EscapeDataString(issued.DevelopmentToken!)}");
        Assert.Equal(HttpStatusCode.Redirect, consumed.StatusCode);
        Assert.Contains(consumed.Headers.GetValues("Set-Cookie"),
            value => value.Contains("LicenseServer.Customer", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/customer/licenses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/customer/licenses/{other.LicenseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateClient().GetAsync("/api/v1/customer/licenses")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/customer/access/consume?token={Uri.EscapeDataString(issued.DevelopmentToken!)}")).StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "13")]
    public async Task PortalProjectionRedactsSecretsAndFullDeviceIdentifiers()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "stage13-redact");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CustomerAccessService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var record = await db.Licenses.Include(item => item.Customer).SingleAsync(item => item.LicenseId == license.LicenseId);
        var issued = await service.RequestAsync(record.Customer.Email, record.LicenseId, "127.0.0.2");
        var customerId = await service.ConsumeAsync(issued.DevelopmentToken!);
        var result = await service.GetLicenseAsync(customerId!.Value, record.LicenseId);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("ActivationCodeHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MetadataJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeviceIdHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result!.ExpiresAt);
    }
}
