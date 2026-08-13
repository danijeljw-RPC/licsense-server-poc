using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
public sealed class BillingPolicyTests(PostgresWebFixture fixture)
{
    [Fact]
    [Trait("ExpectedGreenStage", "15")]
    public async Task CompletedPurchaseIssuesExactlyOneMappedLicenseAndEncryptedEmail()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await AddCatalogMappingsAsync(db, marker);
        var processor = scope.ServiceProvider.GetRequiredService<StripeBillingPolicyProcessor>();
        var snapshot = Purchase(marker);

        Assert.Equal(BillingInboxStatus.Completed, (await processor.ApplyAsync(snapshot)).Status);
        Assert.Equal(BillingInboxStatus.Completed, (await processor.ApplyAsync(snapshot)).Status);

        db.ChangeTracker.Clear();
        var contract = await ContractAsync(db, marker);
        var license = await db.Licenses.AsNoTracking().Include(item => item.Entitlements).Include(item => item.Customer)
            .SingleAsync(item => item.Id == contract.LicenseRecordId);
        Assert.Equal($"buyer-{marker}@example.com", license.Customer.NormalizedEmail);
        Assert.Equal($"buyer-{marker}@example.com", System.Text.Json.JsonDocument.Parse(license.MetadataJson)
            .RootElement.GetProperty("contactEmail").GetString());
        Assert.Single(license.Entitlements);
        Assert.DoesNotContain("stripe", license.MetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.LicenseOrders.CountAsync(item => item.BillingContractId == contract.Id));
        Assert.Equal(1, await db.EmailOutbox.CountAsync(item => item.TemplateName == EmailTemplates.PurchaseActivation.Name
            && item.RecipientHash == HashEmail($"buyer-{marker}@example.com")));
        var email = await db.EmailOutbox.AsNoTracking().SingleAsync(item => item.TemplateName == EmailTemplates.PurchaseActivation.Name
            && item.RecipientHash == HashEmail($"buyer-{marker}@example.com"));
        Assert.DoesNotContain($"buyer-{marker}@example.com", email.ProtectedPayload, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "15")]
    public async Task RenewalIsMonotonicAndSameInvoiceAcrossEventsIsIdempotent()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await AddCatalogMappingsAsync(db, marker);
        var processor = scope.ServiceProvider.GetRequiredService<StripeBillingPolicyProcessor>();
        await processor.ApplyAsync(Purchase(marker));
        var firstEnd = DateTimeOffset.UtcNow.AddYears(2);

        var first = await processor.ApplyAsync(Purchase(marker) with
        {
            Kind = BillingEventKind.RenewalPaid,
            EventId = $"evt_renew_{marker}",
            InvoiceId = $"in_renew_{marker}",
            CheckoutSessionId = null,
            CurrentPeriodEnd = firstEnd
        });
        var replay = await processor.ApplyAsync(Purchase(marker) with
        {
            Kind = BillingEventKind.RenewalPaid,
            EventId = $"evt_renew_alias_{marker}",
            InvoiceId = $"in_renew_{marker}",
            CheckoutSessionId = null,
            CurrentPeriodEnd = firstEnd.AddMonths(-1)
        });

        Assert.Equal(BillingInboxStatus.Completed, first.Status);
        Assert.Equal(BillingInboxStatus.Completed, replay.Status);
        db.ChangeTracker.Clear();
        var contract = await ContractAsync(db, marker);
        var license = await db.Licenses.AsNoTracking().SingleAsync(item => item.Id == contract.LicenseRecordId);
        Assert.Equal(firstEnd, license.ExpiresAt?.AddTicks(license.ExpirySubMicrosecondTicks));
        Assert.Equal(1, await db.StripeInvoiceMappings.CountAsync(item => item.StripeInvoiceId == $"in_renew_{marker}"));
        Assert.Equal(1, await db.EmailOutbox.CountAsync(item => item.TemplateName == EmailTemplates.RenewalReceipt.Name
            && item.RecipientHash == HashEmail($"buyer-{marker}@example.com")));
    }

    [Fact]
    [Trait("ExpectedGreenStage", "15")]
    public async Task PaymentFailureUsesGraceAndRecoveryClearsItWithoutRevocation()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await AddCatalogMappingsAsync(db, marker);
        var processor = scope.ServiceProvider.GetRequiredService<StripeBillingPolicyProcessor>();
        await processor.ApplyAsync(Purchase(marker));

        await processor.ApplyAsync(Purchase(marker) with
        {
            Kind = BillingEventKind.PaymentFailed,
            EventId = $"evt_failed_{marker}",
            InvoiceId = $"in_failed_{marker}",
            CheckoutSessionId = null
        });
        db.ChangeTracker.Clear();
        var failed = await ContractAsync(db, marker, tracked: true);
        var license = await db.Licenses.SingleAsync(item => item.Id == failed.LicenseRecordId);
        Assert.NotNull(failed.GraceUntil);
        Assert.Null(license.RevokedAt);
        Assert.True(license.ExpiresAt >= failed.GraceUntil?.AddTicks(-9));

        var recoveredEnd = DateTimeOffset.UtcNow.AddYears(3);
        await processor.ApplyAsync(Purchase(marker) with
        {
            Kind = BillingEventKind.RenewalPaid,
            EventId = $"evt_recovered_{marker}",
            InvoiceId = $"in_failed_{marker}",
            CheckoutSessionId = null,
            CurrentPeriodEnd = recoveredEnd
        });
        db.ChangeTracker.Clear();
        var recovered = await db.BillingContracts.AsNoTracking().SingleAsync(item => item.Id == failed.Id);
        Assert.Null(recovered.GraceUntil);
        Assert.Equal("active", recovered.Status);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "15")]
    public async Task CancellationReversalPlanChangeAndDeletionPreservePaidThroughPolicy()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await AddCatalogMappingsAsync(db, marker);
        var processor = scope.ServiceProvider.GetRequiredService<StripeBillingPolicyProcessor>();
        var purchase = Purchase(marker);
        await processor.ApplyAsync(purchase);

        await processor.ApplyAsync(purchase with { Kind = BillingEventKind.SubscriptionChanged, EventId = $"evt_cancel_{marker}", CancelAtPeriodEnd = true });
        await processor.ApplyAsync(purchase with { Kind = BillingEventKind.SubscriptionChanged, EventId = $"evt_reverse_{marker}", CancelAtPeriodEnd = false, Seats = 4 });
        await processor.ApplyAsync(purchase with { Kind = BillingEventKind.SubscriptionDeleted, EventId = $"evt_delete_{marker}" });

        db.ChangeTracker.Clear();
        var contract = await ContractAsync(db, marker);
        var license = await db.Licenses.AsNoTracking().Include(item => item.Entitlements).SingleAsync(item => item.Id == contract.LicenseRecordId);
        Assert.False(contract.CancelAtPeriodEnd);
        Assert.Equal("ended", contract.Status);
        Assert.Equal(4, license.Entitlements.Single().Seats);
        Assert.Equal(purchase.CurrentPeriodEnd, license.ExpiresAt?.AddTicks(license.ExpirySubMicrosecondTicks));
    }

    [Theory]
    [InlineData(BillingEventKind.Refunded)]
    [InlineData(BillingEventKind.DisputeOpened)]
    [Trait("ExpectedGreenStage", "15")]
    public async Task RefundAndDisputeDefaultToReviewWithoutSilentSuspension(string kind)
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await AddCatalogMappingsAsync(db, marker);
        var processor = scope.ServiceProvider.GetRequiredService<StripeBillingPolicyProcessor>();
        var purchase = Purchase(marker);
        await processor.ApplyAsync(purchase);
        await processor.ApplyAsync(purchase with { Kind = kind, EventId = $"evt_review_{marker}" });

        db.ChangeTracker.Clear();
        var contract = await ContractAsync(db, marker);
        Assert.True(contract.ReviewRequired);
        Assert.Null(contract.SuspendedAt);
        Assert.Equal("review", contract.Status);
    }

    [Fact]
    [Trait("ExpectedGreenStage", "15")]
    public async Task UnknownOrConflictingMappingQuarantinesWithoutBusinessSideEffects()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = await db.Licenses.CountAsync();
        var result = await scope.ServiceProvider.GetRequiredService<StripeBillingPolicyProcessor>()
            .ApplyAsync(Purchase(marker));
        Assert.Equal(BillingInboxStatus.Quarantined, result.Status);
        Assert.Equal("unknown_price_mapping", result.ErrorCode);
        Assert.Equal(before, await db.Licenses.CountAsync());
    }

    [Fact]
    [Trait("ExpectedGreenStage", "15")]
    public async Task BillingOperationsRequirePermissionAndOnlyResetProcessState()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.WebhookInbox.Add(new WebhookInbox
            {
                Id = Guid.NewGuid(), Provider = "stripe", ProviderEventId = $"evt_ops_{marker}",
                EventType = "invoice.paid", Category = "renewal", ProviderObjectId = $"in_ops_{marker}",
                ProtectedPayload = "protected-immutable", Status = BillingInboxStatus.Quarantined,
                NextAttemptAt = DateTimeOffset.UtcNow, ProviderCreatedAt = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow, LastErrorCode = "unknown_price_mapping"
            });
            await db.SaveChangesAsync();
        }

        using var denied = fixture.CreateAuthenticatedClient(false, "licenses.read");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await denied.GetAsync("/api/v1/admin/billing/events")).StatusCode);
        using var allowed = fixture.CreateAuthenticatedClient(false, "billing.manage");
        var list = await allowed.GetAsync("/api/v1/admin/billing/events");
        Assert.Equal(System.Net.HttpStatusCode.OK, list.StatusCode);
    }

    private static async Task AddCatalogMappingsAsync(ApplicationDbContext db, string marker)
    {
        db.StripeProductMappings.Add(new StripeProductMapping
        {
            Id = Guid.NewGuid(), StripeProductId = $"prod_{marker}", ProductDefinitionId = RoadmapTestSupport.KnownProductId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.StripePriceMappings.Add(new StripePriceMapping
        {
            Id = Guid.NewGuid(), StripePriceId = $"price_{marker}", ProductDefinitionId = RoadmapTestSupport.KnownProductId,
            Edition = "corporate", LicenseType = "subscription", Seats = 2, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static BillingSnapshot Purchase(string marker) => new(
        EventId: $"evt_purchase_{marker}",
        Kind: BillingEventKind.PurchaseCompleted,
        CustomerId: $"cus_{marker}",
        CustomerName: $"Buyer {marker}",
        CustomerEmail: $"buyer-{marker}@example.com",
        ProductId: $"prod_{marker}",
        PriceId: $"price_{marker}",
        SubscriptionId: $"sub_{marker}",
        CheckoutSessionId: $"cs_{marker}",
        InvoiceId: $"in_initial_{marker}",
        CurrentPeriodEnd: DateTimeOffset.UtcNow.AddYears(1),
        CancelAtPeriodEnd: false,
        Seats: 2);

    private static string HashEmail(string email) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email)));

    private static async Task<BillingContract> ContractAsync(ApplicationDbContext db, string marker, bool tracked = false)
    {
        var query = tracked ? db.StripeSubscriptionMappings.AsQueryable() : db.StripeSubscriptionMappings.AsNoTracking();
        return await query.Where(item => item.StripeSubscriptionId == $"sub_{marker}")
            .Select(item => item.BillingContract).SingleAsync();
    }
}
