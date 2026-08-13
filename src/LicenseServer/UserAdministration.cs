using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer;

internal interface IOwnedCredentialRevoker
{
    Task RevokeAllAsync(string ownerUserId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default);
}

internal sealed class NullOwnedCredentialRevoker : IOwnedCredentialRevoker
{
    public Task RevokeAllAsync(string ownerUserId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class UserAdministrationService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    PermissionGuard permissions,
    IOwnedCredentialRevoker credentialRevoker,
    ITransactionalEmailSender emailSender,
    TimeProvider clock,
    IWebHostEnvironment environment)
{
    private const long AdministratorMutationLock = 739_210_864_091;

    public async Task<IReadOnlyList<UserView>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.UsersRead);
        var query = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(user =>
                (user.Email != null && EF.Functions.ILike(user.Email, $"%{term}%"))
                || EF.Functions.ILike(user.AccountType, $"%{term}%"));
        }

        var projected = new List<UserView>();
        foreach (var user in await query.OrderBy(user => user.Email).Take(200).ToListAsync(cancellationToken))
            projected.Add(await ProjectAsync(user));
        return projected;
    }

    public async Task<UserInviteResult> InviteHumanAsync(
        InviteUserRequest request,
        string actor,
        Uri applicationBaseUri,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.UsersManage);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var email = ValidateEmail(request.Email);
        await EnsureEmailAvailableAsync(email);
        var requestedRoles = await ValidateRolesAsync(request.Roles);
        var now = clock.GetUtcNow();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = false,
            AccountType = ApplicationUser.HumanAccountType,
            IsEnabled = true,
            MustChangePassword = true,
            CreatedAt = now
        };
        EnsureSucceeded(await users.CreateAsync(user), "create the invited operator");
        await AssignRolesAsync(user, requestedRoles);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
        var callback = new Uri(applicationBaseUri,
            $"Account/ResetPassword?code={Uri.EscapeDataString(code)}&email={Uri.EscapeDataString(email)}");
        await emailSender.QueueAsync(new TransactionalEmail(
                EmailTemplates.OperatorInvitation, email,
                new Dictionary<string, string> { ["actionUrl"] = callback.AbsoluteUri }),
            $"operator-invite:{user.Id}:{HashForIdempotency(code)}", cancellationToken);
        await AuditAsync(actor, "user.invited", user.Id, new { accountType = user.AccountType, roles = requestedRoles }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserInviteResult(await ProjectAsync(user), environment.IsDevelopment() ? callback.AbsoluteUri : null);
    }

    public async Task<UserMutationResult> CreateServiceAccountAsync(
        CreateServiceAccountRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.UsersManage);
        var email = ValidateEmail(request.Email);
        await EnsureEmailAvailableAsync(email);
        var requestedRoles = await ValidateRolesAsync(request.Roles);
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = false,
            AccountType = ApplicationUser.ServiceAccountType,
            IsEnabled = true,
            MustChangePassword = false,
            CreatedAt = clock.GetUtcNow()
        };
        EnsureSucceeded(await users.CreateAsync(user), "create the service account");
        await AssignRolesAsync(user, requestedRoles);
        await AuditAsync(actor, "user.service-account-created", user.Id,
            new { accountType = user.AccountType, roles = requestedRoles }, cancellationToken);
        return new UserMutationResult(await ProjectAsync(user));
    }

    public async Task<UserMutationResult> SetEnabledAsync(
        string userId,
        bool enabled,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.UsersManage);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireAdministratorLockAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId) ?? throw new InvalidOperationException("User was not found.");
        if (user.IsEnabled == enabled)
        {
            await transaction.CommitAsync(cancellationToken);
            return new UserMutationResult(await ProjectAsync(user));
        }

        if (!enabled && await users.IsInRoleAsync(user, BuiltInRoles.SystemAdministrator))
            await EnsureAnotherEnabledAdministratorAsync(user.Id, cancellationToken);

        var now = clock.GetUtcNow();
        user.IsEnabled = enabled;
        user.DisabledAt = enabled ? null : now;
        user.DisabledBy = enabled ? null : actor;
        EnsureSucceeded(await users.UpdateSecurityStampAsync(user), "invalidate the user's sessions");
        EnsureSucceeded(await users.UpdateAsync(user), enabled ? "enable the user" : "disable the user");
        if (!enabled)
            await credentialRevoker.RevokeAllAsync(user.Id, actor, now, cancellationToken);
        await AuditAsync(actor, enabled ? "user.enabled" : "user.disabled", user.Id,
            new { accountType = user.AccountType }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserMutationResult(await ProjectAsync(user));
    }

    public async Task<UserMutationResult> ReplaceRolesAsync(
        string userId,
        IReadOnlyList<string>? requested,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.UsersManage);
        var expected = await ValidateRolesAsync(requested);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireAdministratorLockAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId) ?? throw new InvalidOperationException("User was not found.");
        var current = await users.GetRolesAsync(user);
        if (user.IsEnabled
            && current.Contains(BuiltInRoles.SystemAdministrator, StringComparer.Ordinal)
            && !expected.Contains(BuiltInRoles.SystemAdministrator, StringComparer.Ordinal))
        {
            await EnsureAnotherEnabledAdministratorAsync(user.Id, cancellationToken);
        }

        var remove = current.Where(role => !expected.Contains(role, StringComparer.Ordinal)).ToArray();
        var add = expected.Where(role => !current.Contains(role, StringComparer.Ordinal)).ToArray();
        if (remove.Length > 0) EnsureSucceeded(await users.RemoveFromRolesAsync(user, remove), "remove old user roles");
        if (add.Length > 0) EnsureSucceeded(await users.AddToRolesAsync(user, add), "add new user roles");
        EnsureSucceeded(await users.UpdateSecurityStampAsync(user), "invalidate sessions after role change");
        await AuditAsync(actor, "user.roles-changed", user.Id, new { roles = expected }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserMutationResult(await ProjectAsync(user));
    }

    public async Task<UserInviteResult> InitiatePasswordResetAsync(
        string userId,
        string actor,
        Uri applicationBaseUri,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.UsersManage);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var user = await users.FindByIdAsync(userId) ?? throw new InvalidOperationException("User was not found.");
        if (user.AccountType != ApplicationUser.HumanAccountType)
            throw new InvalidOperationException("Service accounts cannot receive password reset links.");
        var email = user.Email ?? throw new InvalidOperationException("User does not have an email address.");
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
        var callback = new Uri(applicationBaseUri,
            $"Account/ResetPassword?code={Uri.EscapeDataString(code)}&email={Uri.EscapeDataString(email)}");
        await emailSender.QueueAsync(new TransactionalEmail(
                EmailTemplates.IdentityPasswordRecovery, email,
                new Dictionary<string, string> { ["actionUrl"] = callback.AbsoluteUri }),
            $"operator-password-reset:{user.Id}:{HashForIdempotency(code)}", cancellationToken);
        await AuditAsync(actor, "user.password-reset-initiated", user.Id, new { accountType = user.AccountType }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserInviteResult(await ProjectAsync(user), environment.IsDevelopment() ? callback.AbsoluteUri : null);
    }

    private async Task<UserView> ProjectAsync(ApplicationUser user)
    {
        var assignedRoles = (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal).ToArray();
        var derivedPermissions = assignedRoles.SelectMany(BuiltInRoles.PermissionsFor)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var credentialCount = await db.ApiCredentials.CountAsync(item => item.OwnerUserId == user.Id);
        return new UserView(
            user.Id,
            user.Email ?? "",
            user.AccountType,
            user.IsEnabled,
            user.DisabledAt,
            user.CreatedAt,
            user.LastLoginAt,
            assignedRoles,
            derivedPermissions,
            user.AccountType == ApplicationUser.HumanAccountType && await users.GetTwoFactorEnabledAsync(user),
            credentialCount);
    }

    private async Task<IReadOnlyList<string>> ValidateRolesAsync(IReadOnlyList<string>? requested)
    {
        var distinct = (requested ?? []).Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var role in distinct)
        {
            if (!BuiltInRoles.All.Contains(role, StringComparer.Ordinal) || !await roles.RoleExistsAsync(role))
                throw new InvalidOperationException($"Role '{role}' is not assignable.");
        }
        return distinct;
    }

    private async Task AssignRolesAsync(ApplicationUser user, IReadOnlyList<string> requestedRoles)
    {
        if (requestedRoles.Count > 0)
            EnsureSucceeded(await users.AddToRolesAsync(user, requestedRoles), "assign user roles");
    }

    private async Task EnsureEmailAvailableAsync(string email)
    {
        if (await users.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("A user with that email already exists.");
    }

    private static string ValidateEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        if (!CustomerEmails.TryNormalize(email, out var normalized, out var error))
            throw new InvalidOperationException(error ?? "A valid email address is required.");
        return normalized!;
    }

    private async Task AcquireAdministratorLockAsync(CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AdministratorMutationLock})", cancellationToken);

    private async Task EnsureAnotherEnabledAdministratorAsync(string excludedUserId, CancellationToken cancellationToken)
    {
        var role = await roles.FindByNameAsync(BuiltInRoles.SystemAdministrator)
            ?? throw new InvalidOperationException("System Administrator role is unavailable.");
        var count = await db.UserRoles
            .Where(link => link.RoleId == role.Id && link.UserId != excludedUserId)
            .Join(db.Users, link => link.UserId, user => user.Id, (_, user) => user)
            .CountAsync(user => user.IsEnabled, cancellationToken);
        if (count == 0)
            throw new InvalidOperationException("The final enabled System Administrator cannot be disabled or demoted.");
    }

    private async Task AuditAsync(string actor, string action, string targetId, object context, CancellationToken cancellationToken)
    {
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = action,
            TargetType = "user",
            TargetId = targetId,
            Result = "success",
            ContextJson = JsonSerializer.Serialize(context),
            TimestampUtc = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded) return;
        throw new InvalidOperationException($"Unable to {operation}: {string.Join("; ", result.Errors.Select(error => error.Code))}");
    }

    private static string HashForIdempotency(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record UserView(
    string Id,
    string Email,
    string AccountType,
    bool IsEnabled,
    DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool MfaEnabled,
    int ApiCredentialCount);

public sealed record UserInviteResult(UserView User, string? DevelopmentSetupUrl);
public sealed record UserMutationResult(UserView User);
