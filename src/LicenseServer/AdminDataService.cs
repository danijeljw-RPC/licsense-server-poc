using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer;

public sealed class AdminDataService(ApplicationDbContext db)
{
    public async Task<DashboardView> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var licenses = await db.Licenses.AsNoTracking()
            .Select(x => new { x.RevokedAt, x.ExpiresAt, Active = x.Activations.Any(a => a.DeactivatedAt == null) })
            .ToListAsync(cancellationToken);
        var active = await db.Activations.AsNoTracking().Where(x => x.DeactivatedAt == null).ToListAsync(cancellationToken);
        var audit = await db.AuditRecords.AsNoTracking().OrderByDescending(x => x.TimestampUtc).Take(8)
            .Select(x => new AuditView(x.Actor, x.Action, x.TargetType, x.TargetId, x.Result, x.TimestampUtc, x.ContextJson))
            .ToListAsync(cancellationToken);

        return new DashboardView(
            licenses.Count,
            licenses.Count(x => x.RevokedAt is null && (x.ExpiresAt is null || x.ExpiresAt > now) && !x.Active),
            licenses.Count(x => x.RevokedAt is null && (x.ExpiresAt is null || x.ExpiresAt > now) && x.Active),
            licenses.Count(x => x.ExpiresAt is not null && x.ExpiresAt <= now),
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
            query = query.Where(x => EF.Functions.ILike(x.LicenseId, $"%{term}%") || EF.Functions.ILike(x.Customer.Name, $"%{term}%"));
        }
        query = status?.ToLowerInvariant() switch
        {
            "available" => query.Where(x => x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now) && !x.Activations.Any(a => a.DeactivatedAt == null)),
            "active" => query.Where(x => x.RevokedAt == null && x.Activations.Any(a => a.DeactivatedAt == null)),
            "expired" => query.Where(x => x.ExpiresAt != null && x.ExpiresAt <= now),
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
                x.RevokedAt != null ? "revoked" : x.ExpiresAt != null && x.ExpiresAt <= now ? "expired" : x.Activations.Any(a => a.DeactivatedAt == null) ? "active" : "available",
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
        return new LicenseDetailView(
            x.LicenseId, x.Customer.Name, x.MetadataJson, x.IssuedAt, x.ExpiresAt, x.RevokedAt, x.RevocationReason,
            x.Entitlements.OrderBy(e => e.Product).Select(e => new EntitlementView(e.Product, e.Edition, e.LicenseType, e.Seats, e.UpdatesUntil)).ToList(),
            active is null ? null : new ActivationView(active.ActivationId, active.Mode, active.DeviceIdSuffix, active.DeviceName, active.ActivatedAt, active.RefreshAfter, active.LeaseExpiresAt),
            x.Activations.OrderByDescending(a => a.ActivatedAt).Select(a => new ActivationHistoryView(a.ActivationId, a.Mode, a.DeviceIdSuffix, a.ActivatedAt, a.DeactivatedAt)).ToList());
    }

    public async Task<string> CreateLicenseAsync(CreateLicenseInput input, string actor, CancellationToken cancellationToken = default)
    {
        var licenseId = input.LicenseId.Trim().ToUpperInvariant();
        if (await db.Licenses.AnyAsync(x => x.LicenseId == licenseId, cancellationToken))
            throw new InvalidOperationException("That license ID already exists.");
        var customerName = input.CustomerName.Trim();
        var customer = await db.Customers.FirstOrDefaultAsync(x => x.Name == customerName, cancellationToken)
            ?? new Customer { Id = Guid.NewGuid(), Name = customerName, CreatedAt = DateTimeOffset.UtcNow };
        var now = DateTimeOffset.UtcNow;
        db.Licenses.Add(new LicenseRecord
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Customer = customer,
            ActivationCodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ActivationCode)),
            MetadataJson = "{}", IssuedAt = now, ExpiresAt = input.ExpiresAt,
            Entitlements =
            [
                new Entitlement
                {
                    Id = Guid.NewGuid(), Product = input.Product.Trim().ToLowerInvariant(), Edition = input.Edition.Trim(),
                    LicenseType = input.LicenseType, Seats = input.Seats, UpdatesUntil = input.UpdatesUntil, License = null!
                }
            ]
        });
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor, Action = "license.issued", TargetType = "license", TargetId = licenseId,
            Result = "success", ContextJson = JsonSerializer.Serialize(new { customer = customerName, product = input.Product, seats = input.Seats }), TimestampUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return licenseId;
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
public sealed record LicenseDetailView(string LicenseId, string Customer, string MetadataJson, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string? RevocationReason, IReadOnlyList<EntitlementView> Entitlements, ActivationView? ActiveActivation, IReadOnlyList<ActivationHistoryView> IssuanceHistory);

public sealed class CreateLicenseInput
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(100, MinimumLength = 3)] public string LicenseId { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200)] public string CustomerName { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(100)] public string Product { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(100)] public string Edition { get; set; } = "professional";
    [System.ComponentModel.DataAnnotations.Required] public string LicenseType { get; set; } = "perpetual";
    [System.ComponentModel.DataAnnotations.Range(1, 100000)] public int Seats { get; set; } = 1;
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200, MinimumLength = 12)] public string ActivationCode { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateOnly? UpdatesUntil { get; set; }
}
