using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LicenseIdCounter> LicenseIdCounters => Set<LicenseIdCounter>();
    public DbSet<LicenseRecord> Licenses => Set<LicenseRecord>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<SigningKeyRecord> SigningKeys => Set<SigningKeyRecord>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>().Property(x => x.MustChangePassword).HasDefaultValue(false);
        builder.Entity<Customer>().HasIndex(x => x.ExternalId).IsUnique();
        builder.Entity<Customer>().Property(x => x.Name).HasMaxLength(200);
        builder.Entity<LicenseIdCounter>().HasKey(x => x.BusinessDate);
        builder.Entity<LicenseIdCounter>().ToTable(table => table.HasCheckConstraint(
            "CK_LicenseIdCounters_LastValue",
            "\"LastValue\" BETWEEN 0 AND 16777215"));
        builder.Entity<LicenseRecord>().HasIndex(x => x.LicenseId).IsUnique();
        builder.Entity<LicenseRecord>().Property(x => x.LicenseId).HasMaxLength(19);
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
        builder.Entity<LicenseRecord>().HasMany(x => x.Entitlements).WithOne(x => x.License).HasForeignKey(x => x.LicenseRecordId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<LicenseRecord>().HasMany(x => x.Activations).WithOne(x => x.License).HasForeignKey(x => x.LicenseRecordId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Entitlement>().HasIndex(x => new { x.LicenseRecordId, x.Product }).IsUnique();
        builder.Entity<Entitlement>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Entitlements_LicenseType", "\"LicenseType\" IN ('perpetual', 'subscription', 'evaluation')");
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
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableAudit();
        GuardImmutableLicenseIds();
        NormalizeExpiryPrecision();
        TouchVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableAudit();
        GuardImmutableLicenseIds();
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
