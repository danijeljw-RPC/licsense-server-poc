using LicenseServer.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LicenseServer.Tests;

public sealed class PostgresWebFixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("Run tests through scripts/Test-DatabaseAndAuth.ps1 so they receive an isolated PostgreSQL database.");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connectionString);
        Environment.SetEnvironmentVariable("Licensing__PrivateKeyPath", FindRepositoryFile("keys/license-primary-2026-private.pem"));
        Environment.SetEnvironmentVariable("Licensing__PublicKeyPath", FindRepositoryFile("keys/license-primary-2026-public.pem"));
        Factory = new LicenseServerFactory(connectionString);
        using var client = Factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        ready.EnsureSuccessStatusCode();
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("Licensing__PrivateKeyPath", null);
        Environment.SetEnvironmentVariable("Licensing__PublicKeyPath", null);
    }

    private sealed class LicenseServerFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["SEED_DEFAULT_ADMIN"] = "true",
                ["SEED_DEMO_LICENSE"] = "true",
                ["DEFAULT_ADMIN_EMAIL"] = DatabaseInitializer.DefaultEmail,
                ["DEFAULT_ADMIN_PASSWORD"] = DatabaseInitializer.DefaultPassword,
                ["Security:UseHttpsRedirection"] = "false"
            }));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestSuite : ICollectionFixture<PostgresWebFixture>
{
    public const string Name = "postgres-web";
}
