using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SoftwareLicensing;

namespace LicenseServer;

internal sealed class LicenseStore
{
    private readonly object gate = new();
    private readonly LicenseDefinition demo = LicenseDefinition.CreateDemo();
    private ActiveActivation? active;
    private bool revoked;
    private string? revocationReason;

    public StoreResult<ActiveActivation> Activate(ActivateRequest request, DateTimeOffset now)
    {
        lock (gate)
        {
            if (request.Device is null
                || !string.Equals(request.Device.Scheme, DeviceIdentity.Scheme, StringComparison.Ordinal)
                || request.Device.DeviceId is null
                || !DeviceIdentity.IsValidDeviceId(request.Device.DeviceId))
            {
                return StoreResult<ActiveActivation>.BadRequest(
                    $"Device must use {DeviceIdentity.Scheme} with a 64-character SHA-256 identifier.");
            }

            if (request.Mode is not ("online" or "offline"))
                return StoreResult<ActiveActivation>.BadRequest("Mode must be 'online' or 'offline'.");

            if (!Guid.TryParse(request.RequestId, out _)
                || !IsStrongActivationToken(request.ActivationToken))
            {
                return StoreResult<ActiveActivation>.BadRequest(
                    "RequestId must be a GUID and activationToken must be 32 random bytes encoded as Base64.");
            }

            if (!FixedTimeMatches(request.ActivationCode, demo.ActivationCodeHash))
                return StoreResult<ActiveActivation>.Unauthorized("Activation code is invalid.");

            if (revoked)
                return StoreResult<ActiveActivation>.Forbidden("Licence has been revoked.");

            var tokenHash = Hash(request.ActivationToken!);

            if (active is not null)
            {
                var idempotent = string.Equals(active.RequestId, request.RequestId, StringComparison.Ordinal)
                    && string.Equals(active.DeviceId, request.Device.DeviceId, StringComparison.OrdinalIgnoreCase)
                    && CryptographicOperations.FixedTimeEquals(active.TokenHash, tokenHash);

                return idempotent
                    ? StoreResult<ActiveActivation>.Ok(active)
                    : StoreResult<ActiveActivation>.Conflict(
                        $"Licence is already active on device ...{active.DeviceId[^8..]}. " +
                        "Deactivate that activation before transferring it.");
            }

            active = new ActiveActivation(
                Guid.NewGuid().ToString("D"),
                request.RequestId!,
                request.Device.DeviceId.ToUpperInvariant(),
                request.Device.DeviceName,
                request.Mode!,
                tokenHash,
                now);

            return StoreResult<ActiveActivation>.Ok(active);
        }
    }

    public StoreResult<ActiveActivation> Authorize(
        string activationId,
        ActivationCredentialRequest request)
    {
        lock (gate)
        {
            if (revoked)
                return StoreResult<ActiveActivation>.Forbidden("Licence has been revoked.");

            if (active is null
                || !string.Equals(active.ActivationId, activationId, StringComparison.OrdinalIgnoreCase))
            {
                return StoreResult<ActiveActivation>.NotFound("Activation is not active.");
            }

            if (string.IsNullOrWhiteSpace(request.DeviceId)
                || !string.Equals(active.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase)
                || !FixedTimeMatches(request.ActivationToken, active.TokenHash))
            {
                return StoreResult<ActiveActivation>.Unauthorized("Activation credentials are invalid.");
            }

            return StoreResult<ActiveActivation>.Ok(active);
        }
    }

    public StoreResult<ActiveActivation> Deactivate(
        string activationId,
        ActivationCredentialRequest request)
    {
        lock (gate)
        {
            var result = Authorize(activationId, request);
            if (!result.Success)
                return result;

            var deactivated = active!;
            active = null;
            return StoreResult<ActiveActivation>.Ok(deactivated);
        }
    }

    public StoreResult<bool> Revoke(string? reason)
    {
        lock (gate)
        {
            revoked = true;
            revocationReason = string.IsNullOrWhiteSpace(reason) ? "No reason supplied." : reason;
            active = null;
            return StoreResult<bool>.Ok(true);
        }
    }

    public object Status()
    {
        lock (gate)
        {
            return new
            {
                demo.LicenseId,
                status = revoked ? "revoked" : active is null ? "available" : "active",
                revocationReason,
                activation = active is null ? null : new
                {
                    active.ActivationId,
                    active.DeviceId,
                    active.DeviceName,
                    active.Mode,
                    active.ActivatedAt
                }
            };
        }
    }

    public JsonObject CreateLicense(ActiveActivation activation, DateTimeOffset issuedAt)
    {
        var online = activation.Mode == "online";
        var activationJson = new JsonObject
        {
            ["activationId"] = activation.ActivationId,
            ["mode"] = activation.Mode,
            ["activatedAt"] = activation.ActivatedAt.ToString("O")
        };

        if (online)
        {
            activationJson["refreshAfter"] = issuedAt.AddDays(1).ToString("O");
            activationJson["leaseExpiresAt"] = issuedAt.AddDays(7).ToString("O");
        }

        var bindingJson = new JsonObject
        {
            ["scheme"] = DeviceIdentity.Scheme,
            ["deviceId"] = activation.DeviceId
        };
        if (!string.IsNullOrWhiteSpace(activation.DeviceName))
            bindingJson["deviceName"] = activation.DeviceName;

        return new JsonObject
        {
            ["licenseId"] = demo.LicenseId,
            ["customer"] = demo.Customer,
            ["issuedAt"] = issuedAt.ToString("O"),
            ["metadata"] = demo.Metadata.DeepClone(),
            ["deviceBinding"] = bindingJson,
            ["activation"] = activationJson,
            ["entitlements"] = demo.Entitlements.DeepClone()
        };
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool IsStrongActivationToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeMatches(string? supplied, byte[] expected)
    {
        return supplied is not null
            && CryptographicOperations.FixedTimeEquals(Hash(supplied), expected);
    }

    internal sealed record ActiveActivation(
        string ActivationId,
        string RequestId,
        string DeviceId,
        string? DeviceName,
        string Mode,
        byte[] TokenHash,
        DateTimeOffset ActivatedAt);

    private sealed record LicenseDefinition(
        string LicenseId,
        string Customer,
        byte[] ActivationCodeHash,
        JsonObject Metadata,
        JsonArray Entitlements)
    {
        public static LicenseDefinition CreateDemo() => new(
            "LIC-POC-0001",
            "Example Pty Ltd",
            Hash("POC-DEMO-ACTIVATION-CODE"),
            new JsonObject { ["purchaseOrder"] = "PO-POC-0001" },
            new JsonArray
            {
                new JsonObject
                {
                    ["product"] = "gcexp",
                    ["edition"] = "professional",
                    ["licenseType"] = "perpetual",
                    ["seats"] = 1,
                    ["updatesUntil"] = "2027-08-12"
                }
            });
    }
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
