using System.Text.Json;
using System.Text.RegularExpressions;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using LicenseServer.Authorization;

namespace LicenseServer;

internal static partial class LicenseEditions
{
    public static readonly IReadOnlyList<string> All =
    [
        "community", "project", "education", "consumer",
        "business", "smb", "enterprise", "corporate"
    ];

    public static bool IsSupported(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

internal sealed partial class ProductCatalogService(
    ApplicationDbContext db,
    TimeProvider clock,
    PermissionGuard permissions)
{
    public async Task<List<ProductCatalogItem>> SearchAsync(
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.ProductsRead);
        var query = db.ProductDefinitions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                EF.Functions.ILike(item.Code, $"%{term}%")
                || EF.Functions.ILike(item.DisplayName, $"%{term}%"));
        }

        return await query.OrderBy(item => item.DisplayName)
            .Select(item => new ProductCatalogItem(
                item.Id, item.Code, item.DisplayName, item.Description, item.IsActive,
                item.Entitlements.Count, item.CreatedAt, item.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ProductCatalogItem>> ActiveAsync(CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.ProductsRead);
        return await db.ProductDefinitions.AsNoTracking().Where(item => item.IsActive)
            .OrderBy(item => item.DisplayName)
            .Select(item => new ProductCatalogItem(
                item.Id, item.Code, item.DisplayName, item.Description, item.IsActive,
                item.Entitlements.Count, item.CreatedAt, item.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductCatalogItem> CreateAsync(
        string? code,
        string? displayName,
        string? description,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.ProductsManage);
        var stableCode = code?.Trim();
        var name = displayName?.Trim();
        if (stableCode is null || !ProductCodePattern().IsMatch(stableCode))
            throw new InvalidOperationException("Product code must be a lower-case stable code containing only letters, digits, and hyphens.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new InvalidOperationException("Product display name is required and cannot exceed 200 characters.");
        if (description?.Trim().Length > 2000)
            throw new InvalidOperationException("Product description cannot exceed 2000 characters.");
        if (await db.ProductDefinitions.AnyAsync(item => item.Code == stableCode, cancellationToken))
            throw new InvalidOperationException("Product code already exists.");

        var now = clock.GetUtcNow();
        var product = new ProductDefinition
        {
            Id = Guid.NewGuid(), Code = stableCode, DisplayName = name,
            Description = CleanDescription(description), IsActive = true,
            CreatedAt = now, UpdatedAt = now
        };
        db.ProductDefinitions.Add(product);
        AddAudit(actor, "product.created", product, new { product.Code, product.DisplayName }, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToItem(product, 0);
    }

    public async Task UpdateAsync(
        Guid id,
        string? displayName,
        string? description,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.ProductsManage);
        var product = await db.ProductDefinitions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Product was not found.");
        var name = displayName is null ? product.DisplayName : displayName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new InvalidOperationException("Product display name is required and cannot exceed 200 characters.");
        if (description?.Trim().Length > 2000)
            throw new InvalidOperationException("Product description cannot exceed 2000 characters.");
        var old = new { product.DisplayName, product.Description };
        product.DisplayName = name;
        product.Description = CleanDescription(description);
        product.UpdatedAt = clock.GetUtcNow();
        AddAudit(actor, "product.updated", product, new { old, @new = new { product.DisplayName, product.Description } }, product.UpdatedAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetActiveAsync(
        Guid id,
        bool isActive,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.ProductsManage);
        var product = await db.ProductDefinitions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Product was not found.");
        if (product.IsActive == isActive) return;
        product.IsActive = isActive;
        product.UpdatedAt = clock.GetUtcNow();
        AddAudit(actor, isActive ? "product.activated" : "product.archived", product,
            new { product.Code, isActive }, product.UpdatedAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    private void AddAudit(string actor, string action, ProductDefinition product, object context, DateTimeOffset now) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor[..Math.Min(actor.Length, 256)],
            Action = action, TargetType = "product", TargetId = product.Id.ToString("D"), Result = "success",
            ContextJson = JsonSerializer.Serialize(context), TimestampUtc = now
        });

    private static string? CleanDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static ProductCatalogItem ToItem(ProductDefinition product, int references) =>
        new(product.Id, product.Code, product.DisplayName, product.Description, product.IsActive,
            references, product.CreatedAt, product.UpdatedAt);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodePattern();
}

internal sealed record ProductCatalogItem(
    Guid Id,
    string Code,
    string DisplayName,
    string? Description,
    bool IsActive,
    int LicenseReferenceCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
