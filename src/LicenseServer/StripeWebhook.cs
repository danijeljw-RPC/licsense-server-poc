using System.Text.Json;
using LicenseServer.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace LicenseServer;

internal static class BillingInboxStatus
{
    public const string Pending = "pending";
    public const string Leased = "leased";
    public const string Retry = "retry";
    public const string Categorized = "categorized";
    public const string Completed = "completed";
    public const string Ignored = "ignored";
    public const string Quarantined = "quarantined";
    public const string DeadLetter = "dead_letter";
}

internal sealed class StripeOptions
{
    public const string SupportedApiVersion = "2026-07-29.dahlia";
    public string? ApiKey { get; set; }
    public string? WebhookSecret { get; set; }
    public string ApiVersion { get; set; } = SupportedApiVersion;
}

internal sealed class BillingWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 10;
    public int MaxAttempts { get; set; } = 8;
    public int LeaseSeconds { get; set; } = 120;
}

internal sealed record StripeWebhookReceiveResult(bool Accepted, bool Duplicate, string? ErrorCode = null);

internal sealed class StripeWebhookReceiver(
    ApplicationDbContext db,
    IDataProtectionProvider protection,
    IOptions<StripeOptions> options,
    TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("LicenseServer.StripeWebhook.v1");

    public async Task<StripeWebhookReceiveResult> ReceiveAsync(
        string rawBody,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(options.Value.WebhookSecret))
            return new(false, false, "invalid_signature");

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                rawBody,
                signature,
                options.Value.WebhookSecret,
                tolerance: 300,
                throwOnApiVersionMismatch: false);
        }
        catch (Exception exception) when (exception is StripeException or JsonException or ArgumentException)
        {
            return new(false, false, "invalid_signature_or_payload");
        }

        if (string.IsNullOrWhiteSpace(stripeEvent.Id) || string.IsNullOrWhiteSpace(stripeEvent.Type))
            return new(false, false, "malformed_event");

        string? objectId;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            objectId = document.RootElement.GetProperty("data").GetProperty("object")
                .TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(false, false, "malformed_event");
        }

        var now = clock.GetUtcNow();
        var row = new WebhookInbox
        {
            Id = Guid.NewGuid(),
            Provider = "stripe",
            ProviderEventId = stripeEvent.Id,
            EventType = stripeEvent.Type,
            Category = StripeEventCategories.For(stripeEvent.Type),
            ProviderObjectId = Bound(objectId, 255),
            ProtectedPayload = protector.Protect(rawBody),
            Status = BillingInboxStatus.Pending,
            NextAttemptAt = now,
            ProviderCreatedAt = stripeEvent.Created,
            ReceivedAt = now
        };
        db.WebhookInbox.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(true, false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.WebhookInbox.AsNoTracking().AnyAsync(
                    item => item.ProviderEventId == stripeEvent.Id, cancellationToken))
                return new(true, true);
            throw;
        }
    }

    internal string Unprotect(WebhookInbox row) => protector.Unprotect(row.ProtectedPayload);

    private static string? Bound(string? value, int length) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Length <= length ? value : value[..length];
}

internal static class StripeEventCategories
{
    public static string For(string eventType) => eventType switch
    {
        "checkout.session.completed" => "checkout",
        "invoice.paid" => "renewal",
        "invoice.payment_failed" => "payment_failure",
        "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted" => "subscription",
        "charge.refunded" => "refund",
        "charge.dispute.created" or "charge.dispute.closed" => "dispute",
        _ => "unsupported"
    };
}

internal sealed record BillingEventProcessResult(string Status, string? ErrorCode = null)
{
    public static BillingEventProcessResult Categorized() => new(BillingInboxStatus.Categorized);
    public static BillingEventProcessResult Ignored() => new(BillingInboxStatus.Ignored);
}

internal interface IBillingEventProcessor
{
    Task<BillingEventProcessResult> ProcessAsync(WebhookInbox row, CancellationToken cancellationToken = default);
}

internal sealed class CategorizingBillingEventProcessor : IBillingEventProcessor
{
    public Task<BillingEventProcessResult> ProcessAsync(
        WebhookInbox row,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(row.Category == "unsupported"
            ? BillingEventProcessResult.Ignored()
            : BillingEventProcessResult.Categorized());
}

internal sealed class BillingInboxProcessor(
    ApplicationDbContext db,
    IBillingEventProcessor processor,
    IOptions<BillingWorkerOptions> options,
    TimeProvider clock)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        List<WebhookInbox> batch;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            batch = await db.WebhookInbox.FromSqlInterpolated($"""
                SELECT * FROM "WebhookInbox"
                WHERE (("Status" IN ('pending', 'retry') AND "NextAttemptAt" <= {now})
                    OR ("Status" = 'leased' AND "LeaseExpiresAt" <= {now}))
                ORDER BY "NextAttemptAt", "ReceivedAt"
                FOR UPDATE SKIP LOCKED
                LIMIT {Math.Clamp(options.Value.BatchSize, 1, 100)}
                """).ToListAsync(cancellationToken);
            foreach (var row in batch)
            {
                row.Status = BillingInboxStatus.Leased;
                row.LeaseId = leaseId;
                row.LeaseExpiresAt = now.AddSeconds(Math.Clamp(options.Value.LeaseSeconds, 30, 900));
                row.AttemptCount++;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var row in batch)
        {
            try
            {
                var result = await processor.ProcessAsync(row, cancellationToken);
                row.Status = result.Status;
                row.LastErrorCode = result.ErrorCode;
                row.ProcessedAt = result.Status is BillingInboxStatus.Categorized or BillingInboxStatus.Completed or BillingInboxStatus.Ignored
                    ? clock.GetUtcNow()
                    : null;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                var maxAttempts = Math.Clamp(options.Value.MaxAttempts, 1, 20);
                row.Status = row.AttemptCount >= maxAttempts ? BillingInboxStatus.DeadLetter : BillingInboxStatus.Retry;
                row.LastErrorCode = "processor_failure";
                row.NextAttemptAt = clock.GetUtcNow() + BillingRetrySchedule.Delay(row.AttemptCount);
            }
            row.LeaseId = null;
            row.LeaseExpiresAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        return batch.Count;
    }
}

internal static class BillingRetrySchedule
{
    public static TimeSpan Delay(int attempt) =>
        TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, Math.Clamp(attempt, 1, 10))));
}

internal sealed partial class BillingInboxWorker(
    IServiceScopeFactory scopes,
    IOptions<BillingWorkerOptions> options,
    ILogger<BillingInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<BillingInboxProcessor>()
                    .ProcessBatchAsync(stoppingToken);
                await Task.Delay(processed == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LogIterationFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    [LoggerMessage(1401, LogLevel.Error, "Stripe billing inbox iteration failed without logging request data or secrets.")]
    private static partial void LogIterationFailure(ILogger logger, Exception exception);
}
