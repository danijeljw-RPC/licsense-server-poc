using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using LicenseServer.Authorization;

namespace LicenseServer;

internal sealed class StripeProductMappingService(
    ApplicationDbContext db,
    TimeProvider clock,
    PermissionGuard permissions)
{
    public async Task<List<StripeProductMappingItem>> SearchAsync(
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.BillingManage);
        var query = db.StripeProductMappings.AsNoTracking().Include(item => item.ProductDefinition).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                EF.Functions.ILike(item.StripeProductId, $"%{term}%")
                || EF.Functions.ILike(item.ProductDefinition.Code, $"%{term}%"));
        }

        return await query.OrderBy(item => item.ProductDefinition.Code).ThenBy(item => item.StripeProductId)
            .Select(item => ToItem(item)).ToListAsync(cancellationToken);
    }

    public async Task<StripeProductMappingItem> CreateAsync(
        CreateStripeProductMappingRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.BillingManage);
        var stripeProductId = request.StripeProductId?.Trim();
        if (string.IsNullOrWhiteSpace(stripeProductId) || stripeProductId.Length > 255)
            throw new InvalidOperationException("A Stripe product ID is required and cannot exceed 255 characters.");
        if (request.ProductDefinitionId is null || request.ProductDefinitionId == Guid.Empty)
            throw new InvalidOperationException("A product is required.");
        var product = await db.ProductDefinitions.SingleOrDefaultAsync(
            item => item.Id == request.ProductDefinitionId.Value, cancellationToken)
            ?? throw new InvalidOperationException("The selected product does not exist.");
        if (await db.StripeProductMappings.AnyAsync(item => item.StripeProductId == stripeProductId, cancellationToken))
            throw new InvalidOperationException("A mapping for this Stripe product ID already exists.");
        ValidateOneTimeTerms(request.Edition, request.LicenseType, request.Seats);

        var now = clock.GetUtcNow();
        var mapping = new StripeProductMapping
        {
            Id = Guid.NewGuid(),
            StripeProductId = stripeProductId,
            ProductDefinitionId = product.Id,
            ProductDefinition = product,
            Edition = NormalizeEdition(request.Edition),
            LicenseType = NormalizeLicenseType(request.LicenseType),
            Seats = request.Seats,
            UpdatesUntil = request.UpdatesUntil,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.StripeProductMappings.Add(mapping);
        AddAudit(actor, "stripe-product-mapping.created", mapping, new
        {
            mapping.StripeProductId, productId = product.Id, mapping.Edition, mapping.LicenseType, mapping.Seats
        }, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToItem(mapping);
    }

    public async Task UpdateAsync(
        Guid id,
        UpdateStripeProductMappingRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.BillingManage);
        var mapping = await db.StripeProductMappings.Include(item => item.ProductDefinition)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Stripe product mapping was not found.");
        var product = mapping.ProductDefinition;
        if (request.ProductDefinitionId is { } productId && productId != mapping.ProductDefinitionId)
        {
            product = await db.ProductDefinitions.SingleOrDefaultAsync(item => item.Id == productId, cancellationToken)
                ?? throw new InvalidOperationException("The selected product does not exist.");
        }
        ValidateOneTimeTerms(request.Edition, request.LicenseType, request.Seats);

        var old = new { mapping.Edition, mapping.LicenseType, mapping.Seats, mapping.UpdatesUntil, mapping.ExpiresAt };
        mapping.ProductDefinitionId = product.Id;
        mapping.ProductDefinition = product;
        mapping.Edition = NormalizeEdition(request.Edition);
        mapping.LicenseType = NormalizeLicenseType(request.LicenseType);
        mapping.Seats = request.Seats;
        mapping.UpdatesUntil = request.UpdatesUntil;
        mapping.ExpiresAt = request.ExpiresAt;
        mapping.UpdatedAt = clock.GetUtcNow();
        AddAudit(actor, "stripe-product-mapping.updated", mapping, new
        {
            old,
            @new = new { mapping.Edition, mapping.LicenseType, mapping.Seats, mapping.UpdatesUntil, mapping.ExpiresAt }
        }, mapping.UpdatedAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.BillingManage);
        var mapping = await db.StripeProductMappings.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Stripe product mapping was not found.");
        db.StripeProductMappings.Remove(mapping);
        AddAudit(actor, "stripe-product-mapping.deleted", mapping, new { mapping.StripeProductId }, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateOneTimeTerms(string? edition, string? licenseType, int? seats)
    {
        var anyProvided = edition is not null || licenseType is not null || seats is not null;
        var allProvided = edition is not null && licenseType is not null && seats is not null;
        if (anyProvided && !allProvided)
            throw new InvalidOperationException(
                "Edition, license type, and seats must all be provided together, or all left blank.");
        if (!allProvided) return;
        if (!LicenseEditions.IsSupported(NormalizeEdition(edition)))
            throw new InvalidOperationException("Edition is not an approved controlled value.");
        if (!LicenseTerms.IsSupportedType(NormalizeLicenseType(licenseType)))
            throw new InvalidOperationException("License type must be exactly 'perpetual', 'subscription', or 'evaluation'.");
        if (seats <= 0)
            throw new InvalidOperationException("Seats must be greater than zero.");
    }

    private static string? NormalizeEdition(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeLicenseType(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private void AddAudit(string actor, string action, StripeProductMapping mapping, object context, DateTimeOffset now) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor[..Math.Min(actor.Length, 256)],
            Action = action, TargetType = "stripe_product_mapping", TargetId = mapping.Id.ToString("D"), Result = "success",
            ContextJson = System.Text.Json.JsonSerializer.Serialize(context), TimestampUtc = now
        });

    private static StripeProductMappingItem ToItem(StripeProductMapping mapping) => new(
        mapping.Id, mapping.StripeProductId, mapping.ProductDefinitionId, mapping.ProductDefinition.Code,
        mapping.ProductDefinition.DisplayName, mapping.Edition, mapping.LicenseType, mapping.Seats,
        mapping.UpdatesUntil, mapping.ExpiresAt, mapping.CreatedAt, mapping.UpdatedAt);
}

internal sealed record StripeProductMappingItem(
    Guid Id,
    string StripeProductId,
    Guid ProductDefinitionId,
    string ProductCode,
    string ProductDisplayName,
    string? Edition,
    string? LicenseType,
    int? Seats,
    DateOnly? UpdatesUntil,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
