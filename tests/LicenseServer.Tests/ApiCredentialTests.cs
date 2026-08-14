using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed partial class ApiCredentialTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "10")]
    public async Task CreatedCredentialHasExpectedEntropyFormatAndHashOnlyPersistence()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByEmailAsync(DatabaseInitializer.DefaultEmail) ?? throw new InvalidOperationException();
        var service = scope.ServiceProvider.GetRequiredService<ApiCredentialService>();

        var created = await service.CreateAsync(new CreateApiCredentialRequest(
            "stage10 format", owner.Id, [Permissions.LicensesRead], DateTimeOffset.UtcNow.AddHours(1)), "stage10-test");

        Assert.Matches(ApiKeyPattern(), created.Secret);
        var secretPart = created.Secret[("lic_live_".Length + 16 + 1)..];
        Assert.True(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(secretPart).Length >= 32);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.ApiCredentials.SingleAsync(item => item.Id == created.Credential.Id);
        var json = JsonSerializer.Serialize(stored);
        Assert.Equal(ApiCredentialHasher.CurrentVersion, stored.HashVersion);
        Assert.Equal(32, stored.SecretHash.Length);
        Assert.DoesNotContain(created.Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPart, json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "10")]
    public async Task BearerAuthenticationUsesExistingPermissionPoliciesWithCorrect401And403()
    {
        var created = await CreateSystemAdministratorKeyAsync([Permissions.LicensesRead]);
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Secret);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRead}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/v1/admin/authorization/{Permissions.UsersManage}")).StatusCode);

        using var missing = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await missing.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRead}")).StatusCode);
        missing.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "lic_live_unknown_invalid");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await missing.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRead}")).StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "10")]
    public async Task RotationAndRevocationAreImmediateAndSecretsAreOneTimeViews()
    {
        var created = await CreateSystemAdministratorKeyAsync([Permissions.LicensesRead]);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ApiCredentialService>();
        var listed = await service.ListAsync(created.Credential.OwnerUserId);
        Assert.Contains(listed, item => item.Id == created.Credential.Id);
        Assert.DoesNotContain(created.Secret, JsonSerializer.Serialize(listed), StringComparison.Ordinal);

        var rotated = await service.RotateAsync(created.Credential.Id, "stage10-test");
        Assert.NotEqual(created.Secret, rotated.Secret);
        await AssertAuthenticationAsync(created.Secret, HttpStatusCode.Unauthorized);
        await AssertAuthenticationAsync(rotated.Secret, HttpStatusCode.NoContent);

        await service.RevokeAsync(rotated.Credential.Id, "stage10-test");
        await AssertAuthenticationAsync(rotated.Secret, HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "10")]
    public async Task HumanOwnedKeysRequireExpiryAndOwnerDisableRevokesAllCredentials()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var userAdmin = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
        var invited = await userAdmin.InviteHumanAsync(
            new InviteUserRequest($"key-owner-{Guid.NewGuid():N}@example.com", [BuiltInRoles.Auditor]),
            "stage10-test",
            new Uri("https://localhost"));
        var ownerId = invited.User.Id;
        var service = scope.ServiceProvider.GetRequiredService<ApiCredentialService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new CreateApiCredentialRequest("no expiry", ownerId, [Permissions.LicensesRead], null), "stage10-test"));

        var created = await service.CreateAsync(new CreateApiCredentialRequest(
            "disable owner", ownerId, [Permissions.LicensesRead], DateTimeOffset.UtcNow.AddHours(1)), "stage10-test");
        await userAdmin.SetEnabledAsync(ownerId, false, "stage10-test");
        await AssertAuthenticationAsync(created.Secret, HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "10")]
    public async Task BearerMutationDoesNotRequireCookieAntiforgeryAndAuditLogsRemainRedacted()
    {
        var created = await CreateSystemAdministratorKeyAsync([Permissions.ProductsManage]);
        fixture.CapturedLogs.Clear();
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Secret);
        using var response = await client.PostAsJsonAsync("/api/v1/admin/products", new
        {
            code = $"api-{Guid.NewGuid():N}"[..20],
            displayName = "Bearer-created product",
            description = "stage10"
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuditRecords
            .Where(item => item.Action == "product.created" && item.Actor == DatabaseInitializer.DefaultEmail)
            .ToListAsync();
        var captured = string.Join('\n', fixture.CapturedLogs.Messages) + JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(created.Secret, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Secret[("lic_live_".Length + 16 + 1)..], captured, StringComparison.Ordinal);
    }

    private async Task<CreatedApiCredential> CreateSystemAdministratorKeyAsync(IReadOnlyList<string> scopes)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByEmailAsync(DatabaseInitializer.DefaultEmail) ?? throw new InvalidOperationException();
        return await scope.ServiceProvider.GetRequiredService<ApiCredentialService>().CreateAsync(
            new CreateApiCredentialRequest($"stage10-{Guid.NewGuid():N}", owner.Id, scopes, DateTimeOffset.UtcNow.AddHours(1)),
            DatabaseInitializer.DefaultEmail);
    }

    private async Task AssertAuthenticationAsync(string secret, HttpStatusCode status)
    {
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var response = await client.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRead}");
        Assert.Equal(status, response.StatusCode);
    }

    [GeneratedRegex("^lic_live_[A-Za-z0-9_-]{16}_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();
}
