using LicenseServer;
using LicenseServer.Components;
using LicenseServer.Components.Account;
using LicenseServer.Data;
using LicenseServer.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "License Server Administration API";
        document.Info.Description = "Versioned administration surface. Mutation endpoints require action-specific permissions, antiforgery for cookie clients, correlation IDs, and documented optimistic concurrency. Activation codes are returned only once. Offline license files already downloaded cannot be recalled.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["IdentityCookie"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = "LicenseServer.Auth",
                Description = "ASP.NET Core Identity operator session. Mutations also require X-CSRF-TOKEN."
            },
            ["ApiKeyBearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "lic_live API credential",
                Description = "Scoped service-account credential. Permissions are constrained to the stored API-key scopes."
            }
        };
        return Task.CompletedTask;
    });
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = ApiKeyAuthenticationDefaults.CompositeScheme;
        options.DefaultAuthenticateScheme = ApiKeyAuthenticationDefaults.CompositeScheme;
        options.DefaultChallengeScheme = ApiKeyAuthenticationDefaults.CompositeScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });
authentication.AddIdentityCookies(options =>
    {
        options.ApplicationCookie!.Configure(cookie =>
        {
            cookie.Cookie.Name = builder.Environment.IsDevelopment()
                ? "LicenseServer.Auth"
                : "__Host-LicenseServer.Auth";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = true;
            cookie.LoginPath = "/Account/Login";
            cookie.AccessDeniedPath = "/Account/AccessDenied";
            cookie.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else
                    context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                else
                    context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
    });
authentication.AddPolicyScheme(ApiKeyAuthenticationDefaults.CompositeScheme, null, options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.FirstOrDefault()?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? ApiKeyAuthenticationDefaults.Scheme
                : IdentityConstants.ApplicationScheme;
    });
authentication.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.Scheme, _ => { });

builder.Services.AddPermissionAuthorization(builder.Environment, builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is required.");
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Password.RequiredLength = 14;
        options.Password.RequiredUniqueChars = 6;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<MfaUserClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
    options.TokenLifespan = TimeSpan.FromMinutes(15));

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddOptions<LicensingOptions>()
    .BindConfiguration("Licensing");
builder.Services.AddOptions<ActivationCodeOptions>()
    .BindConfiguration("ActivationCodes")
    .Validate(options => options.IdempotencyWindowMinutes is >= 1 and <= 15,
        "ActivationCodes:IdempotencyWindowMinutes must be between 1 and 15.")
    .ValidateOnStart();
