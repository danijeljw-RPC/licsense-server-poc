using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Phase0Roadmap")]
public sealed class RoadmapIssuanceContractTests(PostgresWebFixture fixture)
{
    private static readonly Regex LicenseIdPattern = new(
        "^LIC-[0-9]{4}-[0-9]{4}[A-F0-9]{6}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ActivationCodePattern = new(
        "^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{8}(?:-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}){3}-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{12}$",
        RegexOptions.CultureInvariant);

    [Fact]
    [Trait("ExpectedGreenStage", "04")]
    public async Task OneThousandConcurrentIssuesAllocateUniqueMonotonicLicenseIds()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var requests = Enumerable.Range(0, 1_000).Select(async index =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/licenses")
            {
                Content = JsonContent.Create(RoadmapTestSupport.ValidIssueRequest($"concurrent-{index}"))
            };
            request.Headers.Add("Idempotency-Key", $"phase0-concurrent-{index}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>();
            return issued ?? throw new InvalidOperationException("Issuance response was empty.");
        });

        var issued = await Task.WhenAll(requests);
        Assert.All(issued, item => Assert.Matches(LicenseIdPattern, item.LicenseId));
        Assert.Equal(issued.Length, issued.Select(item => item.LicenseId).Distinct(StringComparer.Ordinal).Count());

