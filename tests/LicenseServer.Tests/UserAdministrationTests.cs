using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class UserAdministrationTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "09")]
    public async Task UserRoutesSeparateReadFromManageAndReturnSafeProjections()
    {
        using var anonymous = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/users")).StatusCode);

        using var reader = fixture.CreateAuthenticatedClient(false, Permissions.UsersRead);
        using var list = await reader.GetAsync("/api/v1/admin/users");
        list.EnsureSuccessStatusCode();
        var json = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authenticator", json, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.GetAsync($"/api/v1/admin/authorization/{Permissions.UsersManage}")).StatusCode);
        using var forbidden = await reader.PostAsJsonAsync(
            "/api/v1/admin/users",
            new { email = "forbidden@example.com", accountType = "human", roles = new[] { BuiltInRoles.Auditor } });
        Assert.False(forbidden.IsSuccessStatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "09")]
    public async Task HumanInvitationHasNoAdministratorChosenPasswordAndTokenIsSingleUse()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"invited-{Guid.NewGuid():N}@example.com";

        var invitation = await service.InviteHumanAsync(
            new InviteUserRequest(email, [BuiltInRoles.Auditor]),
            "stage09-test",
            new Uri("https://localhost"));

        var user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException("Invitation did not create the user.");
        Assert.Null(user.PasswordHash);
        Assert.Equal(ApplicationUser.HumanAccountType, user.AccountType);
        Assert.NotNull(invitation.DevelopmentSetupUrl);

        var uri = new Uri(invitation.DevelopmentSetupUrl!);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var code = System.Text.Encoding.UTF8.GetString(
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(query["code"].Single()!));
        var first = await users.ResetPasswordAsync(user, code, "InvitedUser!7Kp9-Vx3-Rm8-Qz2");
        Assert.True(first.Succeeded);
        var replay = await users.ResetPasswordAsync(user, code, "ReplayShouldFail!7Kp9-Vx3-Rm8");
        Assert.False(replay.Succeeded);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "09")]
    public async Task ServiceAccountsCannotHoldInteractiveCredentials()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await service.CreateServiceAccountAsync(
            new CreateServiceAccountRequest($"automation-{Guid.NewGuid():N}@example.invalid", [BuiltInRoles.BillingAutomation]),
            "stage09-test");

        var stored = await users.FindByIdAsync(result.User.Id) ?? throw new InvalidOperationException();
        Assert.Equal(ApplicationUser.ServiceAccountType, stored.AccountType);
        Assert.Null(stored.PasswordHash);
        Assert.False(stored.TwoFactorEnabled);
        Assert.False(stored.EmailConfirmed);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "09")]
    public async Task ConcurrentFinalAdministratorMutationsLeaveOneEnabledAdministrator()
    {
        string firstId;
        string secondId;
        string seededId;
        await using (var setup = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = setup.ServiceProvider.GetRequiredService<UserAdministrationService>();
            var first = await service.CreateServiceAccountAsync(
                new CreateServiceAccountRequest($"admin-a-{Guid.NewGuid():N}@example.invalid", [BuiltInRoles.SystemAdministrator]),
                "stage09-test");
            var second = await service.CreateServiceAccountAsync(
                new CreateServiceAccountRequest($"admin-b-{Guid.NewGuid():N}@example.invalid", [BuiltInRoles.SystemAdministrator]),
                "stage09-test");
            firstId = first.User.Id;
            secondId = second.User.Id;
        }

        // Disable the seeded administrator so the two concurrent targets are the only enabled system administrators.
        await using (var setup = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = setup.ServiceProvider.GetRequiredService<UserAdministrationService>();
            var users = setup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var seeded = await users.FindByEmailAsync(DatabaseInitializer.DefaultEmail) ?? throw new InvalidOperationException();
            seededId = seeded.Id;
            await service.SetEnabledAsync(seeded.Id, false, "stage09-test");
        }

        async Task<bool> TryDisableAsync(string id)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<UserAdministrationService>()
                    .SetEnabledAsync(id, false, "stage09-concurrency");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryDisableAsync(firstId), TryDisableAsync(secondId));
        Assert.Single(results, value => value);

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var systemRole = await db.Roles.SingleAsync(role => role.Name == BuiltInRoles.SystemAdministrator);
        var enabledAdmins = await db.UserRoles
            .Where(link => link.RoleId == systemRole.Id)
            .Join(db.Users, link => link.UserId, user => user.Id, (_, user) => user)
            .CountAsync(user => user.IsEnabled);
        Assert.Equal(1, enabledAdmins);
        await verify.ServiceProvider.GetRequiredService<UserAdministrationService>()
            .SetEnabledAsync(seededId, true, "stage09-cleanup");
    }

    [Fact]
    [Trait("ExpectedGreenStage", "09")]
    public async Task DisablingUserChangesSecurityStampAndWritesRedactedAudit()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var created = await service.CreateServiceAccountAsync(
            new CreateServiceAccountRequest($"disable-{Guid.NewGuid():N}@example.invalid", [BuiltInRoles.Auditor]),
            "stage09-test");
        var before = (await users.FindByIdAsync(created.User.Id))!.SecurityStamp;

        await service.SetEnabledAsync(created.User.Id, false, "stage09-test");
        var after = (await users.FindByIdAsync(created.User.Id))!.SecurityStamp;
        Assert.NotEqual(before, after);

        var audit = await db.AuditRecords.Where(item => item.TargetId == created.User.Id).ToListAsync();
        var serialized = JsonSerializer.Serialize(audit);
        Assert.Contains(audit, item => item.Action == "user.disabled");
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
