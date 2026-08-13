using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer;

internal sealed class AdminDataService(ApplicationDbContext db, LicenseStore store)
{
    public async Task<DashboardView> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var licenses = await db.Licenses.AsNoTracking()
            .Select(x => new { x.CancelledAt, x.RevokedAt, x.ExpiresAt, Active = x.Activations.Any(a => a.DeactivatedAt == null) })
            .ToListAsync(cancellationToken);
        var active = await db.Activations.AsNoTracking().Where(x => x.DeactivatedAt == null).ToListAsync(cancellationToken);
        var audit = await db.AuditRecords.AsNoTracking().OrderByDescending(x => x.TimestampUtc).Take(8)
            .Select(x => new AuditView(x.Actor, x.Action, x.TargetType, x.TargetId, x.Result, x.TimestampUtc, x.ContextJson))
            .ToListAsync(cancellationToken);

        return new DashboardView(
            licenses.Count,
            licenses.Count(x => x.CancelledAt is null && x.RevokedAt is null && (x.ExpiresAt is null || x.ExpiresAt > now) && !x.Active),
            licenses.Count(x => x.CancelledAt is null && x.RevokedAt is null && (x.ExpiresAt is null || x.ExpiresAt > now) && x.Active),
            licenses.Count(x => x.CancelledAt is null && x.RevokedAt is null && x.ExpiresAt is not null && x.ExpiresAt <= now),
            licenses.Count(x => x.RevokedAt is not null),
            active.Count(x => x.Mode == "online"),
            active.Count(x => x.Mode == "offline"),
            active.Count(x => x.LeaseExpiresAt > now && x.LeaseExpiresAt <= now.AddDays(2)),
            audit);
    }

    public async Task<PagedLicenses> SearchLicensesAsync(
        string? search,
        string? status,
        string? sort,
        int page,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var query = db.Licenses.AsNoTracking().Include(x => x.Customer).Include(x => x.Activations).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var normalizedTerm = term.ToLowerInvariant();
            query = query.Where(x =>
                EF.Functions.ILike(x.LicenseId, $"%{term}%")
                || EF.Functions.ILike(x.Customer.Name, $"%{term}%")
                || x.Customer.NormalizedEmail.Contains(normalizedTerm));
        }
        query = status?.ToLowerInvariant() switch
        {
            "cancelled" => query.Where(x => x.CancelledAt != null),
            "available" => query.Where(x => x.CancelledAt == null && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now) && !x.Activations.Any(a => a.DeactivatedAt == null)),
            "active" => query.Where(x => x.CancelledAt == null && x.RevokedAt == null && x.Activations.Any(a => a.DeactivatedAt == null)),
            "expired" => query.Where(x => x.CancelledAt == null && x.RevokedAt == null && x.ExpiresAt != null && x.ExpiresAt <= now),
            "revoked" => query.Where(x => x.RevokedAt != null),
            _ => query
        };
        query = sort?.ToLowerInvariant() switch
        {
            "customer" => query.OrderBy(x => x.Customer.Name).ThenBy(x => x.LicenseId),
            "oldest" => query.OrderBy(x => x.IssuedAt),
            _ => query.OrderByDescending(x => x.IssuedAt)
        };
        var count = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new LicenseListView(
                x.LicenseId,
                x.Customer.Name,
                x.CancelledAt != null ? "cancelled" : x.RevokedAt != null ? "revoked" : x.ExpiresAt != null && x.ExpiresAt <= now ? "expired" : x.Activations.Any(a => a.DeactivatedAt == null) ? "active" : "available",
                x.IssuedAt,
                x.ExpiresAt,
                x.Activations.Where(a => a.DeactivatedAt == null).Select(a => a.DeviceIdSuffix).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return new PagedLicenses(rows, page, pageSize, count);
    }

    public async Task<LicenseDetailView?> GetLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        var x = await db.Licenses.AsNoTracking()
            .Include(x => x.Customer).Include(x => x.Entitlements).Include(x => x.Activations)
            .SingleOrDefaultAsync(x => x.LicenseId == licenseId, cancellationToken);
        if (x is null) return null;
        var active = x.Activations.SingleOrDefault(a => a.DeactivatedAt == null);
        var activationIds = x.Activations.Select(y => y.ActivationId).ToArray();
        var history = await db.AuditRecords.AsNoTracking()
            .Where(a => a.TargetId == licenseId || (a.TargetType == "activation" && activationIds.Contains(a.TargetId)))
            .OrderByDescending(a => a.TimestampUtc)
            .Select(a => new AuditView(a.Actor, a.Action, a.TargetType, a.TargetId, a.Result, a.TimestampUtc, a.ContextJson))
            .ToListAsync(cancellationToken);
        return new LicenseDetailView(
            x.LicenseId, x.Customer.Name, x.Customer.Email,
            ContactEmail(x.MetadataJson) ?? throw new InvalidOperationException("Stored license contact metadata is invalid."),
            x.IssuedAt,
            x.ExpiresAt?.AddTicks(x.ExpirySubMicrosecondTicks),
            x.CancelledAt, x.CancellationReason, x.CancelledBy, x.RevokedAt, x.RevocationReason, x.Version,
            LicenseStore.GetLifecycleState(x, active is not null, DateTimeOffset.UtcNow),
            x.Entitlements.OrderBy(e => e.Product).Select(e => new EntitlementView(e.Product, e.Edition, e.LicenseType, e.Seats, e.UpdatesUntil)).ToList(),
            active is null ? null : new ActivationView(active.ActivationId, active.Mode, active.DeviceIdSuffix, active.DeviceName, active.ActivatedAt, active.RefreshAfter, active.LeaseExpiresAt),
            x.Activations.OrderByDescending(a => a.ActivatedAt).Select(a => new ActivationHistoryView(a.ActivationId, a.Mode, a.DeviceIdSuffix, a.ActivatedAt, a.DeactivatedAt)).ToList(),
            history);
    }

    private static string? ContactEmail(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("contactEmail", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<LicenseStore.IssuedLicense> CreateLicenseAsync(
        CreateLicenseInput input,
        string actor,
        string principalId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var result = await store.IssueAsync(new IssueLicenseRequest(
            input.CustomerName, input.CustomerEmail, input.Product, input.Edition, input.LicenseType,
            input.ExpiresAt, input.Seats, input.UpdatesUntil, null),
            new IssuanceContext(actor, principalId, Guid.NewGuid().ToString("D"), idempotencyKey),
            cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        return result.Value!;
    }

    public Task<List<AuditView>> GetAuditAsync(int take = 200, CancellationToken cancellationToken = default) =>
        db.AuditRecords.AsNoTracking().OrderByDescending(x => x.TimestampUtc).Take(Math.Clamp(take, 1, 500))
            .Select(x => new AuditView(x.Actor, x.Action, x.TargetType, x.TargetId, x.Result, x.TimestampUtc, x.ContextJson))
            .ToListAsync(cancellationToken);
}

public sealed record DashboardView(int Total, int Available, int Active, int Expired, int Revoked, int Online, int Offline, int LeasesApproachingExpiry, IReadOnlyList<AuditView> RecentAudit);
public sealed record AuditView(string Actor, string Action, string TargetType, string TargetId, string Result, DateTimeOffset TimestampUtc, string ContextJson);
public sealed record LicenseListView(string LicenseId, string Customer, string Status, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, string? DeviceSuffix);
public sealed record PagedLicenses(IReadOnlyList<LicenseListView> Items, int Page, int PageSize, int Total) { public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)Total / PageSize)); }
public sealed record EntitlementView(string Product, string Edition, string LicenseType, int Seats, DateOnly? UpdatesUntil);
public sealed record ActivationView(string ActivationId, string Mode, string DeviceSuffix, string? DeviceName, DateTimeOffset ActivatedAt, DateTimeOffset? RefreshAfter, DateTimeOffset? LeaseExpiresAt);
public sealed record ActivationHistoryView(string ActivationId, string Mode, string DeviceSuffix, DateTimeOffset ActivatedAt, DateTimeOffset? DeactivatedAt);
public sealed record LicenseDetailView(
    string LicenseId, string Customer, string CustomerEmail, string SignedContactEmail, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? CancelledAt, string? CancellationReason, string? CancelledBy,
    DateTimeOffset? RevokedAt, string? RevocationReason, long Version, string Status,
    IReadOnlyList<EntitlementView> Entitlements, ActivationView? ActiveActivation,
    IReadOnlyList<ActivationHistoryView> IssuanceHistory, IReadOnlyList<AuditView> History);

public sealed class CreateLicenseInput
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200)] public string CustomerName { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.EmailAddress, System.ComponentModel.DataAnnotations.StringLength(320)] public string CustomerEmail { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(100)] public string Product { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(100)] public string Edition { get; set; } = "business";
    [System.ComponentModel.DataAnnotations.Required] public string LicenseType { get; set; } = "perpetual";
    [System.ComponentModel.DataAnnotations.Range(1, 100000)] public int Seats { get; set; } = 1;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateOnly? UpdatesUntil { get; set; }
}
