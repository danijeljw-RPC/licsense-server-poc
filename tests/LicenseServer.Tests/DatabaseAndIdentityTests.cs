using System.Net;
using System.Text.RegularExpressions;
using LicenseServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class DatabaseAndIdentityTests(PostgresWebFixture fixture)
{
    [Fact]
    public async Task CleanMigrationAndDefaultAdminSeedAreIdempotent()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.True(await db.Database.CanConnectAsync());
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(DatabaseInitializer.DefaultEmail);
        Assert.NotNull(admin);
        Assert.True(admin!.MustChangePassword);
        Assert.True(await users.IsInRoleAsync(admin, DatabaseInitializer.AdministratorRole));
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var administrator = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, DatabaseInitializer.AdministratorRole)], "test"));
        var regularUser = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        Assert.True((await authorization.AuthorizeAsync(administrator, null, "Administrator")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(regularUser, null, "Administrator")).Succeeded);
        Assert.Equal(1, await db.Users.CountAsync(x => x.NormalizedEmail == admin.NormalizedEmail));
        Assert.Equal(1, await db.Licenses.CountAsync(x => x.LicenseId == "LIC-POC-0001"));
        Assert.True(await TableExistsAsync(db, "AspNetUserPasskeys"));
    }

    [Fact]
    public async Task AnonymousAdminAccessIsRejectedAndLoginForcesPasswordChange()
    {
        var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var denied = await client.GetAsync("/licenses");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/Account/Login", denied.Headers.Location?.AbsolutePath);

        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = AntiforgeryToken(loginPage);
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "login",
            ["Input.Email"] = DatabaseInitializer.DefaultEmail,
            ["Input.Password"] = DatabaseInitializer.DefaultPassword,
            ["Input.RememberMe"] = "false"
        }));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Login returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Contains("ChangePassword", response.Headers.Location?.OriginalString);

        var blocked = await client.GetAsync("/licenses");
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Contains("ChangePassword", blocked.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task IdentityAuthenticatorRecoveryAndPasskeyServicesAreAvailable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "mfa-test@localhost", Email = "mfa-test@localhost", EmailConfirmed = true };
        var created = await users.CreateAsync(user, "MfaTestPassword!9x-R4q");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(x => x.Code)));
        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user) ?? throw new InvalidOperationException();
        var code = CurrentTotp(key);
        Assert.True(await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code));
        Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        var recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        Assert.Equal(10, recovery.Length);
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, recovery[0])).Succeeded);

        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        signIn.Context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        signIn.Context.Request.Scheme = "https";
        signIn.Context.Request.Host = new HostString("localhost");
        var optionsJson = await signIn.MakePasskeyCreationOptionsAsync(new()
        {
            Id = user.Id,
            Name = user.Email!,
            DisplayName = user.Email!
        });
        Assert.Contains("\"rp\"", optionsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"challenge\"", optionsJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token was not rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<bool> TableExistsAsync(ApplicationDbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        var parameter = command.CreateParameter(); parameter.ParameterName = "name"; parameter.Value = $"\"{table}\""; command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static string CurrentTotp(string base32Secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Secret.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counterBytes = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
#pragma warning disable CA5350 // ASP.NET Core Identity authenticator tokens use the RFC 6238 HMAC-SHA1 profile.
        using var hmac = new HMACSHA1(key);
#pragma warning restore CA5350
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16) | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
