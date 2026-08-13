using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer;

internal sealed class ApiCredentialHasher(byte[] pepper)
{
    public const string CurrentVersion = "hmac-sha256-v1";

    public byte[] Hash(string publicId, string secret)
    {
        using var hmac = new HMACSHA256(pepper);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"{publicId}.{secret}"));
    }

    public bool Verify(string publicId, string secret, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(Hash(publicId, secret), expected);
}

internal sealed class ApiCredentialService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    IAuthorizationService authorization,
    IHttpContextAccessor httpContextAccessor,
    ApiCredentialHasher hasher,
    TimeProvider clock)
    : IOwnedCredentialRevoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CreatedApiCredential> CreateAsync(
        CreateApiCredentialRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await RequireOwnerAccessAsync(request.OwnerUserId);
        var owner = await users.FindByIdAsync(request.OwnerUserId)
            ?? throw new InvalidOperationException("Credential owner was not found.");
        if (!owner.IsEnabled) throw new InvalidOperationException("Disabled users cannot own new API credentials.");
        var expiry = ValidateExpiry(owner, request.ExpiresAt);
        var scopes = await ValidateScopesAsync(owner, request.Scopes);
        var created = CreateRecord(request.Name, owner, scopes, expiry);
        db.ApiCredentials.Add(created.Record);
        AddAudit(actor, "api-key.created", created.Record, new { scopes, expiry });
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiCredential(ToView(created.Record, scopes), created.Secret);
    }

    public async Task<IReadOnlyList<ApiCredentialView>> ListAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await RequireOwnerAccessAsync(ownerUserId);
        var rows = await db.ApiCredentials.AsNoTracking().Include(item => item.OwnerUser)
            .Where(item => item.OwnerUserId == ownerUserId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(item => ToView(item, ParseScopes(item.ScopesJson))).ToArray();
    }

    public async Task<CreatedApiCredential> RotateAsync(
        Guid credentialId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var current = await db.ApiCredentials.Include(item => item.OwnerUser)
            .SingleOrDefaultAsync(item => item.Id == credentialId, cancellationToken)
            ?? throw new InvalidOperationException("API credential was not found.");
        await RequireOwnerAccessAsync(current.OwnerUserId);
        var now = clock.GetUtcNow();
        if (current.RevokedAt is not null || current.ExpiresAt <= now)
            throw new InvalidOperationException("Expired or revoked API credentials cannot be rotated.");
        var scopes = ParseScopes(current.ScopesJson);
        var replacement = CreateRecord(current.Name, current.OwnerUser, scopes, current.ExpiresAt);
        current.RevokedAt = now;
        current.RevokedBy = actor;
        current.ReplacedByCredentialId = replacement.Record.Id;
        db.ApiCredentials.Add(replacement.Record);
        AddAudit(actor, "api-key.rotated", current,
            new { replacementPublicId = replacement.Record.PublicId, ownerUserId = current.OwnerUserId });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedApiCredential(ToView(replacement.Record, scopes), replacement.Secret);
    }

    public async Task RevokeAsync(Guid credentialId, string actor, CancellationToken cancellationToken = default)
    {
        var credential = await db.ApiCredentials.SingleOrDefaultAsync(item => item.Id == credentialId, cancellationToken)
            ?? throw new InvalidOperationException("API credential was not found.");
        await RequireOwnerAccessAsync(credential.OwnerUserId);
        if (credential.RevokedAt is not null) return;
        credential.RevokedAt = clock.GetUtcNow();
        credential.RevokedBy = actor;
        AddAudit(actor, "api-key.revoked", credential, new { credential.OwnerUserId });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthenticatedApiCredential?> AuthenticateAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!TryParse(value, out var publicId, out var secret)) return null;
        var credential = await db.ApiCredentials.AsNoTracking().Include(item => item.OwnerUser)
            .SingleOrDefaultAsync(item => item.PublicId == publicId, cancellationToken);
        var now = clock.GetUtcNow();
        if (credential is null
            || credential.RevokedAt is not null
            || credential.ExpiresAt <= now
            || !credential.OwnerUser.IsEnabled
            || credential.HashVersion != ApiCredentialHasher.CurrentVersion
            || !hasher.Verify(publicId, secret, credential.SecretHash))
        {
            return null;
        }

        if (credential.LastUsedAt is null || credential.LastUsedAt <= now.AddMinutes(-5))
        {
            await db.ApiCredentials.Where(item => item.Id == credential.Id
                    && (item.LastUsedAt == null || item.LastUsedAt <= now.AddMinutes(-5)))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastUsedAt, now), cancellationToken);
        }
        return new AuthenticatedApiCredential(
            credential.Id,
            credential.OwnerUserId,
            credential.OwnerUser.Email ?? credential.PublicId,
            ParseScopes(credential.ScopesJson));
    }

    public async Task RevokeAllAsync(
        string ownerUserId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var credentials = await db.ApiCredentials
            .Where(item => item.OwnerUserId == ownerUserId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var credential in credentials)
        {
            credential.RevokedAt = now;
            credential.RevokedBy = actor;
            AddAudit(actor, "api-key.revoked-owner-disabled", credential, new { ownerUserId });
        }
        if (credentials.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RequireOwnerAccessAsync(string ownerUserId)
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null) return;
        var caller = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var permission = string.Equals(caller, ownerUserId, StringComparison.Ordinal)
            ? Permissions.ApiKeysManageSelf
            : Permissions.ApiKeysManageAll;
        if (!(await authorization.AuthorizeAsync(context.User, null, permission)).Succeeded)
            throw new UnauthorizedAccessException("The API credential owner is outside the caller's management scope.");
    }

    private async Task<IReadOnlyList<string>> ValidateScopesAsync(ApplicationUser owner, IReadOnlyList<string>? requested)
    {
        var scopes = (requested ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0) throw new InvalidOperationException("At least one API credential scope is required.");
        if (scopes.Any(scope => !Permissions.All.Contains(scope, StringComparer.Ordinal)))
            throw new InvalidOperationException("An API credential scope is not recognized.");
        var ownerRoles = await users.GetRolesAsync(owner);
        var allowed = ownerRoles.SelectMany(BuiltInRoles.PermissionsFor).ToHashSet(StringComparer.Ordinal);
        if (scopes.Any(scope => !allowed.Contains(scope)))
            throw new InvalidOperationException("API credential scopes cannot exceed the owner's role permissions.");
        return scopes;
    }

    private DateTimeOffset? ValidateExpiry(ApplicationUser owner, DateTimeOffset? expiry)
    {
        var now = clock.GetUtcNow();
        if (owner.AccountType == ApplicationUser.HumanAccountType && expiry is null)
            throw new InvalidOperationException("Human-owned API credentials require an expiry.");
        if (expiry is not null && expiry <= now)
            throw new InvalidOperationException("API credential expiry must be in the future.");
        return expiry?.ToUniversalTime();
    }

    private (ApiCredential Record, string Secret) CreateRecord(
        string? name,
        ApplicationUser owner,
        IReadOnlyList<string> scopes,
        DateTimeOffset? expiry)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new InvalidOperationException("API credential name is required and cannot exceed 200 characters.");
        var publicId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var fullValue = $"lic_live_{publicId}_{secret}";
        return (new ApiCredential
        {
            Id = Guid.NewGuid(),
            PublicId = publicId,
            Name = normalizedName,
            OwnerUserId = owner.Id,
            OwnerUser = owner,
            SecretHash = hasher.Hash(publicId, secret),
            HashVersion = ApiCredentialHasher.CurrentVersion,
            LastFour = secret[^4..],
            ScopesJson = JsonSerializer.Serialize(scopes, JsonOptions),
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = expiry
        }, fullValue);
    }

    private void AddAudit(string actor, string action, ApiCredential credential, object context) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = action,
            TargetType = "api-key",
            TargetId = credential.PublicId,
            Result = "success",
            ContextJson = JsonSerializer.Serialize(context, JsonOptions),
            TimestampUtc = clock.GetUtcNow()
        });

    private static bool TryParse(string value, out string publicId, out string secret)
    {
        publicId = secret = "";
        const string prefix = "lic_live_";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var separator = value.IndexOf('_', prefix.Length);
        if (separator < 0) return false;
        publicId = value[prefix.Length..separator];
        secret = value[(separator + 1)..];
        return publicId.Length == 16 && secret.Length == 43;
    }

    private static string[] ParseScopes(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];

    private static ApiCredentialView ToView(ApiCredential item, IReadOnlyList<string> scopes) => new(
        item.Id, item.PublicId, item.Name, item.OwnerUserId, item.OwnerUser.Email ?? "",
        item.LastFour, scopes, item.CreatedAt, item.LastUsedAt, item.ExpiresAt, item.RevokedAt,
        item.ReplacedByCredentialId);
}

public sealed record CreateApiCredentialRequest(
    string? Name,
    string OwnerUserId,
    IReadOnlyList<string>? Scopes,
    DateTimeOffset? ExpiresAt);

public sealed record ApiCredentialView(
    Guid Id,
    string PublicId,
    string Name,
    string OwnerUserId,
    string OwnerEmail,
    string LastFour,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    Guid? ReplacedByCredentialId);

public sealed record CreatedApiCredential(ApiCredentialView Credential, string Secret);
internal sealed record AuthenticatedApiCredential(Guid Id, string OwnerUserId, string OwnerName, IReadOnlyList<string> Scopes);
