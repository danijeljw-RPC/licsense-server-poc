using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LicenseServer.Authorization;

internal static class ApiKeyAuthenticationDefaults
{
    public const string Scheme = "LicenseServer.ApiKey";
    public const string CompositeScheme = "LicenseServer.Composite";
}

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiCredentialService credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Bearer authentication failed.");
        var authenticated = await credentials.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (authenticated is null) return AuthenticateResult.Fail("Bearer authentication failed.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authenticated.OwnerUserId),
            new(ClaimTypes.Name, authenticated.OwnerName),
            new("api_credential_id", authenticated.Id.ToString("D")),
            new("amr", "api_key")
        };
        claims.AddRange(authenticated.Scopes.Select(scope => new Claim(Permissions.ClaimType, scope)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
