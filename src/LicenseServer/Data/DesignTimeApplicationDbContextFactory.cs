using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseServer.Data;

public sealed class DesignTimeApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var applicationServices = new ServiceCollection()
            .AddOptions()
            .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=license_server_design;Username=license_design;Password=not-used")
            .UseApplicationServiceProvider(applicationServices)
            .Options;
        return new ApplicationDbContext(options);
    }
}
