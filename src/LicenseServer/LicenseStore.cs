using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using SoftwareLicensing;

namespace LicenseServer;

internal sealed class LicenseStore(ApplicationDbContext db)
{
    public async Task<StoreResult<ActiveActivation>> ActivateAsync(
        string licenseId,
        ActivateRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateActivationRequest(request);
        if (validation is not null)
            return StoreResult<ActiveActivation>.BadRequest(validation);

        var requestId = Guid.Parse(request.RequestId!);
        var tokenHash = Hash(request.ActivationToken!);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var license = await db.Licenses
                .FromSqlInterpolated($"SELECT * FROM \"Licenses\" WHERE \"LicenseId\" = {licenseId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);

            if (license is null)
                return StoreResult<ActiveActivation>.NotFound("License was not found.");
            if (!FixedTimeMatches(request.ActivationCode, license.ActivationCodeHash))
                return StoreResult<ActiveActivation>.Unauthorized("Activation code is invalid.");
            if (license.RevokedAt is not null)
                return StoreResult<ActiveActivation>.Forbidden("License has been revoked.");
            if (license.ExpiresAt is not null && license.ExpiresAt <= now)
                return StoreResult<ActiveActivation>.Forbidden("License has expired.");

            var priorRequest = await db.Activations
                .SingleOrDefaultAsync(x => x.LicenseRecordId == license.Id && x.RequestId == requestId, cancellationToken);
            if (priorRequest is not null)
            {
                var idempotent = priorRequest.DeactivatedAt is null
                    && string.Equals(priorRequest.DeviceIdHash, request.Device!.DeviceId, StringComparison.OrdinalIgnoreCase)
                    && CryptographicOperations.FixedTimeEquals(priorRequest.TokenHash, tokenHash);
                return idempotent
                    ? StoreResult<ActiveActivation>.Ok(ToActive(priorRequest, license.LicenseId))
                    : StoreResult<ActiveActivation>.Conflict("The activation request ID has already been used.");
            }

            var active = await db.Activations.SingleOrDefaultAsync(
                x => x.LicenseRecordId == license.Id && x.DeactivatedAt == null,
                cancellationToken);
            if (active is not null)
            {
                return StoreResult<ActiveActivation>.Conflict(
                    $"License is already active on device ...{active.DeviceIdSuffix}. Deactivate that activation before transferring it.");
            }

            var isTransfer = await db.Activations.AnyAsync(
                x => x.LicenseRecordId == license.Id,
                cancellationToken);

            var entity = new Activation
            {
                Id = Guid.NewGuid(),
                ActivationId = Guid.NewGuid().ToString("D"),
                LicenseRecordId = license.Id,
                License = license,
                RequestId = requestId,
                DeviceIdHash = request.Device!.DeviceId!.ToUpperInvariant(),
                DeviceIdSuffix = request.Device.DeviceId[^8..].ToUpperInvariant(),
                DeviceName = CleanDeviceName(request.Device.DeviceName),
                Mode = request.Mode!,
                TokenHash = tokenHash,
                ActivatedAt = now,
                RefreshAfter = request.Mode == "online" ? now.AddDays(1) : null,
                LeaseExpiresAt = request.Mode == "online" ? now.AddDays(7) : null
            };
            db.Activations.Add(entity);
            AddAudit("license-client", "activation.created", "activation", entity.ActivationId, "success", new
            {
                licenseId,
                mode = entity.Mode,
                deviceSuffix = entity.DeviceIdSuffix
            }, now);
            if (isTransfer)
            {
                AddAudit("license-client", "license.transferred", "license", licenseId, "success", new
                {
                    activationId = entity.ActivationId,
                    deviceSuffix = entity.DeviceIdSuffix,
                    mode = entity.Mode
                }, now);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return StoreResult<ActiveActivation>.Ok(ToActive(entity, license.LicenseId));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreResult<ActiveActivation>.Conflict("The license was activated concurrently. Retry to see the active device.");
        }
    }

    public async Task<StoreResult<ActiveActivation>> AuthorizeAsync(
        string activationId,
        ActivationCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        var activation = await db.Activations.Include(x => x.License)
            .SingleOrDefaultAsync(x => x.ActivationId == activationId && x.DeactivatedAt == null, cancellationToken);
        if (activation is null)
            return StoreResult<ActiveActivation>.NotFound("Activation is not active.");
        if (activation.License.RevokedAt is not null)
            return StoreResult<ActiveActivation>.Forbidden("License has been revoked.");
        if (activation.License.ExpiresAt is not null && activation.License.ExpiresAt <= DateTimeOffset.UtcNow)
            return StoreResult<ActiveActivation>.Forbidden("License has expired.");
        if (string.IsNullOrWhiteSpace(request.DeviceId)
            || !string.Equals(activation.DeviceIdHash, request.DeviceId, StringComparison.OrdinalIgnoreCase)
            || !FixedTimeMatches(request.ActivationToken, activation.TokenHash))
        {
            return StoreResult<ActiveActivation>.Unauthorized("Activation credentials are invalid.");
        }

        return StoreResult<ActiveActivation>.Ok(ToActive(activation, activation.License.LicenseId));
    }

    public async Task<StoreResult<ActiveActivation>> RefreshAsync(
        string activationId,
        ActivationCredentialRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var authorized = await AuthorizeAsync(activationId, request, cancellationToken);
        if (!authorized.Success)
            return authorized;
        if (authorized.Value!.Mode != "online")
            return StoreResult<ActiveActivation>.Conflict("Offline activations do not use leases.");

        var activation = await db.Activations.SingleAsync(x => x.ActivationId == activationId, cancellationToken);
        activation.LastRefreshedAt = now;
        activation.RefreshAfter = now.AddDays(1);
        activation.LeaseExpiresAt = now.AddDays(7);
        AddAudit("license-client", "activation.refreshed", "activation", activationId, "success", new
        {
            leaseExpiresAt = activation.LeaseExpiresAt
        }, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreResult<ActiveActivation>.Ok(ToActive(activation, authorized.Value.LicenseId));
    }

    public async Task<StoreResult<ActiveActivation>> DeactivateAsync(
        string activationId,
        ActivationCredentialRequest request,
        DateTimeOffset now,
        string actor = "license-client",
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var authorized = await AuthorizeAsync(activationId, request, cancellationToken);
        if (!authorized.Success)
            return authorized;

        var activation = await db.Activations.SingleAsync(x => x.ActivationId == activationId, cancellationToken);
        activation.DeactivatedAt = now;
        AddAudit(actor, "activation.deactivated", "activation", activationId, "success", new
        {
            licenseId = authorized.Value!.LicenseId,
            deviceSuffix = activation.DeviceIdSuffix
        }, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreResult<ActiveActivation>.Ok(ToActive(activation, authorized.Value.LicenseId));
    }

    public async Task<StoreResult<bool>> RevokeAsync(
        string licenseId,
        string? reason,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
            return StoreResult<bool>.BadRequest("A revocation reason of at least three characters is required.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var license = await db.Licenses.SingleOrDefaultAsync(x => x.LicenseId == licenseId, cancellationToken);
        if (license is null)
            return StoreResult<bool>.NotFound("License was not found.");
        if (license.RevokedAt is null)
        {
            license.RevokedAt = now;
            license.RevocationReason = reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
            AddAudit(actor, "license.revoked", "license", licenseId, "success", new { reason = license.RevocationReason }, now);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return StoreResult<bool>.Ok(true);
    }

    public async Task<JsonObject> CreateLicenseAsync(
        ActiveActivation activation,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        var record = await db.Licenses.AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Entitlements)
            .SingleAsync(x => x.Id == activation.LicenseRecordId, cancellationToken);

        var activationJson = new JsonObject
        {
            ["activationId"] = activation.ActivationId,
            ["mode"] = activation.Mode,
            ["activatedAt"] = activation.ActivatedAt.ToString("O")
        };
        if (activation.Mode == "online")
        {
            activationJson["refreshAfter"] = activation.RefreshAfter?.ToString("O");
            activationJson["leaseExpiresAt"] = activation.LeaseExpiresAt?.ToString("O");
        }

        var bindingJson = new JsonObject
        {
            ["scheme"] = DeviceIdentity.Scheme,
            ["deviceId"] = activation.DeviceIdHash
        };
        if (!string.IsNullOrWhiteSpace(activation.DeviceName))
            bindingJson["deviceName"] = activation.DeviceName;

        var entitlements = new JsonArray();
        foreach (var entitlement in record.Entitlements.OrderBy(x => x.Product))
        {
            var item = new JsonObject
            {
                ["product"] = entitlement.Product,
                ["edition"] = entitlement.Edition,
                ["licenseType"] = entitlement.LicenseType,
                ["seats"] = entitlement.Seats
            };
            if (entitlement.UpdatesUntil is not null)
                item["updatesUntil"] = entitlement.UpdatesUntil.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            entitlements.Add(item);
        }

        return new JsonObject
        {
            ["licenseId"] = record.LicenseId,
            ["customer"] = record.Customer.Name,
            ["issuedAt"] = issuedAt.ToString("O"),
            ["metadata"] = JsonNode.Parse(record.MetadataJson) ?? new JsonObject(),
            ["deviceBinding"] = bindingJson,
            ["activation"] = activationJson,
            ["entitlements"] = entitlements
        };
    }

    private void AddAudit(string actor, string action, string targetType, string targetId, string result, object context, DateTimeOffset now) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Result = result,
            ContextJson = JsonSerializer.Serialize(context),
            TimestampUtc = now
        });

    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string? ValidateActivationRequest(ActivateRequest request)
    {
        if (request.Device is null
            || !string.Equals(request.Device.Scheme, DeviceIdentity.Scheme, StringComparison.Ordinal)
            || request.Device.DeviceId is null
            || !DeviceIdentity.IsValidDeviceId(request.Device.DeviceId))
            return $"Device must use {DeviceIdentity.Scheme} with a 64-character SHA-256 identifier.";
        if (request.Mode is not ("online" or "offline"))
            return "Mode must be 'online' or 'offline'.";
        if (!Guid.TryParse(request.RequestId, out _) || !IsStrongActivationToken(request.ActivationToken))
            return "RequestId must be a GUID and activationToken must be 32 random bytes encoded as Base64.";
        return null;
    }

    private static string? CleanDeviceName(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, 100)];

    private static bool IsStrongActivationToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return Convert.FromBase64String(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool FixedTimeMatches(string? supplied, byte[] expected) =>
        supplied is not null && CryptographicOperations.FixedTimeEquals(Hash(supplied), expected);

    private static ActiveActivation ToActive(Activation value, string licenseId) => new(
        value.LicenseRecordId,
        licenseId,
        value.ActivationId,
        value.DeviceIdHash,
        value.DeviceIdSuffix,
        value.DeviceName,
        value.Mode,
        value.ActivatedAt,
        value.RefreshAfter,
        value.LeaseExpiresAt);

    internal sealed record ActiveActivation(
        Guid LicenseRecordId,
        string LicenseId,
        string ActivationId,
        string DeviceIdHash,
        string DeviceIdSuffix,
        string? DeviceName,
        string Mode,
        DateTimeOffset ActivatedAt,
        DateTimeOffset? RefreshAfter,
        DateTimeOffset? LeaseExpiresAt);
}

internal sealed record StoreResult<T>(bool Success, int StatusCode, string? Error, T? Value)
{
    public static StoreResult<T> Ok(T value) => new(true, 200, null, value);
    public static StoreResult<T> BadRequest(string error) => new(false, 400, error, default);
    public static StoreResult<T> Unauthorized(string error) => new(false, 401, error, default);
    public static StoreResult<T> Forbidden(string error) => new(false, 403, error, default);
    public static StoreResult<T> NotFound(string error) => new(false, 404, error, default);
    public static StoreResult<T> Conflict(string error) => new(false, 409, error, default);
}
