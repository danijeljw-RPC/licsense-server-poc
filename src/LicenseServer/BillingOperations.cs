using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer;

internal sealed record BillingEventView(
    Guid Id,
    string ProviderEventId,
    string EventType,
    string Category,
    string? ProviderObjectId,
    string Status,
    int AttemptCount,
    string? LastErrorCode,
    DateTimeOffset ProviderCreatedAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt);

internal sealed class BillingOperationsService(
    ApplicationDbContext db,
    PermissionGuard permissions,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<BillingEventView>> ListAsync(
        string? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.BillingManage);
        var query = db.WebhookInbox.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(item => item.Status == status);
        return await query.OrderByDescending(item => item.ReceivedAt).ThenByDescending(item => item.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Select(item => new BillingEventView(
                item.Id, item.ProviderEventId, item.EventType, item.Category, item.ProviderObjectId,
                item.Status, item.AttemptCount, item.LastErrorCode, item.ProviderCreatedAt,
                item.ReceivedAt, item.ProcessedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ReprocessAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.BillingManage);
        var row = await db.WebhookInbox.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null) return false;
        if (row.Status is not (BillingInboxStatus.Quarantined or BillingInboxStatus.DeadLetter
            or BillingInboxStatus.Categorized))
            throw new InvalidOperationException("Only categorized, quarantined, or dead-letter events can be reprocessed.");
        row.Status = BillingInboxStatus.Pending;
        row.AttemptCount = 0;
        row.NextAttemptAt = clock.GetUtcNow();
        row.LeaseId = null;
        row.LeaseExpiresAt = null;
        row.LastErrorCode = null;
        row.ProcessedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
