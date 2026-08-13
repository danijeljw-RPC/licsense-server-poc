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

public sealed record RevokeRequest(bool Confirmed, string? Reason, long? Version);

public sealed record CancelRequest(bool Confirmed, string? Reason, long Version, string? Reference = null);

public sealed record AdminDeactivateRequest(bool Confirmed, string? Reason, long Version);

public sealed record AmendTermsRequest(
    DateTimeOffset? ExpiresAt,
    int? Seats,
    DateOnly? UpdatesUntil,
    string? Reason,
    long Version);

public sealed record IssueLicenseRequest(
    string? CustomerName,
    string? CustomerEmail,
    string? Product,
    string? Edition,
    string? LicenseType,
    DateTimeOffset? ExpiresAt,
    int Seats,
    DateOnly? UpdatesUntil,
    IReadOnlyDictionary<string, object?>? Metadata);