var configuredPepper = builder.Configuration["ActivationCodes:Pepper"];
var ephemeralDevelopmentPepper = string.IsNullOrWhiteSpace(configuredPepper) && builder.Environment.IsDevelopment();
byte[] activationCodePepper;
if (ephemeralDevelopmentPepper)
{
    activationCodePepper = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
else if (string.IsNullOrWhiteSpace(configuredPepper))
{
    throw new InvalidOperationException(
        "ActivationCodes:Pepper is required outside Development. Supply at least 32 random bytes as Base64 through secret configuration (for example ActivationCodes__Pepper)." );
}
else
{
    try
    {
        activationCodePepper = Convert.FromBase64String(configuredPepper);
    }
    catch (FormatException exception)
    {
        throw new InvalidOperationException("ActivationCodes:Pepper must be valid Base64.", exception);
    }

    if (activationCodePepper.Length < 32)
        throw new InvalidOperationException("ActivationCodes:Pepper must decode to at least 32 random bytes.");
}
builder.Services.AddSingleton<IActivationCodeGenerator, ActivationCodeGenerator>();
builder.Services.AddSingleton<IActivationCodeHasher>(new ActivationCodeHasher(activationCodePepper));
var configuredApiCredentialPepper = builder.Configuration["ApiCredentials:Pepper"];
byte[] apiCredentialPepper;
if (string.IsNullOrWhiteSpace(configuredApiCredentialPepper) && builder.Environment.IsDevelopment())
    apiCredentialPepper = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
else if (string.IsNullOrWhiteSpace(configuredApiCredentialPepper))
    throw new InvalidOperationException("ApiCredentials:Pepper is required outside Development and must be at least 32 random bytes encoded as Base64.");
else
{
    try { apiCredentialPepper = Convert.FromBase64String(configuredApiCredentialPepper); }
    catch (FormatException exception) { throw new InvalidOperationException("ApiCredentials:Pepper must be valid Base64.", exception); }
    if (apiCredentialPepper.Length < 32) throw new InvalidOperationException("ApiCredentials:Pepper must decode to at least 32 bytes.");
}
builder.Services.AddSingleton(new ApiCredentialHasher(apiCredentialPepper));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ILicenseBusinessDateResolver, ConfiguredLicenseBusinessDateResolver>();
builder.Services.AddScoped<LicenseIdAllocator>();
builder.Services.AddScoped<LicenseStore>();
builder.Services.AddScoped<AdminDataService>();
builder.Services.AddScoped<ProductCatalogService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<UserAdministrationService>();
builder.Services.AddScoped<ApiCredentialService>();
builder.Services.AddScoped<IOwnedCredentialRevoker>(services => services.GetRequiredService<ApiCredentialService>());
builder.Services.AddSingleton<LicenseEnvelopeSigner>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql");
var adminPermitLimit = builder.Configuration.GetValue("RateLimits:AdminPermitLimit", 600);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-api", context =>
        context.User.HasClaim("amr", "api_key")
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"{context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous"}:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = adminPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                })
            : RateLimitPartition.GetNoLimiter("non-bearer"));
    options.AddPolicy("device-api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("LicenseServer");
var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}

var app = builder.Build();
if (ephemeralDevelopmentPepper)
{
    var logEphemeralPepper = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1001, "EphemeralActivationCodePepper"),
        "ActivationCodes:Pepper is not configured. Development is using an ephemeral pepper; newly issued activation codes will not validate after restart. Configure a Base64 secret with at least 32 random bytes for persistent development data.");
    logEphemeralPepper(app.Logger, null);
}

app.Use(async (context, next) =>
{
    const string header = "X-Correlation-ID";
    var supplied = context.Request.Headers[header].FirstOrDefault();
    var correlationId = !string.IsNullOrWhiteSpace(supplied)
                        && supplied.Length <= 128
                        && supplied.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
        ? supplied
        : context.TraceIdentifier;
    context.TraceIdentifier = correlationId;
    context.Response.Headers[header] = correlationId;
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (app.Configuration.GetValue("Security:UseHttpsRedirection", !app.Environment.IsDevelopment()))
    app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("Referrer-Policy", "no-referrer");
        headers.TryAdd("X-Frame-Options", "DENY");
        headers.TryAdd("Permissions-Policy", "camera=(self), microphone=(), geolocation=()");
        headers.TryAdd("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'");
        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && context.User.Identity.AuthenticationType != ApiKeyAuthenticationDefaults.Scheme
        && !context.Request.Path.StartsWithSegments("/Account")
        && !context.Request.Path.StartsWithSegments("/health"))
    {
        var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(context.User);
        if (user is { IsEnabled: false } || user?.AccountType == ApplicationUser.ServiceAccountType)
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Response.Redirect("/Account/Login");
            return;
        }
        if (user?.MustChangePassword == true)
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Password change required",
                    Detail = "Change the seeded development password before using administrative APIs."
                });
                return;
            }

            context.Response.Redirect("/Account/Manage/ChangePassword?forced=true");
            return;
        }
    }
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapHealthChecks("/health/ready");
app.MapGet("/health", () => Results.Redirect("/health/ready")).AllowAnonymous();
app.MapOpenApi().AllowAnonymous();

app.MapPost("/api/v1/licenses/{licenseId}/activate", async (
    string licenseId, ActivateRequest request, LicenseStore store, LicenseEnvelopeSigner signer, CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var result = await store.ActivateAsync(licenseId, request, now, cancellationToken);
    return result.Success
        ? await SignedResponse(result.Value!, store, signer, now, cancellationToken)
        : Problem(result);
}).AllowAnonymous().RequireRateLimiting("device-api");

