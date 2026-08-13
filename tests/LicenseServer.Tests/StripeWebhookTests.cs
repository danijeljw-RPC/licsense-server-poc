using System.Net;
using System.Text;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class StripeWebhookTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "14")]
    public async Task InvalidSignatureAndMalformedSignedPayloadHaveNoSideEffects()
    {
        var marker = Guid.NewGuid().ToString("N");
        var validBody = EventJson($"evt_{marker}", "checkout.session.completed", $"cs_{marker}");
        var before = await CountsAsync();

        using var client = fixture.Factory.CreateClient();
        using (var invalid = Request(validBody, "t=1,v1=invalid"))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalid)).StatusCode);

        const string malformed = "{not-json";
        using (var invalidJson = Request(malformed, Signature(malformed)))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalidJson)).StatusCode);

        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    [Trait("ExpectedGreenStage", "14")]
    public async Task VerifiedDuplicateDeliveryCreatesOneProtectedInboxRow()
    {
        var marker = Guid.NewGuid().ToString("N");
        var eventId = $"evt_{marker}";
        var objectId = $"cs_{marker}";
        var body = EventJson(eventId, "checkout.session.completed", objectId);
        var signature = Signature(body);

        using var client = fixture.Factory.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = Request(body, signature);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .WebhookInbox.AsNoTracking().SingleAsync(item => item.ProviderEventId == eventId);
        Assert.Equal("pending", row.Status);
        Assert.Equal("checkout", row.Category);
        Assert.Equal(objectId, row.ProviderObjectId);
        Assert.DoesNotContain(eventId, row.ProtectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("payment_status", row.ProtectedPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "14")]
    public async Task ConcurrentDuplicateDeliveryIsDurablyIdempotent()
    {
        var marker = Guid.NewGuid().ToString("N");
        var eventId = $"evt_{marker}";
        var body = EventJson(eventId, "invoice.paid", $"in_{marker}");
        var signature = Signature(body);

        using var client = fixture.Factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var request = Request(body, signature);
            return await client.SendAsync(request);
        }));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in responses) response.Dispose();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .WebhookInbox.CountAsync(item => item.ProviderEventId == eventId));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "14")]
    public async Task WorkerCategorizesDelayedReorderedEventsAndRecoversExpiredLease()
    {
        var marker = Guid.NewGuid().ToString("N");
        var newerId = $"evt_new_{marker}";
        var olderId = $"evt_old_{marker}";
        var now = DateTimeOffset.UtcNow;
        using var client = fixture.Factory.CreateClient();
        await SendVerifiedAsync(client, EventJson(newerId, "customer.subscription.updated", $"sub_new_{marker}", now));
        await SendVerifiedAsync(client, EventJson(olderId, "invoice.payment_failed", $"in_old_{marker}", now.AddDays(-2)));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var older = await db.WebhookInbox.SingleAsync(item => item.ProviderEventId == olderId);
        older.Status = "leased";
        older.LeaseId = Guid.NewGuid();
        older.LeaseExpiresAt = now.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.True(await scope.ServiceProvider.GetRequiredService<BillingInboxProcessor>()
            .ProcessBatchAsync() >= 2);
        db.ChangeTracker.Clear();
        var rows = await db.WebhookInbox.AsNoTracking()
            .Where(item => item.ProviderEventId == newerId || item.ProviderEventId == olderId)
            .OrderBy(item => item.ProviderEventId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("categorized", row.Status));
        Assert.Equal("subscription", rows.Single(row => row.ProviderEventId == newerId).Category);
        Assert.Equal("payment_failure", rows.Single(row => row.ProviderEventId == olderId).Category);
        Assert.All(rows, row => Assert.Null(row.LeaseId));
    }

    private async Task<(int Inbox, int Audit, int Email, int Customer, int Order, int License)> CountsAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (
            await db.WebhookInbox.CountAsync(),
            await db.AuditRecords.CountAsync(),
            await db.EmailOutbox.CountAsync(),
            await db.Customers.CountAsync(),
            await db.LicenseOrders.CountAsync(),
            await db.Licenses.CountAsync());
    }

    private static async Task SendVerifiedAsync(HttpClient client, string body)
    {
        using var request = Request(body, Signature(body));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private static HttpRequestMessage Request(string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/stripe/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", signature);
        return request;
    }

    private static string Signature(string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = EventUtility.ComputeSignature(PostgresWebFixture.StripeWebhookSecret, timestamp, body);
        return $"t={timestamp},v1={signature}";
    }

    private static string EventJson(
        string eventId,
        string eventType,
        string objectId,
        DateTimeOffset? created = null) => $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2026-07-29.dahlia",
          "created": {{(created ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}},
          "data": {
            "object": {
              "id": "{{objectId}}",
              "object": "{{ObjectType(eventType)}}",
              "customer": "cus_{{eventId}}",
              "payment_status": "paid"
            }
          },
          "livemode": false,
          "pending_webhooks": 1,
          "request": { "id": null, "idempotency_key": null },
          "type": "{{eventType}}"
        }
        """;

    private static string ObjectType(string eventType) => eventType switch
    {
        "checkout.session.completed" => "checkout.session",
        "invoice.paid" or "invoice.payment_failed" => "invoice",
        _ when eventType.StartsWith("customer.subscription.", StringComparison.Ordinal) => "subscription",
        _ => "charge"
    };
}
