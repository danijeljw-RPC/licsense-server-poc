namespace LicenseServer;

public sealed record DeviceRequest(string? Scheme, string? DeviceId, string? DeviceName);

public sealed record ActivateRequest(
    string? RequestId,
    string? ActivationCode,
    string? ActivationToken,
    string? Mode,
    DeviceRequest? Device);

public sealed record ActivationCredentialRequest(
    string? ActivationToken,
    string? DeviceId);

public sealed record ActivationResponse(
    string LicenseId,
    string ActivationId,
    string Status,
    string SignedLicense,
    DateTimeOffset? RefreshAfter,
    DateTimeOffset? LeaseExpiresAt);

public sealed record ValidationResponse(
    string LicenseId,
    string ActivationId,
    string Status,
    DateTimeOffset ServerTime);

public sealed record DeactivationResponse(
    string LicenseId,
    string ActivationId,
    string Status,
    DateTimeOffset DeactivatedAt);

public sealed record RevokeRequest(string? Reason);
