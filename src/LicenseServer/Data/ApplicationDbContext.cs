using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LicenseServer.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LicenseIdCounter> LicenseIdCounters => Set<LicenseIdCounter>();
    public DbSet<ProductDefinition> ProductDefinitions => Set<ProductDefinition>();
    public DbSet<LicenseRecord> Licenses => Set<LicenseRecord>();
    public DbSet<IssuanceIdempotencyRecord> IssuanceIdempotencyRecords => Set<IssuanceIdempotencyRecord>();
    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<SigningKeyRecord> SigningKeys => Set<SigningKeyRecord>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>().Property(x => x.MustChangePassword).HasDefaultValue(false);
        builder.Entity<Customer>().HasIndex(x => x.ExternalId).IsUnique();
        builder.Entity<Customer>().HasIndex(x => x.NormalizedEmail);
        builder.Entity<Customer>().Property(x => x.Name).HasMaxLength(200);
        builder.Entity<Customer>().Property(x => x.Email).HasMaxLength(CustomerEmails.MaximumLength);
        builder.Entity<Customer>().Property(x => x.NormalizedEmail).HasMaxLength(CustomerEmails.MaximumLength);
        builder.Entity<LicenseIdCounter>().HasKey(x => x.BusinessDate);
        builder.Entity<LicenseIdCounter>().ToTable(table => table.HasCheckConstraint(
            "CK_LicenseIdCounters_LastValue",
            "\"LastValue\" BETWEEN 0 AND 16777215"));
        builder.Entity<ProductDefinition>().HasIndex(x => x.Code).IsUnique();
        builder.Entity<ProductDefinition>().Property(x => x.Code).HasMaxLength(100);
        builder.Entity<ProductDefinition>().Property(x => x.DisplayName).HasMaxLength(200);
        builder.Entity<ProductDefinition>().Property(x => x.Description).HasMaxLength(2000);
        builder.Entity<ProductDefinition>().ToTable(table => table.HasCheckConstraint(
            "CK_ProductDefinitions_Code",
            "\"Code\" ~ '^[a-z0-9][a-z0-9-]{0,99}$'"));
        builder.Entity<LicenseRecord>().HasIndex(x => x.LicenseId).IsUnique();
        builder.Entity<LicenseRecord>().Property(x => x.LicenseId).HasMaxLength(19);
        builder.Entity<LicenseRecord>().Property(x => x.MetadataJson).HasColumnType("jsonb");
        builder.Entity<LicenseRecord>().Property(x => x.ActivationCodeHashVersion).HasMaxLength(32)
            .HasDefaultValue(ActivationCodeHasher.LegacySha256Version);
        builder.Entity<LicenseRecord>().Property(x => x.Version).IsConcurrencyToken();
        builder.Entity<LicenseRecord>().Property(x => x.RevocationReason).HasMaxLength(500);
        builder.Entity<LicenseRecord>().Property(x => x.CancellationReason).HasMaxLength(500);
        builder.Entity<LicenseRecord>().Property(x => x.RevokedBy).HasMaxLength(256);
        builder.Entity<LicenseRecord>().Property(x => x.CancelledBy).HasMaxLength(256);
        builder.Entity<LicenseRecord>().Property(x => x.CancellationReference).HasMaxLength(200);
        builder.Entity<LicenseRecord>().HasIndex(x => x.ExpiresAt);
        builder.Entity<LicenseRecord>().Property(x => x.ExpirySubMicrosecondTicks).HasDefaultValue(0);
        builder.Entity<LicenseRecord>().HasIndex(x => x.CancelledAt);
        builder.Entity<LicenseRecord>().HasIndex(x => x.RevokedAt);
        builder.Entity<LicenseRecord>().ToTable(table => table.HasCheckConstraint(
            "CK_Licenses_TerminalState",
            "NOT (\"CancelledAt\" IS NOT NULL AND \"RevokedAt\" IS NOT NULL)"));
        builder.Entity<LicenseRecord>().ToTable(table => table.HasCheckConstraint(
            "CK_Licenses_ExpiryPrecision",
            "\"ExpirySubMicrosecondTicks\" BETWEEN 0 AND 9"));
        builder.Entity<LicenseRecord>().ToTable(table => table.HasCheckConstraint(
            "CK_Licenses_ContactEmail",
            "COALESCE(jsonb_typeof(\"MetadataJson\" -> 'contactEmail') = 'string' " +
            "AND (\"MetadataJson\" ->> 'contactEmail') = lower(btrim(\"MetadataJson\" ->> 'contactEmail')) " +
            "AND (\"MetadataJson\" ->> 'contactEmail') ~ '^[^[:space:]@]+@[^[:space:]@]+\\.[^[:space:]@]+$', FALSE)"));
        builder.Entity<LicenseRecord>().HasMany(x => x.Entitlements).WithOne(x => x.License).HasForeignKey(x => x.LicenseRecordId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<LicenseRecord>().HasMany(x => x.Activations).WithOne(x => x.License).HasForeignKey(x => x.LicenseRecordId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<IssuanceIdempotencyRecord>().Property(x => x.PrincipalId).HasMaxLength(256);
        builder.Entity<IssuanceIdempotencyRecord>().Property(x => x.ProtectedResult).HasColumnType("text");
        builder.Entity<IssuanceIdempotencyRecord>().HasIndex(x => new { x.PrincipalId, x.KeyHash }).IsUnique();
        builder.Entity<IssuanceIdempotencyRecord>().HasIndex(x => x.ExpiresAt);
        builder.Entity<ApiCredential>().HasIndex(x => x.PublicId).IsUnique();
        builder.Entity<ApiCredential>().HasIndex(x => x.OwnerUserId);
        builder.Entity<ApiCredential>().HasIndex(x => x.ExpiresAt);
        builder.Entity<ApiCredential>().Property(x => x.PublicId).HasMaxLength(32);
        builder.Entity<ApiCredential>().Property(x => x.Name).HasMaxLength(200);
        builder.Entity<ApiCredential>().Property(x => x.HashVersion).HasMaxLength(32);
        builder.Entity<ApiCredential>().Property(x => x.LastFour).HasMaxLength(4);
        builder.Entity<ApiCredential>().Property(x => x.RevokedBy).HasMaxLength(256);
        builder.Entity<ApiCredential>().Property(x => x.ScopesJson).HasColumnType("jsonb");
        builder.Entity<ApiCredential>().HasOne(x => x.OwnerUser).WithMany()
            .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ApiCredential>().ToTable(table => table.HasCheckConstraint(
            "CK_ApiCredentials_Lifecycle", "\"ExpiresAt\" IS NULL OR \"ExpiresAt\" > \"CreatedAt\""));
        builder.Entity<Entitlement>().HasIndex(x => x.LicenseRecordId).IsUnique();
        builder.Entity<Entitlement>().HasOne(x => x.ProductDefinition).WithMany(x => x.Entitlements)
            .HasForeignKey(x => x.ProductDefinitionId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Entitlement>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Entitlements_LicenseType", "\"LicenseType\" IN ('perpetual', 'subscription', 'evaluation')");
            table.HasCheckConstraint("CK_Entitlements_Edition", "\"Edition\" IN ('community', 'project', 'education', 'consumer', 'business', 'smb', 'enterprise', 'corporate')");
            table.HasCheckConstraint("CK_Entitlements_Seats", "\"Seats\" > 0");
        });
        builder.Entity<Activation>().HasIndex(x => x.ActivationId).IsUnique();
        builder.Entity<Activation>().HasIndex(x => new { x.LicenseRecordId, x.RequestId }).IsUnique();
        builder.Entity<Activation>().HasIndex(x => x.LicenseRecordId).IsUnique().HasFilter("\"DeactivatedAt\" IS NULL");
        builder.Entity<Activation>().HasIndex(x => x.LeaseExpiresAt);
        builder.Entity<SigningKeyRecord>().HasIndex(x => x.KeyId).IsUnique();
        builder.Entity<AuditRecord>().HasIndex(x => x.TimestampUtc);
        builder.Entity<AuditRecord>().Property(x => x.Actor).HasMaxLength(256);
        builder.Entity<AuditRecord>().Property(x => x.Action).HasMaxLength(100);
        builder.Entity<ApplicationUser>().Property(x => x.AccountType).HasMaxLength(20)
            .HasDefaultValue(ApplicationUser.HumanAccountType);
        builder.Entity<ApplicationUser>().Property(x => x.IsEnabled).HasDefaultValue(true);
        builder.Entity<ApplicationUser>().Property(x => x.DisabledBy).HasMaxLength(256);
        builder.Entity<ApplicationUser>().HasIndex(x => new { x.AccountType, x.IsEnabled });
        builder.Entity<ApplicationUser>().ToTable(table => table.HasCheckConstraint(
            "CK_AspNetUsers_AccountType", "\"AccountType\" IN ('human', 'service')"));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableAudit();
        GuardImmutableLicenseIds();
        GuardImmutableProductCodes();
        GuardCustomerContactSnapshots();
        NormalizeExpiryPrecision();
        TouchVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableAudit();
        GuardImmutableLicenseIds();
        GuardImmutableProductCodes();
        GuardCustomerContactSnapshots();
        NormalizeExpiryPrecision();
        TouchVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardImmutableAudit()
    {
        if (ChangeTracker.Entries<AuditRecord>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit records are immutable.");
    }

    private void GuardImmutableLicenseIds()
    {
        if (ChangeTracker.Entries<LicenseRecord>().Any(entry =>
                entry.State == EntityState.Modified
                && entry.Property(x => x.LicenseId).IsModified
                && !string.Equals(
                    entry.Property(x => x.LicenseId).OriginalValue,
                    entry.Property(x => x.LicenseId).CurrentValue,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("LicenseId is immutable after insertion.");
        }
    }

    private void TouchVersions()
    {
        foreach (var entry in ChangeTracker.Entries<LicenseRecord>().Where(x => x.State == EntityState.Modified))
            entry.Entity.Version++;
    }

    private void GuardCustomerContactSnapshots()
    {
        foreach (var entry in ChangeTracker.Entries<Customer>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (!CustomerEmails.TryNormalize(entry.Entity.Email, out var normalized, out var error)
                || !string.Equals(entry.Entity.NormalizedEmail, normalized, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(error ?? "Customer normalized email must match the current email.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<LicenseRecord>())
        {
            if (entry.State == EntityState.Modified && entry.Property(item => item.MetadataJson).IsModified)
                throw new InvalidOperationException("Signed contact metadata is an immutable issuance snapshot.");
            if (entry.State != EntityState.Added)
                continue;

            string? contactEmail;
            try
            {
                using var document = JsonDocument.Parse(entry.Entity.MetadataJson);
                contactEmail = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("contactEmail", out var value)
                    && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                contactEmail = null;
            }

            if (!CustomerEmails.TryNormalize(contactEmail, out var normalized, out _)
                || !string.Equals(entry.Entity.Customer.NormalizedEmail, normalized, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("License metadata.contactEmail must equal the normalized customer email at issuance.");
            }
        }
    }

    private void GuardImmutableProductCodes()
    {
        if (ChangeTracker.Entries<ProductDefinition>().Any(entry =>
                entry.State == EntityState.Modified
                && entry.Property(item => item.Code).IsModified
                && !string.Equals(
                    entry.Property(item => item.Code).OriginalValue,
                    entry.Property(item => item.Code).CurrentValue,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Product code is immutable after creation.");
        }
    }

    private void NormalizeExpiryPrecision()
    {
        foreach (var entry in ChangeTracker.Entries<LicenseRecord>()
                     .Where(x => x.Entity.ExpiresAt is not null
                         && (x.State == EntityState.Added || x.Property(y => y.ExpiresAt).IsModified)))
        {
            var utc = entry.Entity.ExpiresAt!.Value.ToUniversalTime();
            entry.Entity.ExpirySubMicrosecondTicks = (int)(utc.Ticks % 10);
            entry.Entity.ExpiresAt = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        }
    }
}