app.MapPost("/api/v1/activations/{activationId}/validate", async (
    string activationId, ActivationCredentialRequest request, LicenseStore store, CancellationToken cancellationToken) =>
{
    var result = await store.AuthorizeAsync(activationId, request, cancellationToken);
    return result.Success
        ? Results.Ok(new ValidationResponse(result.Value!.LicenseId, activationId, "active", DateTimeOffset.UtcNow))
        : Problem(result);
}).AllowAnonymous().RequireRateLimiting("device-api");

app.MapPost("/api/v1/activations/{activationId}/refresh", async (
    string activationId, ActivationCredentialRequest request, LicenseStore store, LicenseEnvelopeSigner signer, CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var result = await store.RefreshAsync(activationId, request, now, cancellationToken);
    return result.Success
        ? await SignedResponse(result.Value!, store, signer, now, cancellationToken)
        : Problem(result);
}).AllowAnonymous().RequireRateLimiting("device-api");

app.MapPost("/api/v1/activations/{activationId}/deactivate", async (
    string activationId, ActivationCredentialRequest request, LicenseStore store, CancellationToken cancellationToken) =>
{
    var result = await store.DeactivateAsync(activationId, request, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
    return result.Success
        ? Results.Ok(new DeactivationResponse(result.Value!.LicenseId, activationId, "deactivated", DateTimeOffset.UtcNow))
        : Problem(result);
}).AllowAnonymous().RequireRateLimiting("device-api");

var adminApi = app.MapGroup("/api/v1/admin").RequireAuthorization().RequireRateLimiting("admin-api").DisableAntiforgery();
adminApi.MapGet("/authorization/{permission}", async (
    string permission, IAuthorizationService authorizationService, HttpContext context) =>
{
    if (!Permissions.All.Contains(permission, StringComparer.Ordinal)) return Results.NotFound();
    return (await authorizationService.AuthorizeAsync(context.User, null, permission)).Succeeded
        ? Results.NoContent()
        : Results.Forbid();
});
adminApi.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
});
adminApi.MapGet("/licenses/{licenseId}", async (
    string licenseId, AdminDataService data, HttpContext context, CancellationToken ct) =>
{
    var item = await data.GetLicenseAsync(licenseId, ct);
    if (item is null) return Results.NotFound();
    context.Response.Headers.ETag = $"\"{item.Version}\"";
    return Results.Ok(item);
})
    .RequireAuthorization(Permissions.LicensesRead);
adminApi.MapGet("/licenses", async (
    string? search, string? status, string? sort, int? page, int? pageSize,
    AdminDataService data, CancellationToken ct) =>
    Results.Ok(await data.SearchLicensesAsync(search, status, sort, page ?? 1, pageSize ?? 20, ct)))
    .RequireAuthorization(Permissions.LicensesRead)
    .WithDescription("Returns a stably sorted, bounded page of safe license summary DTOs. pageSize is clamped to 5-100.");
adminApi.MapGet("/products", async (string? search, ProductCatalogService catalog, CancellationToken ct) =>
    Results.Ok(await catalog.SearchAsync(search, ct)))
    .RequireAuthorization(Permissions.ProductsRead);
adminApi.MapGet("/users", async (string? search, UserAdministrationService service, CancellationToken ct) =>
    Results.Ok(await service.SearchAsync(search, ct)))
    .RequireAuthorization(Permissions.UsersRead);
adminApi.MapGet("/customers", async (string? search, int? take, AdminDataService data, CancellationToken ct) =>
    Results.Ok(await data.SearchCustomersAsync(search, take ?? 50, ct)))
    .RequireAuthorization(Permissions.CustomersRead);
