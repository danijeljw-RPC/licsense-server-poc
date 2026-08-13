using LicenseServer;
using LicenseServer.Components;
using LicenseServer.Components.Account;
using LicenseServer.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies(options =>
    {
        options.ApplicationCookie!.Configure(cookie =>
        {
            cookie.Cookie.Name = "__Host-LicenseServer.Auth";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = true;
            cookie.LoginPath = "/Account/Login";
            cookie.AccessDeniedPath = "/Account/AccessDenied";
        });
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Administrator", policy => policy.RequireRole(DatabaseInitializer.AdministratorRole));

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
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddScoped<LicenseStore>();
builder.Services.AddScoped<AdminDataService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<LicenseEnvelopeSigner>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql");

var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("LicenseServer");
}

var app = builder.Build();

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
app.UseAuthorization();
app.UseAntiforgery();

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && !context.Request.Path.StartsWithSegments("/Account")
        && !context.Request.Path.StartsWithSegments("/health"))
    {
        var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(context.User);
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

app.MapPost("/api/v1/licenses/{licenseId}/activate", async (
    string licenseId, ActivateRequest request, LicenseStore store, LicenseEnvelopeSigner signer, CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var result = await store.ActivateAsync(licenseId, request, now, cancellationToken);
    return result.Success
        ? await SignedResponse(result.Value!, store, signer, now, cancellationToken)
        : Problem(result);
}).AllowAnonymous();

app.MapPost("/api/v1/activations/{activationId}/validate", async (
    string activationId, ActivationCredentialRequest request, LicenseStore store, CancellationToken cancellationToken) =>
{
    var result = await store.AuthorizeAsync(activationId, request, cancellationToken);
    return result.Success
        ? Results.Ok(new ValidationResponse(result.Value!.LicenseId, activationId, "active", DateTimeOffset.UtcNow))
        : Problem(result);
}).AllowAnonymous();

app.MapPost("/api/v1/activations/{activationId}/refresh", async (
    string activationId, ActivationCredentialRequest request, LicenseStore store, LicenseEnvelopeSigner signer, CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var result = await store.RefreshAsync(activationId, request, now, cancellationToken);
    return result.Success
        ? await SignedResponse(result.Value!, store, signer, now, cancellationToken)
        : Problem(result);
}).AllowAnonymous();

app.MapPost("/api/v1/activations/{activationId}/deactivate", async (
    string activationId, ActivationCredentialRequest request, LicenseStore store, CancellationToken cancellationToken) =>
{
    var result = await store.DeactivateAsync(activationId, request, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
    return result.Success
        ? Results.Ok(new DeactivationResponse(result.Value!.LicenseId, activationId, "deactivated", DateTimeOffset.UtcNow))
        : Problem(result);
}).AllowAnonymous();

var adminApi = app.MapGroup("/api/v1/admin").RequireAuthorization("Administrator");
adminApi.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
});
adminApi.MapGet("/licenses/{licenseId}", async (string licenseId, AdminDataService data, CancellationToken ct) =>
    await data.GetLicenseAsync(licenseId, ct) is { } item ? Results.Ok(item) : Results.NotFound());
adminApi.MapPost("/licenses/{licenseId}/revoke", async (
    string licenseId, RevokeRequest request, LicenseStore store, HttpContext httpContext, CancellationToken ct) =>
{
    var result = await store.RevokeAsync(licenseId, request.Reason, httpContext.User.Identity?.Name ?? "unknown", DateTimeOffset.UtcNow, ct);
    return result.Success ? Results.Ok(new { licenseId, status = "revoked" }) : Problem(result);
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

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

public partial class Program;
