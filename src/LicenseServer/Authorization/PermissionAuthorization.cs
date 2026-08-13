using System.Security.Claims;
using LicenseServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LicenseServer.Authorization;

internal sealed record PermissionRequirement(string Permission, bool RequireMfa) : IAuthorizationRequirement;

internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (!context.User.HasClaim(Permissions.ClaimType, requirement.Permission))
            return Task.CompletedTask;
        if (requirement.RequireMfa && !context.User.HasClaim("amr", "mfa"))
            return Task.CompletedTask;
        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

internal static class PermissionAuthorizationExtensions
{
    public static void AddPermissionAuthorization(
        this IServiceCollection services,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        var enforceMfa = configuration.GetValue(
            "Security:RequireMfaForHighRiskPermissions",
            !environment.IsDevelopment());
        var authorization = services.AddAuthorizationBuilder();
        foreach (var permission in Permissions.All)
        {
            authorization.AddPolicy(permission, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new PermissionRequirement(
                    permission,
                    enforceMfa && Permissions.HighRisk.Contains(permission)));
            });
        }
        authorization.AddPolicy("Administrator", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new PermissionRequirement(
                Permissions.UsersManage,
                enforceMfa));
        });
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<PermissionGuard>();
    }
}

internal sealed class PermissionGuard(
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorization)
{
    public async Task RequireAsync(string permission)
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null) return;
        if (!(await authorization.AuthorizeAsync(context.User, null, permission)).Succeeded)
            throw new UnauthorizedAccessException($"Permission '{permission}' is required.");
    }
}

internal sealed class MfaUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (await UserManager.GetTwoFactorEnabledAsync(user))
            identity.AddClaim(new Claim("amr", "mfa"));
        return identity;
    }
}