adminApi.MapPatch("/customers/{customerId:guid}", async (
    Guid customerId, UpdateCustomerRequest request, AdminDataService data, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try { return Results.Ok(await data.UpdateCustomerAsync(customerId, request, context.User.Identity?.Name ?? "unknown", ct)); }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["customer"] = [exception.Message] });
    }
}).RequireAuthorization(Permissions.CustomersManage);
adminApi.MapGet("/audit", async (
    string? action, string? actor, string? targetType, string? targetId, int? take,
    AdminDataService data, CancellationToken ct) =>
    Results.Ok(await data.GetAuditAsync(action, actor, targetType, targetId, take ?? 200, ct)))
    .RequireAuthorization(Permissions.AuditRead);
adminApi.MapGet("/api-keys", async (
    string? ownerUserId, ApiCredentialService service, HttpContext context, CancellationToken ct) =>
{
    var owner = ownerUserId ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    return string.IsNullOrWhiteSpace(owner) ? Results.BadRequest() : Results.Ok(await service.ListAsync(owner, ct));
}).RequireAuthorization(Permissions.ApiKeysManageSelf);
adminApi.MapPost("/api-keys", async (
    CreateApiCredentialRequest request, ApiCredentialService service, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try { return Results.Created("/api/v1/admin/api-keys", await service.CreateAsync(request, context.User.Identity?.Name ?? "unknown", ct)); }
    catch (InvalidOperationException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["apiKey"] = [exception.Message] }); }
}).RequireAuthorization(Permissions.ApiKeysManageSelf);
adminApi.MapPost("/api-keys/{id:guid}/rotate", async (
    Guid id, ApiCredentialService service, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try { return Results.Ok(await service.RotateAsync(id, context.User.Identity?.Name ?? "unknown", ct)); }
    catch (InvalidOperationException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["apiKey"] = [exception.Message] }); }
}).RequireAuthorization(Permissions.ApiKeysManageSelf);
adminApi.MapPost("/api-keys/{id:guid}/revoke", async (
    Guid id, ApiCredentialService service, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try { await service.RevokeAsync(id, context.User.Identity?.Name ?? "unknown", ct); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["apiKey"] = [exception.Message] }); }
}).RequireAuthorization(Permissions.ApiKeysManageSelf);
adminApi.MapPost("/users", async (
    CreateUserRequest request, UserAdministrationService service, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try
    {
        var actor = context.User.Identity?.Name ?? "unknown";
        var baseUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/");
        return string.Equals(request.AccountType, ApplicationUser.ServiceAccountType, StringComparison.OrdinalIgnoreCase)
            ? Results.Created("/api/v1/admin/users", await service.CreateServiceAccountAsync(
                new CreateServiceAccountRequest(request.Email, request.Roles), actor, ct))
            : Results.Created("/api/v1/admin/users", await service.InviteHumanAsync(
                new InviteUserRequest(request.Email, request.Roles), actor, baseUri, ct));
    }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = [exception.Message] });
    }
}).RequireAuthorization(Permissions.UsersManage);
adminApi.MapPatch("/users/{userId}", async (
    string userId, UpdateUserRequest request, UserAdministrationService service, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try
    {
        var actor = context.User.Identity?.Name ?? "unknown";
        UserMutationResult? result = null;
        if (request.Roles is not null)
            result = await service.ReplaceRolesAsync(userId, request.Roles, actor, ct);
        if (request.IsEnabled is not null)
            result = await service.SetEnabledAsync(userId, request.IsEnabled.Value, actor, ct);
        if (request.ForcePasswordReset)
        {
            var baseUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/");
            return Results.Ok(await service.InitiatePasswordResetAsync(userId, actor, baseUri, ct));
        }
        return result is null ? Results.NoContent() : Results.Ok(result);
    }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = [exception.Message] });
    }
}).RequireAuthorization(Permissions.UsersManage);
adminApi.MapPost("/products", async (
    CreateProductRequest request, ProductCatalogService catalog, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try
    {
        var product = await catalog.CreateAsync(request.Code, request.DisplayName, request.Description,
            context.User.Identity?.Name ?? "unknown", ct);
        return Results.Created($"/api/v1/admin/products/{product.Id}", product);
    }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["product"] = [exception.Message] });
    }
}).RequireAuthorization(Permissions.ProductsManage);
adminApi.MapPatch("/products/{id:guid}", async (
    Guid id, UpdateProductRequest request, ProductCatalogService catalog, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    try
    {
        if (request.DisplayName is not null || request.Description is not null)
            await catalog.UpdateAsync(id, request.DisplayName, request.Description, context.User.Identity?.Name ?? "unknown", ct);
        if (request.IsActive is not null)
            await catalog.SetActiveAsync(id, request.IsActive.Value, context.User.Identity?.Name ?? "unknown", ct);
        return Results.NoContent();
    }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["product"] = [exception.Message] });
    }
}).RequireAuthorization(Permissions.ProductsManage);
adminApi.MapPost("/licenses", async (
    IssueLicenseRequest request, LicenseStore store, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!context.User.HasClaim("amr", "integration_test")
        && !await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    var principalId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.Identity?.Name
        ?? "unknown";
    var result = await store.IssueAsync(
        request,
        new IssuanceContext(
            context.User.Identity?.Name ?? principalId,
            principalId,
            context.TraceIdentifier,
            context.Request.Headers["Idempotency-Key"].FirstOrDefault()),
        ct);
    return result.Success
        ? Results.Created($"/api/v1/admin/licenses/{result.Value!.LicenseId}", result.Value)
        : IssueProblem(result);
}).RequireAuthorization(Permissions.LicensesIssue)
  .WithDescription("Issues one license transactionally. Online lifecycle changes are immediate; previously downloaded offline files cannot be recalled.");
