using LicenseServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<LicenseStore>();
builder.Services.AddSingleton<LicenseEnvelopeSigner>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/v1/licenses/{licenseId}/activate", (
    string licenseId,
    ActivateRequest request,
    LicenseStore store,
    LicenseEnvelopeSigner signer) =>
{
    if (!IsDemoLicense(licenseId))
        return Results.NotFound();

    var now = DateTimeOffset.UtcNow;
    var result = store.Activate(request, now);
    if (!result.Success)
        return Problem(result);

    return SignedResponse(licenseId, result.Value!, store, signer, now);
});

app.MapPost("/api/v1/activations/{activationId}/validate", (
    string activationId,
    ActivationCredentialRequest request,
    LicenseStore store) =>
{
    var result = store.Authorize(activationId, request);
    return result.Success
        ? Results.Ok(new ValidationResponse(
            "LIC-POC-0001", activationId, "active", DateTimeOffset.UtcNow))
        : Problem(result);
});

app.MapPost("/api/v1/activations/{activationId}/refresh", (
    string activationId,
    ActivationCredentialRequest request,
    LicenseStore store,
    LicenseEnvelopeSigner signer) =>
{
    var result = store.Authorize(activationId, request);
    if (!result.Success)
        return Problem(result);
    if (result.Value!.Mode != "online")
        return Results.Problem("Offline activations do not use leases.", statusCode: 409);

    return SignedResponse("LIC-POC-0001", result.Value, store, signer, DateTimeOffset.UtcNow);
});

app.MapPost("/api/v1/activations/{activationId}/deactivate", (
    string activationId,
    ActivationCredentialRequest request,
    LicenseStore store) =>
{
    var result = store.Deactivate(activationId, request);
    return result.Success
        ? Results.Ok(new DeactivationResponse(
            "LIC-POC-0001", activationId, "deactivated", DateTimeOffset.UtcNow))
        : Problem(result);
});

app.MapGet("/api/v1/admin/licenses/{licenseId}", (
    string licenseId,
    HttpRequest request,
    IConfiguration configuration,
    LicenseStore store) =>
{
    if (!IsAdmin(request, configuration))
        return Results.Unauthorized();
    return IsDemoLicense(licenseId) ? Results.Ok(store.Status()) : Results.NotFound();
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/revoke", (
    string licenseId,
    RevokeRequest revoke,
    HttpRequest request,
    IConfiguration configuration,
    LicenseStore store) =>
{
    if (!IsAdmin(request, configuration))
        return Results.Unauthorized();
    if (!IsDemoLicense(licenseId))
        return Results.NotFound();

    store.Revoke(revoke.Reason);
    return Results.Ok(store.Status());
});

app.Run();

static IResult SignedResponse(
    string licenseId,
    LicenseStore.ActiveActivation activation,
    LicenseStore store,
    LicenseEnvelopeSigner signer,
    DateTimeOffset now)
{
    var signed = signer.Sign(store.CreateLicense(activation, now));
    var parsed = SoftwareLicensing.LicenseVerifier.Verify(signed.ToJsonString()).Data.Activation!;
    return Results.Ok(new ActivationResponse(
        licenseId,
        activation.ActivationId,
        "active",
        signed.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
        parsed.RefreshAfter,
        parsed.LeaseExpiresAt));
}

static IResult Problem<T>(StoreResult<T> result) =>
    Results.Problem(result.Error, statusCode: result.StatusCode);

static bool IsDemoLicense(string value) =>
    string.Equals(value, "LIC-POC-0001", StringComparison.OrdinalIgnoreCase);

static bool IsAdmin(HttpRequest request, IConfiguration configuration)
{
    var expected = configuration["Licensing:AdminKey"] ?? "local-poc-admin-key";
    return request.Headers.TryGetValue("X-Admin-Key", out var supplied)
        && string.Equals(supplied.ToString(), expected, StringComparison.Ordinal);
}

public partial class Program;
