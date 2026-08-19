using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Globalization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftwareLicensing;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Baseline")]
public sealed class LicensingFlowTests(PostgresWebFixture fixture)
{
    [Fact]
    public async Task OneSeatLicenseAcceptsFirstMachineRejectsSecondAndAllowsReplacementAfterDeactivation()
    {
        var client = fixture.Factory.CreateClient();
        var demoLicenseId = await RoadmapTestSupport.DemoLicenseIdAsync(fixture);
        var first = Request(new string('A', 64), "online");

        using var activated = await client.PostAsJsonAsync($"/api/v1/licenses/{demoLicenseId}/activate", first);
        Assert.True(activated.IsSuccessStatusCode,
            $"Activation returned {(int)activated.StatusCode}: {await activated.Content.ReadAsStringAsync()}");
        var response = await activated.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.Equal(demoLicenseId, response.LicenseId);
        Assert.NotNull(await RoadmapTestSupport.VerifySignedLicenseAsync(fixture, response.SignedLicense));

        using var second = await client.PostAsJsonAsync($"/api/v1/licenses/{demoLicenseId}/activate", Request(new string('B', 64), "online"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("Activation limit reached", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var credentials = new ActivationCredentialRequest(first.ActivationToken, first.Device!.DeviceId);
        using var refresh = await client.PostAsJsonAsync($"/api/v1/activations/{response.ActivationId}/refresh", credentials);
        refresh.EnsureSuccessStatusCode();
        var refreshed = await refresh.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.Equal(demoLicenseId, refreshed.LicenseId);
        Assert.NotNull(await RoadmapTestSupport.VerifySignedLicenseAsync(fixture, refreshed.SignedLicense));

        using var deactivated = await client.PostAsJsonAsync($"/api/v1/activations/{response.ActivationId}/deactivate", credentials);
        deactivated.EnsureSuccessStatusCode();
        Assert.Equal(demoLicenseId, (await deactivated.Content.ReadFromJsonAsync<DeactivationResponse>())?.LicenseId);

        var offlineRequest = Request(new string('B', 64), "offline");
        using var offline = await client.PostAsJsonAsync($"/api/v1/licenses/{demoLicenseId}/activate", offlineRequest);
        offline.EnsureSuccessStatusCode();
        var offlineResponse = await offline.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.Null(offlineResponse.LeaseExpiresAt);
        Assert.NotNull(await RoadmapTestSupport.VerifySignedLicenseAsync(fixture, offlineResponse.SignedLicense));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        Assert.True((await store.RevokeAsync(demoLicenseId, "automated integration test", "test-admin", DateTimeOffset.UtcNow)).Success);
        using var rejected = await client.PostAsJsonAsync(
            $"/api/v1/activations/{offlineResponse.ActivationId}/validate",
            new ActivationCredentialRequest(offlineRequest.ActivationToken, offlineRequest.Device!.DeviceId));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.NotNull(await RoadmapTestSupport.VerifySignedLicenseAsync(fixture, offlineResponse.SignedLicense));
    }

    [Fact]
    public async Task FortySeatLicenseAcceptsFortyDistinctActiveMachinesAndRejectsTheFortyFirst()
    {
        const string suffix = "forty-seat";
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, suffix, seats: 40);
        var client = fixture.Factory.CreateClient();

        for (var i = 0; i < 40; i++)
        {
            var request = Request(DeviceId(i), "offline", ActivationCodeFor(suffix));
            var response = await ActivateAsync(client, license.LicenseId, request);
            Assert.NotNull(await RoadmapTestSupport.VerifySignedLicenseAsync(fixture, response.SignedLicense));
        }

        using var rejected = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{license.LicenseId}/activate",
            Request(DeviceId(40), "offline", ActivationCodeFor(suffix)));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("40 of 40", await rejected.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await AssertActiveActivationCountAsync(license.Id, 40);
    }

    [Fact]
    public async Task DeactivatingOneOfFortyPermitsAReplacementActivation()
    {
        const string suffix = "replacement-seat";
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, suffix, seats: 40);
        var client = fixture.Factory.CreateClient();
        var activations = new List<(ActivateRequest Request, ActivationResponse Response)>();

        for (var i = 0; i < 40; i++)
        {
            var request = Request(DeviceId(i), "offline", ActivationCodeFor(suffix));
            var response = await ActivateAsync(client, license.LicenseId, request);
            activations.Add((request, response));
        }

        var released = activations[16];
        using var deactivated = await client.PostAsJsonAsync(
            $"/api/v1/activations/{released.Response.ActivationId}/deactivate",
            new ActivationCredentialRequest(released.Request.ActivationToken, released.Request.Device!.DeviceId));
        deactivated.EnsureSuccessStatusCode();

        var replacement = await ActivateAsync(
            client,
            license.LicenseId,
            Request(DeviceId(99), "offline", ActivationCodeFor(suffix)));
        Assert.NotNull(await RoadmapTestSupport.VerifySignedLicenseAsync(fixture, replacement.SignedLicense));
        await AssertActiveActivationCountAsync(license.Id, 40);
    }

    [Fact]
    public async Task SameMachineCannotConsumeTwoActiveSeatsAndCanRecoverItsExistingActivation()
    {
        const string suffix = "same-machine";
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, suffix, seats: 2);
        var client = fixture.Factory.CreateClient();
        var first = Request(DeviceId(1), "offline", ActivationCodeFor(suffix));

        var activated = await ActivateAsync(client, license.LicenseId, first);

        using var recovered = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{license.LicenseId}/activate",
            first with { RequestId = Guid.NewGuid().ToString("D") });
        recovered.EnsureSuccessStatusCode();
        Assert.Equal(activated.ActivationId, (await recovered.Content.ReadFromJsonAsync<ActivationResponse>())?.ActivationId);