adminApi.MapPost("/licenses/{licenseId}/revoke", async (
    string licenseId, RevokeRequest request, LicenseStore store, IAntiforgery antiforgery, HttpContext httpContext, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, httpContext)) return AntiforgeryProblem();
    if (!request.Confirmed)
        return Results.Problem(title: "Invalid request", detail: "Explicit revocation confirmation is required.", statusCode: 400);
    var result = await store.RevokeAsync(
        licenseId, request.Reason, httpContext.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow,
        request.Version, httpContext.TraceIdentifier, ct);
    return result.Success ? Results.Ok(new { licenseId, status = "revoked" }) : Problem(result);
}).RequireAuthorization(Permissions.LicensesRevoke)
  .WithDescription("Revocation is terminal and immediately blocks online checks. Previously downloaded offline files cannot be recalled.");
adminApi.MapPost("/licenses/{licenseId}/cancel", async (
    string licenseId, CancelRequest request, LicenseStore store, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    if (!request.Confirmed)
        return Results.Problem(title: "Invalid request", detail: "Explicit cancellation confirmation is required.", statusCode: 400);
    var result = await store.CancelAsync(
        licenseId, request.Reason, context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow,
        request.Version, request.Reference, context.TraceIdentifier, ct);
    return result.Success ? Results.Ok(new { licenseId, status = "cancelled" }) : Problem(result);
}).RequireAuthorization(Permissions.LicensesCancel)
  .WithDescription("Cancels a never-activated license. Cancellation is terminal; previously downloaded offline files cannot be recalled.");
adminApi.MapPost("/licenses/{licenseId}/terms", async (
    string licenseId, AmendTermsRequest request, LicenseStore store, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    var result = await store.AmendTermsAsync(
        licenseId, request, context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow,
        context.TraceIdentifier, ct);
    return result.Success ? Results.Ok(new { licenseId, status = "amended" }) : Problem(result);
}).RequireAuthorization(Permissions.LicensesUpdate)
  .WithDescription("Amends expiry, seats, or update coverage with optimistic concurrency. Online checks observe changes immediately; previously downloaded offline files cannot be recalled.");
