using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LicenseServer.Authorization;
using LicenseServer.Data;

namespace LicenseServer.Components.Account;

// This is a server-side AuthenticationStateProvider that revalidates the security stamp for the connected user
// every 30 minutes an interactive circuit is connected.
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // Get the user manager from a new scope to ensure it fetches fresh data
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await ValidateSecurityStampAsync(userManager, authenticationState.User))
            return false;

        var claimsFactory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        return await ValidatePermissionClaimsAsync(userManager, claimsFactory, authenticationState.User);
    }

    private async Task<bool> ValidateSecurityStampAsync(UserManager<ApplicationUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return false;
        }
        else if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }
        else
        {
            var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
            var userStamp = await userManager.GetSecurityStampAsync(user);
            return principalStamp == userStamp;
        }
    }

    // The security stamp only changes when a user's own password/roles are edited through the
    // admin UI. Role *permission* grants (BuiltInRoles.Matrix) can also change independently -
    // e.g. a deploy that adds a new permission to an existing role - without touching any user's
    // security stamp. Since the claims principal is otherwise fixed at sign-in, a session that
    // predates such a change keeps seeing the old (missing) permissions until it is forced to
    // re-authenticate. Comparing the session's cached "permission" claims against a freshly
    // generated set closes that gap: a mismatch forces sign-out, and the next sign-in rebuilds
    // the principal from current role/permission state.
    private static async Task<bool> ValidatePermissionClaimsAsync(
        UserManager<ApplicationUser> userManager,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
        ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null) return false;

        var cached = principal.FindAll(Permissions.ClaimType).Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        var current = await claimsFactory.CreateAsync(user);
        var fresh = current.FindAll(Permissions.ClaimType).Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        return cached.SetEquals(fresh);
    }
}

