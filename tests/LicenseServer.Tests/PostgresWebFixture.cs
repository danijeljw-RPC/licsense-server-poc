using LicenseServer.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace LicenseServer.Tests;

public sealed class PostgresWebFixture : IAsyncLifetime
{
    private readonly List<WebApplicationFactory<Program>> authenticatedFactories = [];
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public SecretCaptureLoggerProvider CapturedLogs { get; } = new();

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
        foreach (var factory in authenticatedFactories)
            await factory.DisposeAsync();
        await Factory.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("Licensing__PrivateKeyPath", null);
        Environment.SetEnvironmentVariable("Licensing__PublicKeyPath", null);
    }

    public HttpClient CreateAuthenticatedClient(bool administrator, params string[] permissions)
    {
        var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
            services.AddSingleton<ILoggerProvider>(CapturedLogs);
        }));
        authenticatedFactories.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });
        if (administrator)
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, DatabaseInitializer.AdministratorRole);
        foreach (var permission in permissions)
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PermissionHeader, permission);
        return client;
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
                ["ActivationCodes:Pepper"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                ["Security:UseHttpsRedirection"] = "false"
            }));
        }
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Phase0Test";
    public const string RoleHeader = "X-Test-Role";
    public const string PermissionHeader = "X-Test-Permission";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "phase0-test-operator"),
            new(ClaimTypes.Name, "phase0-test-operator")
        };
        claims.AddRange(Request.Headers[RoleHeader].Select(value => new Claim(ClaimTypes.Role, value!)));
        claims.AddRange(Request.Headers[PermissionHeader].Select(value => new Claim("permission", value!)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public sealed class SecretCaptureLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();
    public IReadOnlyCollection<string> Messages => messages.ToArray();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(messages, categoryName);
    public void Clear() { while (messages.TryDequeue(out _)) { } }
    public void Dispose() { }

    private sealed class CaptureLogger(
        System.Collections.Concurrent.ConcurrentQueue<string> messages,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestSuite : ICollectionFixture<PostgresWebFixture>
{
    public const string Name = "postgres-web";
}