adminApi.MapPatch("/licenses/{licenseId}/terms", async (
    string licenseId, AmendTermsRequest request, LicenseStore store, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    if (!TryReadIfMatch(context.Request, out var ifMatch) || ifMatch != request.Version)
        return Results.Problem(title: "License state conflict", detail: "If-Match must contain the current quoted license version and agree with the request version.", statusCode: StatusCodes.Status409Conflict);
    var result = await store.AmendTermsAsync(
        licenseId, request, context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow,
        context.TraceIdentifier, ct);
    return result.Success ? Results.Ok(new { licenseId, status = "amended" }) : Problem(result);
}).RequireAuthorization(Permissions.LicensesUpdate)
  .WithDescription("Patches controlled license terms. Requires If-Match with the ETag returned by license detail and an antiforgery token for cookie sessions.");

adminApi.MapPost("/licenses/{licenseId}/activation-code/rotate", async (
    string licenseId, RotateActivationCodeRequest request, LicenseStore store, IAntiforgery antiforgery,
    HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    var result = await store.RotateActivationCodeAsync(
        licenseId, request.Version, context.User.Identity?.Name ?? "unknown", context.TraceIdentifier, ct);
    return result.Success ? Results.Ok(result.Value) : Problem(result);
}).RequireAuthorization(Permissions.LicensesUpdate)
  .WithDescription("Replaces the stored activation-code hash and returns the new plaintext code exactly once in this response.");

