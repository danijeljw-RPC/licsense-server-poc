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
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using LicenseServer.Authorization;

namespace LicenseServer.Tests;

public sealed class PostgresWebFixture : IAsyncLifetime
{
    internal const string MailerSendWebhookSecret = "stage12-mailersend-webhook-secret-with-32-bytes";
    internal const string StripeWebhookSecret = "whsec_stage14_test_webhook_secret";
    private readonly List<WebApplicationFactory<Program>> authenticatedFactories = [];
    private string? temporaryKeyDirectory;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public SecretCaptureLoggerProvider CapturedLogs { get; } = new();

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("Run tests through scripts/Test-DatabaseAndAuth.ps1 so they receive an isolated PostgreSQL database.");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connectionString);
        Environment.SetEnvironmentVariable("Security__RequireMfaForHighRiskPermissions", "true");
        Environment.SetEnvironmentVariable("Licensing__KeyDirectory", ResolveKeyDirectory());
        Environment.SetEnvironmentVariable("Licensing__DefaultSigningKey", "primary-2026");
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

    /// <summary>
    /// Points the key-ring at the repository's real "keys/" directory when the (gitignored) private
    /// key files are present locally or in CI; otherwise generates a throwaway "primary-2026" pair
    /// into a temp directory. Because signing/verification are both key-ring-backed now, there is no
    /// longer a separate TrustedPublicKeys monkey-patch needed for the fallback case - dropping a
    /// correctly-named pair into a directory is the one mechanism both paths share.
    /// </summary>
    private string ResolveKeyDirectory()
    {
        try
        {
            return Path.GetDirectoryName(FindRepositoryFile("keys/primary-2026.private.pem"))!;
        }
        catch (FileNotFoundException)
        {
            temporaryKeyDirectory = Path.Combine(Path.GetTempPath(), $"license-server-test-keys-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryKeyDirectory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(temporaryKeyDirectory, "primary-2026.private.pem"), key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(Path.Combine(temporaryKeyDirectory, "primary-2026.public.pem"), key.ExportSubjectPublicKeyInfoPem());
            return temporaryKeyDirectory;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in authenticatedFactories)
            await factory.DisposeAsync();
        await Factory.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("Licensing__KeyDirectory", null);
        Environment.SetEnvironmentVariable("Licensing__DefaultSigningKey", null);
        Environment.SetEnvironmentVariable("Security__RequireMfaForHighRiskPermissions", null);
        if (temporaryKeyDirectory is not null && Directory.Exists(temporaryKeyDirectory))
            Directory.Delete(temporaryKeyDirectory, recursive: true);
    }

    public HttpClient CreateAuthenticatedClient(bool administrator, params string[] permissions)
    {
        var client = CreateTestClient();
        if (administrator)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, DatabaseInitializer.AdministratorRole);
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.MfaHeader, "true");
        }
        foreach (var permission in permissions)
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PermissionHeader, permission);
        return client;
    }

    public HttpClient CreateRoleClient(string role, bool mfa)
    {
        var client = CreateTestClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, role);
        if (mfa) client.DefaultRequestHeaders.Add(TestAuthenticationHandler.MfaHeader, "true");
        return client;
    }

    private HttpClient CreateTestClient()
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
                ["ApiCredentials:Pepper"] = "HyQjIiEgHx4dHBsaGRgXFhUUExIREA8ODQwLCgkIBwY=",
                ["DeploymentKeys:Pepper"] = "9vWKjZpH04swggPztM7cfUpwLXtasA7YaCyYWZPylAI=",
                ["RateLimits:DeploymentKeyEnrollPermitLimit"] = "10",
                ["Email:WorkerEnabled"] = "false",
                ["MailerSend:WebhookSecret"] = MailerSendWebhookSecret,
                ["Stripe:WebhookSecret"] = StripeWebhookSecret,
                ["Billing:WorkerEnabled"] = "false",
                ["Security:UseHttpsRedirection"] = "false",
                ["Security:RequireMfaForHighRiskPermissions"] = "true",
                ["RateLimits:AdminPermitLimit"] = "5000",
                ["RateLimits:DevicePermitLimit"] = "5000"
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
    public const string MfaHeader = "X-Test-Mfa";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "phase0-test-operator"),
            new(ClaimTypes.Name, "phase0-test-operator")
        };
        foreach (var role in Request.Headers[RoleHeader].Select(value => value!).Where(value => value is not null))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.AddRange(BuiltInRoles.PermissionsFor(role)
                .Select(permission => new Claim(Permissions.ClaimType, permission)));
        }
        claims.AddRange(Request.Headers[PermissionHeader].Select(value => new Claim("permission", value!)));
        if (string.Equals(Request.Headers[MfaHeader].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase))
            claims.Add(new Claim("amr", "mfa"));
        claims.Add(new Claim("amr", "integration_test"));
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
