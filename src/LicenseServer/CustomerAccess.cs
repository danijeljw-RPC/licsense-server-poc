using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer;

internal sealed class CustomerAccessService(
    ApplicationDbContext db,
    ITransactionalEmailSender emailSender,
    TimeProvider clock,
    IWebHostEnvironment environment,
    IConfiguration configuration)
{
    internal const string GenericMessage = "If the supplied details match our records, a sign-in link will be sent shortly.";

    public async Task<CustomerAccessRequestResult> RequestAsync(
        string? email,
        string? licenseId,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (!CustomerEmails.TryNormalize(email, out var normalized, out _))
        {
            await ConstantWorkAsync(email ?? string.Empty, cancellationToken);
            return new CustomerAccessRequestResult(GenericMessage, null);
        }

        var identifierHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized!));
        var addressHash = SHA256.HashData(Encoding.UTF8.GetBytes(remoteAddress));
        var since = now.AddMinutes(-15);
        var recentForIdentifier = await db.CustomerAccessChallenges.CountAsync(
            item => item.CreatedAt >= since && item.IdentifierHash == identifierHash, cancellationToken);
        var recentForAddress = await db.CustomerAccessChallenges.CountAsync(
            item => item.CreatedAt >= since && item.RemoteAddressHash == addressHash, cancellationToken);
        if (recentForIdentifier >= 3 || recentForAddress >= 12)
        {
            await ConstantWorkAsync(normalized!, cancellationToken);
            return new CustomerAccessRequestResult(GenericMessage, null);
        }

        var customer = await db.Customers.AsNoTracking()
            .Where(item => item.NormalizedEmail == normalized)
            .Where(item => string.IsNullOrWhiteSpace(licenseId)
                           || item.Licenses.Any(license => license.LicenseId == licenseId))
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            await ConstantWorkAsync(normalized!, cancellationToken);
            return new CustomerAccessRequestResult(GenericMessage, null);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var challenge = new CustomerAccessChallenge
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, TokenHash = tokenHash,
            IdentifierHash = identifierHash, RemoteAddressHash = addressHash,
            CreatedAt = now, ExpiresAt = now.AddMinutes(12)
        };
        db.CustomerAccessChallenges.Add(challenge);
        var tokenFingerprint = Convert.ToHexStringLower(tokenHash);
        var publicBaseUrl = configuration["CustomerPortal:PublicBaseUrl"]?.TrimEnd('/')
            ?? (environment.IsDevelopment()
                ? "http://localhost:8080"
                : throw new InvalidOperationException("CustomerPortal:PublicBaseUrl is required outside Development."));
        await emailSender.QueueAsync(new TransactionalEmail(
                EmailTemplates.CustomerMagicLink, customer.Email,
                new Dictionary<string, string>
                {
                    ["actionUrl"] = $"{publicBaseUrl}/customer/access/consume?token={Uri.EscapeDataString(token)}"
                }),
            $"customer-magic-link:{challenge.Id:N}:{tokenFingerprint}", cancellationToken);
        return new CustomerAccessRequestResult(GenericMessage, environment.IsDevelopment() ? token : null);
    }

    public async Task<Guid?> ConsumeAsync(string? token, CancellationToken cancellationToken = default)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var challenge = await db.CustomerAccessChallenges
            .FromSqlInterpolated($"SELECT * FROM \"CustomerAccessChallenges\" WHERE \"TokenHash\" = {suppliedHash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= clock.GetUtcNow())
            return null;
        challenge.ConsumedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return challenge.CustomerId;
    }

    public async Task<IReadOnlyList<CustomerLicenseView>> GetLicensesAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = await ResolveNormalizedEmailAsync(customerId, cancellationToken);
        if (normalizedEmail is null) return [];
        var records = await db.Licenses.AsNoTracking()
            .Where(item => item.Customer.NormalizedEmail == normalizedEmail)
            .Include(item => item.Entitlements).Include(item => item.Activations)
            .AsSplitQuery()
            .OrderByDescending(item => item.IssuedAt).ThenBy(item => item.LicenseId)
            .ToListAsync(cancellationToken);
        return records.Select(Project).ToList();
    }

    public async Task<CustomerLicenseView?> GetLicenseAsync(
        Guid customerId,
        string licenseId,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = await ResolveNormalizedEmailAsync(customerId, cancellationToken);
        if (normalizedEmail is null) return null;
        var record = await db.Licenses.AsNoTracking()
            .Where(item => item.Customer.NormalizedEmail == normalizedEmail && item.LicenseId == licenseId)
            .Include(item => item.Entitlements).Include(item => item.Activations)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);
        return record is null ? null : Project(record);
    }

    // The same email can own several Customer rows (one per issuance), so the portal
    // session's anchor customer is resolved to its email and every record sharing that
    // email is in scope, rather than only the single Customer row the session was signed
    // in against.
    private Task<string?> ResolveNormalizedEmailAsync(Guid customerId, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking()
            .Where(item => item.Id == customerId)
            .Select(item => item.NormalizedEmail)
            .SingleOrDefaultAsync(cancellationToken);

    private static CustomerLicenseView Project(LicenseRecord record)
    {
        // record.Entitlements.Single() would throw for an imported multi-product license: an
        // import can legitimately create more than one Entitlement per LicenseRecord (see
        // "License import" in the key-ring design spec). The customer self-service portal has no
        // multi-product view yet, so summarize by the alphabetically-first product rather than
        // crash; the license's full entitlement set is still visible to an operator in the admin
        // portal, and preserved byte-for-byte in the stored artifact either way.
        var entitlement = record.Entitlements.OrderBy(x => x.Product, StringComparer.Ordinal).First();
        var activeActivations = record.Activations.Where(item => item.DeactivatedAt is null).ToList();
        var activation = activeActivations.FirstOrDefault();
        return new CustomerLicenseView(
            record.LicenseId,
            LicenseStore.GetLifecycleState(record, activation is not null, DateTimeOffset.UtcNow),
            entitlement.Product,
            entitlement.Edition,
            entitlement.Seats,
            activeActivations.Count,
            string.Equals(entitlement.LicenseType, "perpetual", StringComparison.Ordinal)
                ? null
                : record.ExpiresAt?.AddTicks(record.ExpirySubMicrosecondTicks),
            activation is null ? "not active" : "active",
            activation?.DeviceIdSuffix,
            null,
            null);
    }

    private static async Task ConstantWorkAsync(string value, CancellationToken cancellationToken)
    {
        _ = HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes("customer-access-dummy")), Encoding.UTF8.GetBytes(value));
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
    }
}

internal sealed record CustomerAccessRequestResult(string Message, string? DevelopmentToken);
internal sealed record CustomerLicenseView(
    string LicenseId, string Status, string Product, string Edition, int Seats, int ActiveSeatCount,
    DateTimeOffset? ExpiresAt, string ActivationStatus, string? DeviceSuffix,
    string? InvoiceUrl, string? RenewalUrl);
