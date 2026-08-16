using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftwareLicensing;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class LicenseImportTests(PostgresWebFixture fixture)
{
    [Fact]
    public async Task ImportHappyPathStoresVerbatimBytesAndAuditsIt()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"import-{Guid.NewGuid():N}@example.com";
        var (bytes, envelope) = await SignAsync(BuildLicense(licenseId, "Imported Customer", contactEmail: contactEmail));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, bytes, "widget.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(licenseId, body.GetProperty("licenseId").GetString());

        var stored = await ReadStoredLicenseAsync(licenseId);
        Assert.Equal(LicenseProvenance.Imported, stored.Provenance);
        Assert.NotNull(stored.ImportedAt);
        Assert.Equal("phase0-test-operator", stored.ImportedBy);
        // Byte-for-byte, not a jsonb round trip: proves storage did not re-normalize whitespace,
        // key order, or formatting - the entire point of #23's bytea-over-jsonb resolution.
        Assert.Equal(bytes, stored.ImportedSignedEnvelope);
        Assert.Equal(SHA256.HashData(bytes), stored.ImportedSignedEnvelopeSha256);
        Assert.Equal(envelope["keyId"]!.GetValue<string>(), (await ReadImportAuditAsync(licenseId)).GetProperty("keyId").GetString());
    }

    [Fact]
    public async Task MultiProductImportCreatesOneEntitlementPerProductWithoutViolatingUniqueness()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"multi-{Guid.NewGuid():N}@example.com";
        var license = BuildLicense(licenseId, "Multi Product Customer", contactEmail: contactEmail);
        var entitlements = (JsonArray)license["entitlements"]!;
        entitlements.Add(new JsonObject
        {
            ["product"] = $"second-product-{Guid.NewGuid():N}"[..20],
            ["edition"] = "business",
            ["licenseType"] = "perpetual",
            ["seats"] = 5
        });
        var (bytes, _) = await SignAsync(license);
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, bytes, "multi.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var record = await db.Licenses.Include(x => x.Entitlements).AsNoTracking().SingleAsync(x => x.LicenseId == licenseId);
        Assert.Equal(2, record.Entitlements.Count);
        // No universal expiry supplied for either product, so the aggregate index column is null
        // (never-expires for search/listing) rather than misreporting the license as expired.
        Assert.Null(record.ExpiresAt);
    }

    [Fact]
    public async Task ImportAutoCreatesInactiveProductDefinitionForUnknownProduct()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"unknown-product-{Guid.NewGuid():N}@example.com";
        var productCode = $"unknown-{Guid.NewGuid():N}"[..24];
        var (bytes, _) = await SignAsync(BuildLicense(licenseId, "Unknown Product Customer", product: productCode, contactEmail: contactEmail));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, bytes, "unknown-product.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await db.ProductDefinitions.AsNoTracking().SingleAsync(x => x.Code == productCode);
        Assert.False(product.IsActive);
        var audit = await db.AuditRecords.AsNoTracking()
            .SingleAsync(x => x.Action == "product.auto-created" && x.TargetId == product.Id.ToString("D"));
        Assert.Equal("success", audit.Result);
    }

    [Fact]
    public async Task ImportRequiresContactEmail()
    {
        var licenseId = UniqueLicenseId();
        var (bytes, _) = await SignAsync(BuildLicense(licenseId, "No Email Customer"));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, bytes, "no-email.license", contactEmail: null);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, await CountLicensesAsync(licenseId));
    }

    [Fact]
    public async Task ImportRejectsContactEmailMismatchWithArtifactMetadata()
    {
        var licenseId = UniqueLicenseId();
        var artifactEmail = $"artifact-{Guid.NewGuid():N}@example.com";
        var operatorEmail = $"operator-{Guid.NewGuid():N}@example.com";
        var (bytes, _) = await SignAsync(BuildLicense(licenseId, "Mismatch Customer", contactEmail: artifactEmail));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, bytes, "mismatch.license", operatorEmail);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, await CountLicensesAsync(licenseId));
    }

    [Fact]
    public async Task ImportRejectsWhenArtifactContactEmailIsNotAString()
    {
        // LicenseSchema.Parse allows any metadata value to be a string, number, or boolean, so a
        // non-string metadata.contactEmail is a legitimately-signable artifact. It must still be
        // treated as "present" and rejected on mismatch, not silently skipped as if absent.
        var licenseId = UniqueLicenseId();
        var operatorEmail = $"non-string-{Guid.NewGuid():N}@example.com";
        var license = BuildLicense(licenseId, "Non-String Metadata Customer");
        license["metadata"] = new JsonObject { ["contactEmail"] = 12345 };
        var (bytes, _) = await SignAsync(license);
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, bytes, "non-string-email.license", operatorEmail);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, await CountLicensesAsync(licenseId));
    }

    [Fact]
    public async Task ImportedActivationFailsClosedForRefreshAndDeactivate()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"activation-{Guid.NewGuid():N}@example.com";
        var deviceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(licenseId)));
        var activationId = Guid.NewGuid().ToString("D");
        var license = BuildLicense(licenseId, "Activated Customer", contactEmail: contactEmail);
        license["deviceBinding"] = new JsonObject
        {
            ["scheme"] = DeviceIdentity.Scheme,
            ["deviceId"] = deviceId
        };
        license["activation"] = new JsonObject
        {
            ["activationId"] = activationId,
            ["mode"] = "offline",
            ["activatedAt"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O")
        };
        var (bytes, _) = await SignAsync(license);
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);
        using var importResponse = await PostImportAsync(client, bytes, "activated.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, importResponse.StatusCode);

        // No server-issued token could ever exist for a pre-activated offline import, so any
        // guessed credential - including an empty one - must fail both device-facing lifecycle
        // actions, by construction rather than a nullable/sentinel branch. See #22's resolution.
        var guessedCredential = new ActivationCredentialRequest("guessed-activation-token", deviceId);
        using var anonymous = fixture.Factory.CreateClient();
        using var refresh = await anonymous.PostAsJsonAsync($"/api/v1/activations/{activationId}/refresh", guessedCredential);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var deactivate = await anonymous.PostAsJsonAsync($"/api/v1/activations/{activationId}/deactivate", guessedCredential);
        Assert.Equal(HttpStatusCode.Unauthorized, deactivate.StatusCode);
    }

    [Fact]
    public async Task ImportRejectsATamperedSignature()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"tampered-{Guid.NewGuid():N}@example.com";
        var (_, envelope) = await SignAsync(BuildLicense(licenseId, "Tampered Customer", contactEmail: contactEmail));
        // Mutate signed content after signing, leaving keyId/signature as-is, so this specifically
        // exercises signature-verification failure rather than an unknown-key rejection.
        envelope["license"]!["customer"] = "Someone Else Entirely";
        var tampered = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, tampered, "tampered.license", contactEmail);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, await CountLicensesAsync(licenseId));
    }

    [Fact]
    public async Task ImportRejectsAnUnknownSigningKey()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"unknown-key-{Guid.NewGuid():N}@example.com";
        var (_, envelope) = await SignAsync(BuildLicense(licenseId, "Unknown Key Customer", contactEmail: contactEmail));
        envelope["keyId"] = "not-a-registered-key";
        var tampered = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var response = await PostImportAsync(client, tampered, "unknown-key.license", contactEmail);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, await CountLicensesAsync(licenseId));
    }

    [Fact]
    public async Task ImportRejectsTheSameArtifactUploadedTwice()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"duplicate-{Guid.NewGuid():N}@example.com";
        var (bytes, _) = await SignAsync(BuildLicense(licenseId, "Duplicate Customer", contactEmail: contactEmail));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);

        using var first = await PostImportAsync(client, bytes, "duplicate.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var second = await PostImportAsync(client, bytes, "duplicate.license", contactEmail);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountLicensesAsync(licenseId));
    }

    [Fact]
    public async Task ImportRejectsAnOversizedFile()
    {
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);
        var oversized = new byte[LicenseImportService.MaxUploadBytes + 1];
        using var response = await PostImportAsync(client, oversized, "oversized.license", "oversized@example.com");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ImportDoesNotRejectAFileExactlyAtTheSizeLimitForMultipartFramingOverhead()
    {
        // The whole-request pre-check (Program.cs, before the form is even parsed) has to bound
        // against request.ContentLength, which includes multipart boundaries, per-part headers,
        // and the contactEmail field on top of the file itself - not just the artifact limit used
        // by the precise post-parse file.Length check below it. A file sitting exactly at the
        // documented limit must not be rejected purely because of that framing overhead.
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);
        var atLimit = new byte[LicenseImportService.MaxUploadBytes];
        using var response = await PostImportAsync(client, atLimit, "at-limit.license", "at-limit@example.com");
        // Not a valid signed envelope, so it still fails - just not on size.
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await RoadmapTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportRequiresTheLicensesImportPermission()
    {
        var (bytes, _) = await SignAsync(BuildLicense(UniqueLicenseId(), "Forbidden Customer", contactEmail: "forbidden@example.com"));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesRead);
        using var response = await PostImportAsync(client, bytes, "forbidden.license", "forbidden@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ImportPageRequiresTheLicensesImportPermission()
    {
        using var permitted = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);
        Assert.Equal(HttpStatusCode.OK, (await permitted.GetAsync("/licenses/import")).StatusCode);

        using var forbidden = fixture.CreateAuthenticatedClient(false, Permissions.LicensesRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await forbidden.GetAsync("/licenses/import")).StatusCode);
    }

    [Fact]
    public async Task ImportedLicenseSurfacesProvenanceInAdminListAndDetailViews()
    {
        var licenseId = UniqueLicenseId();
        var contactEmail = $"provenance-{Guid.NewGuid():N}@example.com";
        var (bytes, _) = await SignAsync(BuildLicense(licenseId, "Provenance Customer", contactEmail: contactEmail));
        using var client = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);
        using var response = await PostImportAsync(client, bytes, "provenance.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var data = scope.ServiceProvider.GetRequiredService<AdminDataService>();

        var detail = await data.GetLicenseAsync(licenseId);
        Assert.NotNull(detail);
        Assert.Equal(LicenseProvenance.Imported, detail!.Provenance);
        Assert.NotNull(detail.ImportedAt);

        var results = await data.SearchLicensesAsync(licenseId, null, null, 1);
        var row = Assert.Single(results.Items);
        Assert.Equal(LicenseProvenance.Imported, row.Provenance);
    }

    [Fact]
    public async Task ImportedSingleProductLicenseRejectsTermsAmendmentToPreventDriftFromTheSignedArtifact()
    {
        // A single-product import passes the "exactly one entitlement" check that otherwise gates
        // term amendments, so without a provenance check this would silently mutate the relational
        // index (ExpiresAt/Seats/Edition) while ImportedSignedEnvelope - the declared source of
        // truth for an imported license - stays byte-for-byte unchanged.
        var licenseId = UniqueLicenseId();
        var contactEmail = $"amend-{Guid.NewGuid():N}@example.com";
        var (bytes, _) = await SignAsync(BuildLicense(licenseId, "Amend Customer", contactEmail: contactEmail));
        using var importer = fixture.CreateAuthenticatedClient(false, Permissions.LicensesImport);
        using var importResponse = await PostImportAsync(importer, bytes, "amend.license", contactEmail);
        Assert.Equal(HttpStatusCode.Created, importResponse.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Licenses.AsNoTracking().SingleAsync(x => x.LicenseId == licenseId);
        var originalSeats = (await db.Entitlements.AsNoTracking().SingleAsync(x => x.LicenseRecordId == stored.Id)).Seats;

        using var updater = fixture.CreateAuthenticatedClient(false, Permissions.LicensesUpdate);
        using var amendResponse = await RoadmapTestSupport.PostAdminAsync(updater,
            $"/api/v1/admin/licenses/{licenseId}/terms",
            new { seats = originalSeats + 1, reason = "Should be rejected for an imported license", version = stored.Version });
        await RoadmapTestSupport.AssertProblemAsync(amendResponse, HttpStatusCode.Conflict);

        var unchanged = await db.Entitlements.AsNoTracking().SingleAsync(x => x.LicenseRecordId == stored.Id);
        Assert.Equal(originalSeats, unchanged.Seats);
    }

    private static string UniqueLicenseId() => $"IMP-{Guid.NewGuid():N}"[..19];

    private static JsonObject BuildLicense(
        string licenseId, string customerName, string product = "gcexp", string? contactEmail = null)
    {
        var license = new JsonObject
        {
            ["licenseId"] = licenseId,
            ["customer"] = customerName,
            ["issuedAt"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O"),
            ["entitlements"] = new JsonArray(new JsonObject
            {
                ["product"] = product,
                ["edition"] = "business",
                ["licenseType"] = "perpetual",
                ["seats"] = 3
            })
        };
        if (contactEmail is not null)
            license["metadata"] = new JsonObject { ["contactEmail"] = contactEmail };
        return license;
    }

    private async Task<(byte[] Bytes, JsonObject Envelope)> SignAsync(JsonObject license)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();
        var result = signer.Sign(license, null);
        Assert.True(result.Success, result.ErrorMessage);
        var json = result.Envelope!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return (Encoding.UTF8.GetBytes(json), result.Envelope);
    }

    private static async Task<HttpResponseMessage> PostImportAsync(
        HttpClient client, byte[] fileBytes, string fileName, string? contactEmail)
    {
        var token = await RoadmapTestSupport.AntiforgeryTokenAsync(client);
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        if (contactEmail is not null)
            content.Add(new StringContent(contactEmail), "contactEmail");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/licenses/import") { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private async Task<int> CountLicensesAsync(string licenseId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Licenses.CountAsync(x => x.LicenseId == licenseId);
    }

    private async Task<LicenseRecord> ReadStoredLicenseAsync(string licenseId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Licenses.AsNoTracking().SingleAsync(x => x.LicenseId == licenseId);
    }

    private async Task<JsonElement> ReadImportAuditAsync(string licenseId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await db.AuditRecords.AsNoTracking()
            .SingleAsync(x => x.Action == "license.imported" && x.TargetId == licenseId);
        return JsonDocument.Parse(audit.ContextJson).RootElement;
    }
}