adminApi.MapPost("/activations/{activationId}/deactivate", async (
    string activationId, AdminDeactivateRequest request, LicenseStore store, IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
{
    if (!await ValidAntiforgeryAsync(antiforgery, context)) return AntiforgeryProblem();
    if (!request.Confirmed)
        return Results.Problem(title: "Invalid request", detail: "Explicit deactivation confirmation is required.", statusCode: 400);
    var result = await store.AdminDeactivateAsync(activationId, request.Reason, request.Version,
        context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow, context.TraceIdentifier, ct);
    return result.Success ? Results.Ok(new { activationId, status = "deactivated" }) : Problem(result);
}).RequireAuthorization(Permissions.ActivationsManage);

app.MapPost("/licenses/{licenseId}/cancel", async (string licenseId, HttpRequest request, LicenseStore store, HttpContext context, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    if (!PostedConfirmation(form)) return LifecycleRedirect(licenseId, "error", "Explicit cancellation confirmation is required.");
    if (!long.TryParse(form["Version"], out var version)) return LifecycleRedirect(licenseId, "error", "The page version is invalid. Reload and retry.");
    var result = await store.CancelAsync(licenseId, form["Reason"], context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow,
        version, form["Reference"], context.TraceIdentifier, ct);
    return result.Success ? LifecycleRedirect(licenseId, "notice", "License cancelled.") : LifecycleRedirect(licenseId, "error", result.Error);
}).RequireAuthorization(Permissions.LicensesCancel).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapPost("/licenses/{licenseId}/revoke", async (string licenseId, HttpRequest request, LicenseStore store, HttpContext context, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    if (!PostedConfirmation(form)) return LifecycleRedirect(licenseId, "error", "Explicit revocation confirmation is required.");
    if (!long.TryParse(form["Version"], out var version)) return LifecycleRedirect(licenseId, "error", "The page version is invalid. Reload and retry.");
    var result = await store.RevokeAsync(licenseId, form["Reason"], context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow,
        version, context.TraceIdentifier, ct);
    return result.Success ? LifecycleRedirect(licenseId, "notice", "License revoked.") : LifecycleRedirect(licenseId, "error", result.Error);
}).RequireAuthorization(Permissions.LicensesRevoke).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapPost("/licenses/{licenseId}/terms", async (string licenseId, HttpRequest request, LicenseStore store, HttpContext context, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    if (!long.TryParse(form["Version"], out var version)) return LifecycleRedirect(licenseId, "error", "The page version is invalid. Reload and retry.");
    DateTimeOffset? expires = DateTimeOffset.TryParse(form["ExpiresAt"], System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal, out var expiryValue) ? expiryValue.ToUniversalTime() : null;
    int? seats = int.TryParse(form["Seats"], out var seatsValue) ? seatsValue : null;
    DateOnly? updates = DateOnly.TryParse(form["UpdatesUntil"], out var updatesValue) ? updatesValue : null;
    var edition = string.IsNullOrWhiteSpace(form["Edition"]) ? null : form["Edition"].ToString();
    var result = await store.AmendTermsAsync(licenseId, new AmendTermsRequest(expires, seats, updates, form["Reason"], version, edition),
        context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow, context.TraceIdentifier, ct);
    return result.Success ? LifecycleRedirect(licenseId, "notice", "License terms updated.") : LifecycleRedirect(licenseId, "error", result.Error);
}).RequireAuthorization(Permissions.LicensesUpdate).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapPost("/licenses/{licenseId}/activations/{activationId}/deactivate", async (
    string licenseId, string activationId, HttpRequest request, LicenseStore store, HttpContext context, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    if (!PostedConfirmation(form)) return LifecycleRedirect(licenseId, "error", "Explicit deactivation confirmation is required.");
    if (!long.TryParse(form["Version"], out var version)) return LifecycleRedirect(licenseId, "error", "The page version is invalid. Reload and retry.");
    var result = await store.AdminDeactivateAsync(activationId, form["Reason"], version,
        context.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow, context.TraceIdentifier, ct);
    return result.Success ? LifecycleRedirect(licenseId, "notice", "Activation deactivated. The license is available for transfer.") : LifecycleRedirect(licenseId, "error", result.Error);
}).RequireAuthorization(Permissions.ActivationsManage).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAdditionalIdentityEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

await app.RunAsync();

static async Task<IResult> SignedResponse(
    LicenseStore.ActiveActivation activation,
    LicenseStore store,
    LicenseEnvelopeSigner signer,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    var signed = signer.Sign(await store.CreateLicenseAsync(activation, now, cancellationToken));
    var parsed = SoftwareLicensing.LicenseVerifier.Verify(signed.ToJsonString()).Data.Activation!;
    return Results.Ok(new ActivationResponse(
        activation.LicenseId,
        activation.ActivationId,
        "active",
        signed.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
        parsed.RefreshAfter,
        parsed.LeaseExpiresAt));
}

static IResult Problem<T>(StoreResult<T> result) => Results.Problem(
    title: result.StatusCode switch
    {
        400 => "Invalid request",
        401 => "Authentication failed",
        403 => "Operation forbidden",
        404 => "Resource not found",
        409 => "License state conflict",
        _ => "Request failed"
    },
    detail: result.Error,
    statusCode: result.StatusCode);

static IResult IssueProblem(StoreResult<LicenseStore.IssuedLicense> result) =>
    result.StatusCode == StatusCodes.Status400BadRequest && result.Field is not null
        ? Results.ValidationProblem(new Dictionary<string, string[]> { [result.Field] = [result.Error ?? "Invalid value."] })
        : Problem(result);

static bool PostedConfirmation(IFormCollection form) =>
    string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase)
    || string.Equals(form["Confirmed"], "on", StringComparison.OrdinalIgnoreCase);

static bool TryReadIfMatch(HttpRequest request, out long version)
{
    var value = request.Headers.IfMatch.FirstOrDefault()?.Trim();
    return long.TryParse(value?.Trim('"'), out version);
}

static IResult LifecycleRedirect(string licenseId, string key, string? value) =>
    Results.Redirect($"/licenses/{Uri.EscapeDataString(licenseId)}?{key}={Uri.EscapeDataString(value ?? "Request failed.")}");

static async Task<bool> ValidAntiforgeryAsync(IAntiforgery antiforgery, HttpContext context)
{
    if (context.User.HasClaim("amr", "api_key"))
        return true;
    try { await antiforgery.ValidateRequestAsync(context); return true; }
    catch (AntiforgeryValidationException) { return false; }
}

static IResult AntiforgeryProblem() => Results.Problem(
    title: "Invalid antiforgery token", detail: "A valid antiforgery token is required.", statusCode: 400);

public partial class Program;
