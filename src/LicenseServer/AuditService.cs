using System.Text.Json;
using LicenseServer.Data;

namespace LicenseServer;

public sealed class AuditService(ApplicationDbContext db)
{
    public async Task RecordAsync(string actor, string action, string targetType, string targetId, string result, object? context = null, CancellationToken cancellationToken = default)
    {
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "anonymous" : actor[..Math.Min(actor.Length, 256)],
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Result = result,
            ContextJson = JsonSerializer.Serialize(context ?? new { }),
            TimestampUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
