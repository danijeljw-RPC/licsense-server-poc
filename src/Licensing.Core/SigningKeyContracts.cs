using System.Text.Json.Nodes;

namespace SoftwareLicensing;

/// <summary>
/// Signing-key ring contracts. Interfaces and records only, no implementation and no I/O - they
/// live here so both the server (which implements them over a database-backed, hot-reloading key
/// directory) and the offline CLI can share one vocabulary without Licensing.Core taking on an
/// ASP.NET Core or EF Core dependency.
/// </summary>
public enum SigningKeyStatus
{
    Active,
    VerificationOnly,
    Revoked,
    Invalid
}

public sealed record SigningKeyInfo(
    string KeyId,
    string Algorithm,
    bool HasPrivateKey,
    bool HasPublicKey,
    bool CanSign,
    bool CanVerify,
    SigningKeyStatus Status,
    string? StatusDetail,
    bool IsDefault,
    DateTimeOffset DiscoveredAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    string? RevocationReason);

public interface ILicenseKeyRing
{
    string? DefaultKeyId { get; }
    IReadOnlyList<SigningKeyInfo> Keys { get; }
    SigningKeyInfo? Find(string keyId);
}

public sealed record LicenseSigningResult(bool Success, JsonObject? Envelope, string? ErrorCode, string? ErrorMessage);

public interface ILicenseSigner
{
    LicenseSigningResult Sign(JsonObject license, string? requestedKeyId);

    /// <summary>
    /// True if <see cref="Sign"/> would currently succeed for this key selection - the same
    /// default/lookup/status resolution, without the cost of a private-key import or signature.
    /// Callers that mutate durable state before signing (activation, lease refresh) use this to
    /// fail before that mutation instead of after, since state committed ahead of a signing
    /// failure cannot be un-committed.
    /// </summary>
    bool CanSign(string? requestedKeyId);
}

public interface ILicenseVerifier
{
    VerifiedLicense Verify(string signedLicenseJson);
}