        var orderedSuffixes = issued
            .GroupBy(item => item.LicenseId[..13], StringComparer.Ordinal)
            .Select(group => group.Select(item => Convert.ToInt32(item.LicenseId[^6..], 16)).Order().ToArray());
        Assert.All(orderedSuffixes, values =>
            Assert.All(values.Zip(values.Skip(1)), pair => Assert.True(pair.Second > pair.First)));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "05")]
    public async Task ActivationCodeUsesSafeAlphabetIsRevealedOnceAndLeavesNoPlaintextTrace()
    {
        fixture.CapturedLogs.Clear();
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue", "licenses.read", "audit.read");
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", RoadmapTestSupport.ValidIssueRequest("secret"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");
        Assert.Matches(ActivationCodePattern, issued.ActivationCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.Licenses.AsNoTracking().SingleAsync(item => item.LicenseId == issued.LicenseId);
        var audit = await db.AuditRecords.AsNoTracking().Where(item => item.TargetId == issued.LicenseId).ToListAsync();
        Assert.Equal(ActivationCodeHasher.CurrentVersion, row.ActivationCodeHashVersion);
        Assert.DoesNotContain(issued.ActivationCode, Convert.ToHexString(row.ActivationCodeHash), StringComparison.Ordinal);
        Assert.All(audit, item => Assert.DoesNotContain(issued.ActivationCode, item.ContextJson, StringComparison.Ordinal));
        Assert.All(fixture.CapturedLogs.Messages, message => Assert.DoesNotContain(issued.ActivationCode, message, StringComparison.Ordinal));

        using var later = await client.GetAsync($"/api/v1/admin/licenses/{Uri.EscapeDataString(issued.LicenseId)}");
        var laterBody = await later.Content.ReadAsStringAsync();
        Assert.DoesNotContain(issued.ActivationCode, laterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("activationCode", laterBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "05")]
    public async Task SamePrincipalAndIdempotencyKeyReturnsEncryptedRetryResultWithoutDuplicateIssuance()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var requestBody = RoadmapTestSupport.ValidIssueRequest($"idempotent-{Guid.NewGuid():N}");
        var key = $"phase05-{Guid.NewGuid():N}";

        async Task<IssueLicenseResultContract> IssueAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/licenses")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
                ?? throw new InvalidOperationException("Issuance response was empty.");
        }

        var first = await IssueAsync();
        var retry = await IssueAsync();
        Assert.Equal(first.LicenseId, retry.LicenseId);
        Assert.Equal(first.ActivationCode, retry.ActivationCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.Licenses.CountAsync(item => item.LicenseId == first.LicenseId));
        var keyHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        var retryRecord = await db.IssuanceIdempotencyRecords.AsNoTracking()
            .SingleAsync(item => item.KeyHash == keyHash);
        Assert.DoesNotContain(first.ActivationCode, retryRecord.ProtectedResult, StringComparison.Ordinal);
        Assert.DoesNotContain(key, Convert.ToHexString(retryRecord.KeyHash), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "05")]
    public async Task ReusingAnIdempotencyKeyForDifferentInputIsRejected()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var key = $"phase05-{Guid.NewGuid():N}";
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/licenses")
        {
            Content = JsonContent.Create(RoadmapTestSupport.ValidIssueRequest($"first-{Guid.NewGuid():N}"))
        };
        firstRequest.Headers.Add("Idempotency-Key", key);
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/licenses")
        {
            Content = JsonContent.Create(RoadmapTestSupport.ValidIssueRequest($"second-{Guid.NewGuid():N}"))
        };
        secondRequest.Headers.Add("Idempotency-Key", key);
        using var second = await client.SendAsync(secondRequest);
        await RoadmapTestSupport.AssertProblemAsync(second, HttpStatusCode.Conflict);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "06")]
    public async Task IssuanceRequiresNormalizedCustomerEmailAndAuthoritativeContactMetadata()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var request = RoadmapTestSupport.ValidIssueRequest("email") with
        {
            CustomerEmail = "  Customer.Email+Tag@Example.COM  ",
            Metadata = new Dictionary<string, object?> { ["contactEmail"] = "attacker@example.net" }
        };
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");
        Assert.Equal("customer.email+tag@example.com", issued.Metadata.GetProperty("contactEmail").GetString());

        var missing = request with { CustomerEmail = "", Metadata = new Dictionary<string, object?>() };
        using var rejected = await client.PostAsJsonAsync("/api/v1/admin/licenses", missing);
        await RoadmapTestSupport.AssertProblemAsync(rejected, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "06")]
    public async Task CurrentCustomerEmailCanChangeWithoutRewritingTheSignedSnapshot()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/licenses",
            RoadmapTestSupport.ValidIssueRequest("snapshot") with { CustomerEmail = "first@example.com" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var license = await db.Licenses.Include(item => item.Customer)
            .SingleAsync(item => item.LicenseId == issued.LicenseId);
        var snapshot = license.MetadataJson;
        license.Customer.Email = "second@example.com";
        license.Customer.NormalizedEmail = "second@example.com";
        await db.SaveChangesAsync();

        Assert.Equal(snapshot, license.MetadataJson);
        Assert.Equal("first@example.com", JsonDocument.Parse(license.MetadataJson)
            .RootElement.GetProperty("contactEmail").GetString());
    }

    [Fact]
    [Trait("ExpectedGreenStage", "06")]
    public async Task LicenseSearchUsesNormalizedCustomerEmailCaseInsensitively()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/licenses",
            RoadmapTestSupport.ValidIssueRequest("email-search") with { CustomerEmail = "Search.Me@Example.COM" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var data = scope.ServiceProvider.GetRequiredService<AdminDataService>();
        var results = await data.SearchLicensesAsync("SEARCH.ME@EXAMPLE.COM", null, null, 1);
        Assert.Contains(results.Items, item => item.Customer == "Phase 0 Customer email-search");
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "business", "subscription")]
    [InlineData("11111111-1111-1111-1111-111111111111", "professional", "subscription")]
    [InlineData("11111111-1111-1111-1111-111111111111", "business", "trial-ish")]
    [Trait("ExpectedGreenStage", "07")]
    public async Task ForgedProductEditionAndLicenseTypeValuesAreRejected(
        string productId, string edition, string licenseType)
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var request = RoadmapTestSupport.ValidIssueRequest("controlled") with
        {
            ProductId = Guid.Parse(productId),
            Edition = edition,
            LicenseType = licenseType
        };
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", request);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "07")]
    public async Task PortalOrApiIssuanceCreatesExactlyOneEntitlement()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", RoadmapTestSupport.ValidIssueRequest("one-product"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");
        Assert.Single(issued.Entitlements);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.Entitlements.CountAsync(item => item.License.LicenseId == issued.LicenseId));
    }

    [Theory]
    [InlineData("subscription", null)]
    [InlineData("evaluation", "2000-01-01T00:00:00Z")]
    [Trait("ExpectedGreenStage", "02")]
    public async Task TimeLimitedIssuanceRequiresFutureExpiry(string licenseType, string? expiry)
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var parsed = expiry is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(expiry, CultureInfo.InvariantCulture);
        var request = RoadmapTestSupport.ValidIssueRequest("expiry-invalid") with
        {
            LicenseType = licenseType,
            ExpiresAt = parsed
        };
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", request);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task PerpetualIssuanceUsesCanonicalExpiry()
    {
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.issue");
        var request = RoadmapTestSupport.ValidIssueRequest("perpetual") with
        {
            LicenseType = "perpetual",
            ExpiresAt = null
        };
        using var response = await client.PostAsJsonAsync("/api/v1/admin/licenses", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<IssueLicenseResultContract>()
            ?? throw new InvalidOperationException("Issuance response was empty.");
        Assert.Equal(RoadmapTestSupport.PerpetualExpiry, issued.Entitlements.Single().ExpiresAt);
    }
}
