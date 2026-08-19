using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

internal static class RoadmapTestSupport
{
    public static readonly Guid KnownProductId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly DateTimeOffset PerpetualExpiry =
        new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_990);

    public static async Task<string> DemoLicenseIdAsync(PostgresWebFixture fixture)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Licenses.AsNoTracking()
            .Where(item => item.Customer.ExternalId == "POC-CUSTOMER")
            .Select(item => item.LicenseId)
            .SingleAsync();
    }

    public static IssueLicenseContract ValidIssueRequest(string suffix = "default") => new(
        $"Phase 0 Customer {suffix}",
        $"phase0-{suffix}@example.com",
        KnownProductId,
        "business",
        "subscription",
        DateTimeOffset.UtcNow.AddYears(1),
        3,
        null,
        new Dictionary<string, object?> { ["source"] = "phase0-test" });

    public static async Task<LicenseRecord> AddLicenseAsync(
        PostgresWebFixture fixture,
        string suffix,
        DateTimeOffset? expiresAt = null,
        bool withActivationHistory = false,
        int seats = 1)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var license = new LicenseRecord
        {
            Id = Guid.NewGuid(),
            LicenseId = $"LIC-{DateTime.UtcNow:yyyy}-{DateTime.UtcNow:MMdd}{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..6]}",
            Customer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = $"Phase 0 {suffix}",
                Email = $"phase0-{suffix}@example.com",
                NormalizedEmail = $"phase0-{suffix}@example.com",
                ExternalId = $"phase0-{suffix}-{Guid.NewGuid():N}",
                CreatedAt = DateTimeOffset.UtcNow
            },
            ActivationCodeHash = LicenseStore.Hash($"PHASE0-{suffix}-ACTIVATION-CODE"),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                contactEmail = $"phase0-{suffix}@example.com"
            }),
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Entitlements =
            [
                new Entitlement
                {
                    Id = Guid.NewGuid(), ProductDefinitionId = KnownProductId,
                    ProductDefinition = null!, Product = "gcexp", Edition = "business",
                    LicenseType = "subscription", Seats = seats, License = null!
                }
            ]
        };
        if (withActivationHistory)
        {
            license.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), ActivationId = $"ACT-{Guid.NewGuid():N}", License = license,
                RequestId = Guid.NewGuid(), DeviceIdHash = new string('A', 64), DeviceIdSuffix = "AAAAAAAAAAAA",
                Mode = "offline", TokenHash = LicenseStore.Hash(Guid.NewGuid().ToString()),
                ActivatedAt = DateTimeOffset.UtcNow.AddMinutes(-5), DeactivatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        }
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license;
    }

    public static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/admin/antiforgery");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("requestToken").GetString() ?? throw new InvalidOperationException("No antiforgery token returned.");
    }

    public static async Task<HttpResponseMessage> PostAdminAsync<T>(HttpClient client, string path, T body)
    {
        var token = await AntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        _ = await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

internal sealed record IssueLicenseContract(
    string CustomerName,
    string CustomerEmail,
    Guid ProductId,
    string Edition,
    string LicenseType,
    DateTimeOffset? ExpiresAt,
    int Seats,
    DateOnly? UpdatesUntil,
    IReadOnlyDictionary<string, object?> Metadata);

internal sealed record IssueLicenseResultContract(
    string LicenseId,
    string ActivationCode,
    JsonElement Metadata,
    IReadOnlyList<IssuedEntitlementContract> Entitlements);

internal sealed record IssuedEntitlementContract(
    string Product,
    string Edition,
    string LicenseType,
    int Seats,
    DateTimeOffset ExpiresAt);