        using var conflicting = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{license.LicenseId}/activate",
            Request(first.Device!.DeviceId!, "offline", ActivationCodeFor(suffix)));
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Contains("already active on device", await conflicting.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await AssertActiveActivationCountAsync(license.Id, 1);
    }

    [Fact]
    public async Task SameRequestIdRetryIsIdempotentAndConflictingReuseIsRejected()
    {
        const string suffix = "request-id";
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, suffix, seats: 2);
        var client = fixture.Factory.CreateClient();
        var requestId = Guid.NewGuid().ToString("D");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var request = Request(DeviceId(5), "online", ActivationCodeFor(suffix), requestId, token);

        var activated = await ActivateAsync(client, license.LicenseId, request);

        using var retried = await client.PostAsJsonAsync($"/api/v1/licenses/{license.LicenseId}/activate", request);
        retried.EnsureSuccessStatusCode();
        Assert.Equal(activated.ActivationId, (await retried.Content.ReadFromJsonAsync<ActivationResponse>())?.ActivationId);

        using var conflicting = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{license.LicenseId}/activate",
            request with { Device = new DeviceRequest(DeviceIdentity.Scheme, DeviceId(6), "TEST-DEVICE-6") });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Contains("request ID", await conflicting.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentActivationAtTheFinalAvailableSeatCannotExceedTheLimit()
    {
        const string suffix = "concurrent-seat";
        var license = await RoadmapTestSupport.AddLicenseAsync(fixture, suffix, seats: 1);
        var requestA = Request(DeviceId(21), "online", ActivationCodeFor(suffix));
        var requestB = Request(DeviceId(22), "online", ActivationCodeFor(suffix));

        using var clientA = fixture.Factory.CreateClient();
        using var clientB = fixture.Factory.CreateClient();
        var taskA = clientA.PostAsJsonAsync($"/api/v1/licenses/{license.LicenseId}/activate", requestA);
        var taskB = clientB.PostAsJsonAsync($"/api/v1/licenses/{license.LicenseId}/activate", requestB);

        await Task.WhenAll(taskA, taskB);

        using var responseA = await taskA;
        using var responseB = await taskB;
        var statuses = new[] { responseA.StatusCode, responseB.StatusCode };
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Conflict));

        await AssertActiveActivationCountAsync(license.Id, 1);
    }

    private async Task AssertActiveActivationCountAsync(Guid licenseRecordId, int expected)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var count = await db.Activations.CountAsync(item => item.LicenseRecordId == licenseRecordId && item.DeactivatedAt == null);
        Assert.Equal(expected, count);
    }

    private static async Task<ActivationResponse> ActivateAsync(HttpClient client, string licenseId, ActivateRequest request)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/activate", request);
        Assert.True(response.IsSuccessStatusCode,
            $"Activation returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
    }

    private static string ActivationCodeFor(string suffix) => $"PHASE0-{suffix}-ACTIVATION-CODE";

    private static string DeviceId(int index) => index.ToString("X", CultureInfo.InvariantCulture).PadLeft(64, '0');

    private static ActivateRequest Request(
        string device,
        string mode,
        string activationCode = "POC-DEMO-ACTIVATION-CODE",
        string? requestId = null,
        string? activationToken = null) => new(
            requestId ?? Guid.NewGuid().ToString("D"),
            activationCode,
            activationToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            mode,
            new DeviceRequest(DeviceIdentity.Scheme, device, $"TEST-DEVICE-{device[^4..]}"));
}
