using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftwareLicensing;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Phase0Roadmap")]
public sealed class RoadmapLifecycleContractTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task NeverActivatedLicenseCanBeCancelledAndCannotActivateAfterward()
    {
        var available = await RoadmapTestSupport.AddLicenseAsync(fixture, "cancel-available");
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.cancel");

        using var cancelled = await RoadmapTestSupport.PostAdminAsync(client,
            $"/api/v1/admin/licenses/{available.LicenseId}/cancel",
            new { confirmed = true, reason = "Order entered in error", version = available.Version });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        using var activationAfterCancellation = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{available.LicenseId}/activate",
            ActivationRequest($"PHASE0-cancel-available-ACTIVATION-CODE", "offline"));
        Assert.Equal(HttpStatusCode.Forbidden, activationAfterCancellation.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task LicenseWithActivationHistoryCannotBeCancelledAndRequiresRevocation()
    {
        var used = await RoadmapTestSupport.AddLicenseAsync(fixture, "cancel-used", withActivationHistory: true);
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.cancel");
        using var rejected = await RoadmapTestSupport.PostAdminAsync(client,
            $"/api/v1/admin/licenses/{used.LicenseId}/cancel",
            new { confirmed = true, reason = "Should be revoked instead", version = used.Version });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("revoke", await rejected.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task AuthorizedExpiryAmendmentMayMoveIntoPastAndImmediatelyInvalidatesOnlineChecks()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "past-expiry", DateTimeOffset.UtcNow.AddDays(10));
        var activationCode = "PHASE0-past-expiry-ACTIVATION-CODE";
        using var device = fixture.Factory.CreateClient();
        var request = ActivationRequest(activationCode, "online");
        using var activated = await device.PostAsJsonAsync($"/api/v1/licenses/{license.LicenseId}/activate", request);
        activated.EnsureSuccessStatusCode();
        var active = await activated.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();

        using var admin = fixture.CreateAuthenticatedClient(true, "licenses.update");
        using var amended = await RoadmapTestSupport.PostAdminAsync(admin,
            $"/api/v1/admin/licenses/{license.LicenseId}/terms",
            new { expiresAt = DateTimeOffset.UtcNow.AddDays(-1), reason = "End contractual access", version = license.Version });
        Assert.Equal(HttpStatusCode.OK, amended.StatusCode);

        using var validation = await device.PostAsJsonAsync($"/api/v1/activations/{active.ActivationId}/validate",
            new ActivationCredentialRequest(request.ActivationToken, request.Device!.DeviceId));
        Assert.Equal(HttpStatusCode.Forbidden, validation.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Contains(await db.AuditRecords.Where(item => item.TargetId == license.LicenseId).ToListAsync(),
            item => item.Action == "license.terms-amended"
                && item.ContextJson.Contains("expiresAt", StringComparison.Ordinal)
                && item.ContextJson.Contains("reason", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task SignedEntitlementExpiryExactlyMatchesServerExpiry()
    {
        var expiry = DateTimeOffset.UtcNow.AddMonths(6).ToUniversalTime();
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "signed-expiry", expiry);
        var activationCode = "PHASE0-signed-expiry-ACTIVATION-CODE";
        using var client = fixture.Factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/v1/licenses/{license.LicenseId}/activate",
            ActivationRequest(activationCode, "offline"));
        response.EnsureSuccessStatusCode();
        var issued = await response.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        var verified = LicenseVerifier.Verify(issued.SignedLicense);
        Assert.Equal(expiry, verified.Data.Entitlements.Single().ExpiresAt);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "03")]
    public async Task RevocationRequiresPostedConfirmation()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "confirm-post");
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.revoke");

        using var revoke = await RoadmapTestSupport.PostAdminAsync(client,
            $"/api/v1/admin/licenses/{license.LicenseId}/revoke",
            new { confirmed = false, reason = "Required test reason", version = license.Version });
        await RoadmapTestSupport.AssertProblemAsync(revoke, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "03")]
    public async Task OperatorDeactivationRequiresPostedConfirmation()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "deactivate-confirm-post");
        using var client = fixture.CreateAuthenticatedClient(true, "activations.manage");
        using var deactivate = await RoadmapTestSupport.PostAdminAsync(client,
            "/api/v1/admin/activations/not-active/deactivate",
            new { confirmed = false, reason = "Required test reason", version = license.Version });
        await RoadmapTestSupport.AssertProblemAsync(deactivate, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "03")]
    public async Task LifecyclePageHasPostableConfirmationAndExplainsOfflineLimitation()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "lifecycle-page");
        using var client = fixture.CreateAuthenticatedClient(true, "licenses.read", "licenses.revoke");
        var html = await client.GetStringAsync($"/licenses/{Uri.EscapeDataString(license.LicenseId)}");
        Assert.Contains("Offline files cannot be recalled", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled>Confirm revocation", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "08")]
    public async Task LifecycleMutationsAreDeniedWithoutTheirSpecificPermission()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "authorization");
        using var anonymous = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        using var anonymousResponse = await anonymous.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{license.LicenseId}/revoke", new { confirmed = true, reason = "No principal" });
        Assert.True(anonymousResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect);

        using var reader = fixture.CreateAuthenticatedClient(false, "licenses.read");
        using var readerResponse = await reader.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{license.LicenseId}/revoke", new { confirmed = true, reason = "Wrong permission" });
        Assert.Equal(HttpStatusCode.Forbidden, readerResponse.StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "02")]
    public async Task OnlineInvalidationIsImmediateWhileIssuedOfflineArtifactRemainsCryptographicallyValid()
    {
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, "boundary");
        var activationCode = "PHASE0-boundary-ACTIVATION-CODE";
        var request = ActivationRequest(activationCode, "offline");
        using var client = fixture.Factory.CreateClient();
        using var activated = await client.PostAsJsonAsync($"/api/v1/licenses/{license.LicenseId}/activate", request);
        activated.EnsureSuccessStatusCode();
        var issued = await activated.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.NotNull(LicenseVerifier.Verify(issued.SignedLicense));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
            Assert.True((await store.RevokeAsync(license.LicenseId, "Boundary test", "phase0-test", DateTimeOffset.UtcNow)).Success);
        }

        using var validation = await client.PostAsJsonAsync($"/api/v1/activations/{issued.ActivationId}/validate",
            new ActivationCredentialRequest(request.ActivationToken, request.Device!.DeviceId));
        Assert.Equal(HttpStatusCode.Forbidden, validation.StatusCode);
        Assert.NotNull(LicenseVerifier.Verify(issued.SignedLicense));
    }

    private static ActivateRequest ActivationRequest(string activationCode, string mode) => new(
        Guid.NewGuid().ToString("D"),
        activationCode,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        mode,
        new DeviceRequest(DeviceIdentity.Scheme, new string('C', 64), "PHASE0-DEVICE"));
}
