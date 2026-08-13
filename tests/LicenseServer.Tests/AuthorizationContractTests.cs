using System.Net;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Phase0Roadmap")]
public sealed class AuthorizationContractTests(PostgresWebFixture fixture)
{
    private static readonly string[] ExpectedPermissions =
    [
        "licenses.read", "licenses.issue", "licenses.update", "licenses.cancel", "licenses.revoke",
        "activations.manage", "customers.read", "customers.manage", "products.read", "products.manage",
        "users.read", "users.manage", "apiKeys.manageSelf", "apiKeys.manageAll", "audit.read", "billing.manage"
    ];
    private static readonly string[] ExpectedRoles =
    [
        "System Administrator", "License Manager", "License Issuer", "Support Agent",
        "Product Administrator", "Auditor", "Billing Automation"
    ];

    [Fact]
    [Trait("ExpectedGreenStage", "08")]
    public void RoadmapPermissionsAndBuiltInRolesAreComplete()
    {
        Assert.Equal(ExpectedPermissions, Permissions.All);
        Assert.Equal(ExpectedRoles, BuiltInRoles.All);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "08")]
    public async Task EveryBuiltInRoleReceivesExactlyItsAllowedPermissionMatrixOverHttp()
    {
        foreach (var role in BuiltInRoles.All)
        {
            using var client = fixture.CreateRoleClient(role, mfa: true);
            foreach (var permission in Permissions.All)
            {
                using var response = await client.GetAsync($"/api/v1/admin/authorization/{Uri.EscapeDataString(permission)}");
                var expected = BuiltInRoles.PermissionsFor(role).Contains(permission, StringComparer.Ordinal)
                    ? HttpStatusCode.NoContent
                    : HttpStatusCode.Forbidden;
                Assert.Equal(expected, response.StatusCode);
            }
        }
    }

    [Fact]
    [Trait("ExpectedGreenStage", "08")]
    public async Task LegacyAdministratorIsMappedWithoutLockoutAndClaimsSeedIdempotently()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var admin = await users.FindByEmailAsync(DatabaseInitializer.DefaultEmail)
            ?? throw new InvalidOperationException("Default administrator was not seeded.");
        Assert.True(await users.IsInRoleAsync(admin, BuiltInRoles.LegacyAdministrator));
        Assert.True(await users.IsInRoleAsync(admin, BuiltInRoles.SystemAdministrator));

        foreach (var roleName in BuiltInRoles.All)
        {
            var role = await roles.FindByNameAsync(roleName) ?? throw new InvalidOperationException($"Role {roleName} was not seeded.");
            var claims = await roles.GetClaimsAsync(role);
            Assert.Equal(
                BuiltInRoles.PermissionsFor(roleName).Order(StringComparer.Ordinal),
                claims.Where(item => item.Type == Permissions.ClaimType).Select(item => item.Value).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    [Trait("ExpectedGreenStage", "08")]
    public async Task PagesAndEndpointsUseActionLevelPermissions()
    {
        using var issuer = fixture.CreateRoleClient(BuiltInRoles.LicenseIssuer, mfa: true);
        Assert.Equal(HttpStatusCode.OK, (await issuer.GetAsync("/licenses")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await issuer.GetAsync("/licenses/create")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await issuer.GetAsync("/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await issuer.GetAsync("/products")).StatusCode);

        using var productAdmin = fixture.CreateRoleClient(BuiltInRoles.ProductAdministrator, mfa: true);
        Assert.Equal(HttpStatusCode.OK, (await productAdmin.GetAsync("/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await productAdmin.GetAsync("/licenses")).StatusCode);

        using var anonymous = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRead}")).StatusCode);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "08")]
    public async Task HighRiskPermissionRequiresMfaWhenEnforcementIsEnabled()
    {
        using var withoutMfa = fixture.CreateRoleClient(BuiltInRoles.SystemAdministrator, mfa: false);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await withoutMfa.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRevoke}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await withoutMfa.GetAsync($"/api/v1/admin/authorization/{Permissions.UsersManage}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await withoutMfa.GetAsync($"/api/v1/admin/authorization/{Permissions.ApiKeysManageAll}")).StatusCode);

        using var withMfa = fixture.CreateRoleClient(BuiltInRoles.SystemAdministrator, mfa: true);
        Assert.Equal(HttpStatusCode.NoContent,
            (await withMfa.GetAsync($"/api/v1/admin/authorization/{Permissions.LicensesRevoke}")).StatusCode);
    }
}
