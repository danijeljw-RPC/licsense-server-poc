using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
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
        builder.Entity<LicenseRecord>().HasIndex(x => x.LicenseId).IsUnique();
        builder.Entity<LicenseRecord>().Property(x => x.LicenseId).HasMaxLength(100);
        builder.Entity<LicenseRecord>().Property(x => x.Version).IsConcurrencyToken();
        builder.Entity<LicenseRecord>().HasMany(x => x.Entitlements).WithOne(x => x.License).HasForeignKey(x => x.LicenseRecordId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<LicenseRecord>().HasMany(x => x.Activations).WithOne(x => x.License).HasForeignKey(x => x.LicenseRecordId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Entitlement>().HasIndex(x => new { x.LicenseRecordId, x.Product }).IsUnique();
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
        TouchVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableAudit();
        TouchVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardImmutableAudit()
    {
        if (ChangeTracker.Entries<AuditRecord>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit records are immutable.");
    }

    private void TouchVersions()
    {
        foreach (var entry in ChangeTracker.Entries<LicenseRecord>().Where(x => x.State == EntityState.Modified))
            entry.Entity.Version++;
    }
}
