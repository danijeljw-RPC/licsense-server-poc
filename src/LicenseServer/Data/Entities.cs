namespace LicenseServer.Data;

public sealed class Customer
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public string? ExternalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<LicenseRecord> Licenses { get; set; } = [];
}

public sealed class LicenseIdCounter
{
    public DateOnly BusinessDate { get; set; }
    public int LastValue { get; set; }
}

public sealed class ProductDefinition
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Entitlement> Entitlements { get; set; } = [];
}

public sealed class LicenseRecord
{
    public Guid Id { get; set; }
    public required string LicenseId { get; set; }
    public Guid CustomerId { get; set; }
    public required Customer Customer { get; set; }
    public required byte[] ActivationCodeHash { get; set; }
    public string ActivationCodeHashVersion { get; set; } = ActivationCodeHasher.LegacySha256Version;
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int ExpirySubMicrosecondTicks { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReference { get; set; }
    public long Version { get; set; }
    public List<Entitlement> Entitlements { get; set; } = [];
    public List<Activation> Activations { get; set; } = [];
}

public sealed class IssuanceIdempotencyRecord
{
    public Guid Id { get; set; }
    public required string PrincipalId { get; set; }
    public required byte[] KeyHash { get; set; }
    public required byte[] RequestHash { get; set; }
    public required string ProtectedResult { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ApiCredential
{
    public Guid Id { get; set; }
    public required string PublicId { get; set; }
    public required string Name { get; set; }
    public required string OwnerUserId { get; set; }
    public required ApplicationUser OwnerUser { get; set; }
    public required byte[] SecretHash { get; set; }
    public required string HashVersion { get; set; }
    public required string LastFour { get; set; }
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public Guid? ReplacedByCredentialId { get; set; }
}

public sealed class EmailOutboxMessage
{
    public Guid Id { get; set; }
    public required string TemplateName { get; set; }
    public int TemplateVersion { get; set; }
    public required string RecipientHash { get; set; }
    public required string ProtectedPayload { get; set; }
    public required byte[] IdempotencyHash { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

public sealed class EmailDeliveryEvent
{
    public Guid Id { get; set; }
    public required string ProviderEventId { get; set; }
    public string? ProviderMessageId { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class CustomerAccessChallenge
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public required byte[] TokenHash { get; set; }
    public required byte[] IdentifierHash { get; set; }
    public required byte[] RemoteAddressHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class Entitlement
{
    public Guid Id { get; set; }
    public Guid LicenseRecordId { get; set; }
    public required LicenseRecord License { get; set; }
    public Guid ProductDefinitionId { get; set; }
    public required ProductDefinition ProductDefinition { get; set; }
    public required string Product { get; set; }
    public required string Edition { get; set; }
    public required string LicenseType { get; set; }
    public int Seats { get; set; }
    public DateOnly? UpdatesUntil { get; set; }
    public string MetadataJson { get; set; } = "{}";
}

public sealed class Activation
{
    public Guid Id { get; set; }
    public required string ActivationId { get; set; }
    public Guid LicenseRecordId { get; set; }
    public required LicenseRecord License { get; set; }
    public Guid RequestId { get; set; }
    public required string DeviceIdHash { get; set; }
    public required string DeviceIdSuffix { get; set; }
    public string? DeviceName { get; set; }
    public required string Mode { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset? RefreshAfter { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
}

public sealed class SigningKeyRecord
{
    public Guid Id { get; set; }
    public required string KeyId { get; set; }
    public required string Algorithm { get; set; }
    public required string PublicKeyPem { get; set; }
    public required string Provider { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class AuditRecord
{
    public long Id { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string Result { get; set; }
    public string ContextJson { get; set; } = "{}";
    public DateTimeOffset TimestampUtc { get; set; }
}
