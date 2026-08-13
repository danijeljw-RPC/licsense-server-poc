using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Data;

public sealed partial class DatabaseInitializer(
    ApplicationDbContext db,
    RoleManager<IdentityRole> roles,
    UserManager<ApplicationUser> users,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<DatabaseInitializer> logger)
{
    public const string AdministratorRole = "Administrator";
    public const string DefaultEmail = "admin@localhost.com";
    public const string DefaultPassword = "LocalAdmin!7Kp9-Vx3-Rm8-Qz2";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(7349210862)", cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
            await SeedRoleAndAdminAsync();
            await SeedDemoDataAsync(cancellationToken);
        }
        finally
        {
            try { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(7349210862)", cancellationToken); }
            finally { await db.Database.CloseConnectionAsync(); }
        }
    }

    private async Task SeedRoleAndAdminAsync()
    {
        if (!await roles.RoleExistsAsync(AdministratorRole))
        {
            var roleResult = await roles.CreateAsync(new IdentityRole(AdministratorRole));
            EnsureSucceeded(roleResult, "create the Administrator role");
        }

        if (!GetBoolean("SEED_DEFAULT_ADMIN", environment.IsDevelopment()))
        {
            LogAdminSeedDisabled(logger);
            return;
        }

        var email = configuration["DEFAULT_ADMIN_EMAIL"] ?? DefaultEmail;
        var password = configuration["DEFAULT_ADMIN_PASSWORD"] ?? DefaultPassword;
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                MustChangePassword = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            EnsureSucceeded(await users.CreateAsync(user, password), "create the development administrator");
            LogAdminSeeded(logger, email);
        }

        if (!await users.IsInRoleAsync(user, AdministratorRole))
            EnsureSucceeded(await users.AddToRoleAsync(user, AdministratorRole), "assign the Administrator role");

        if (GetBoolean("RESET_ADMIN_PASSWORD", false))
        {
            var resetToken = await users.GeneratePasswordResetTokenAsync(user);
            EnsureSucceeded(await users.ResetPasswordAsync(user, resetToken, password), "reset the development administrator password");
            user.MustChangePassword = true;
            EnsureSucceeded(await users.UpdateAsync(user), "mark the reset administrator password for replacement");
            LogAdminReset(logger, email);
        }
    }

    private async Task SeedDemoDataAsync(CancellationToken cancellationToken)
    {
        if (!GetBoolean("SEED_DEMO_LICENSE", environment.IsDevelopment())) return;

        var customer = await db.Customers.SingleOrDefaultAsync(x => x.ExternalId == "POC-CUSTOMER", cancellationToken);
        if (customer is null)
        {
            customer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = "Example Pty Ltd",
                ExternalId = "POC-CUSTOMER",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(customer);
        }

        if (!await db.Licenses.AnyAsync(x => x.LicenseId == "LIC-POC-0001", cancellationToken))
        {
            db.Licenses.Add(new LicenseRecord
            {
                Id = Guid.NewGuid(),
                LicenseId = "LIC-POC-0001",
                Customer = customer,
                ActivationCodeHash = LicenseStore.Hash("POC-DEMO-ACTIVATION-CODE"),
                MetadataJson = new JsonObject { ["purchaseOrder"] = "PO-POC-0001" }.ToJsonString(),
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = LicenseTerms.PerpetualExpiry,
                Entitlements =
                [
                    new Entitlement
                    {
                        Id = Guid.NewGuid(),
                        Product = "gcexp",
                        Edition = "professional",
                        LicenseType = "perpetual",
                        Seats = 1,
                        UpdatesUntil = new DateOnly(2027, 8, 12),
                        License = null!
                    }
                ]
            });
        }

        if (!await db.SigningKeys.AnyAsync(x => x.KeyId == "primary-2026", cancellationToken))
        {
            var publicKeyPath = ResolvePublicKeyPath();
            db.SigningKeys.Add(new SigningKeyRecord
            {
                Id = Guid.NewGuid(),
                KeyId = "primary-2026",
                Algorithm = "ECDSA-P256-SHA256",
                PublicKeyPem = await File.ReadAllTextAsync(publicKeyPath, cancellationToken),
                Provider = "development-file (private key external to database)",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private string ResolvePublicKeyPath()
    {
        var configured = configuration["Licensing:PublicKeyPath"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "..", "..", "keys", "license-primary-2026-public.pem")
            : configured;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new InvalidOperationException($"Public signing key metadata file was not found at '{path}'.");
        return path;
    }

    private bool GetBoolean(string key, bool defaultValue) =>
        bool.TryParse(configuration[key], out var value) ? value : defaultValue;

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded) return;
        throw new InvalidOperationException($"Unable to {operation}: {string.Join("; ", result.Errors.Select(x => x.Code))}");
    }

    [LoggerMessage(1001, LogLevel.Information, "Default administrator seeding is disabled.")]
    private static partial void LogAdminSeedDisabled(ILogger logger);

    [LoggerMessage(1002, LogLevel.Warning, "A development-only administrator account was seeded for {Email}; its password must be changed at first login.")]
    private static partial void LogAdminSeeded(ILogger logger, string email);

    [LoggerMessage(1003, LogLevel.Warning, "The development administrator password was reset for {Email}. Disable RESET_ADMIN_PASSWORD immediately; the new password must be changed at login.")]
    private static partial void LogAdminReset(ILogger logger, string email);
}
