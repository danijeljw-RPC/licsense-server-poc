using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LicenseServer.Data;
using Microsoft.Extensions.DependencyInjection;
using SoftwareLicensing;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "Baseline")]
public sealed class LicensingFlowTests(PostgresWebFixture fixture)
{
    [Fact]
    public async Task ActivationConflictRefreshTransferRevocationAndOfflineSignaturesWork()
    {
        var client = fixture.Factory.CreateClient();
        var device1 = new string('A', 64);
        var first = Request(device1, "online");
        var activated = await client.PostAsJsonAsync("/api/v1/licenses/LIC-POC-0001/activate", first);
        activated.EnsureSuccessStatusCode();
        var response = await activated.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.Equal("LIC-POC-0001", response.LicenseId);
        Assert.NotNull(LicenseVerifier.Verify(response.SignedLicense));

        var second = await client.PostAsJsonAsync("/api/v1/licenses/LIC-POC-0001/activate", Request(new string('B', 64), "online"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var credentials = new ActivationCredentialRequest(first.ActivationToken, device1);
        var refresh = await client.PostAsJsonAsync($"/api/v1/activations/{response.ActivationId}/refresh", credentials);
        refresh.EnsureSuccessStatusCode();
        var refreshed = await refresh.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.Equal("LIC-POC-0001", refreshed.LicenseId);
        Assert.NotNull(LicenseVerifier.Verify(refreshed.SignedLicense));

        var deactivated = await client.PostAsJsonAsync($"/api/v1/activations/{response.ActivationId}/deactivate", credentials);
        deactivated.EnsureSuccessStatusCode();
        Assert.Equal("LIC-POC-0001", (await deactivated.Content.ReadFromJsonAsync<DeactivationResponse>())?.LicenseId);
        var offlineRequest = Request(new string('B', 64), "offline");
        var offline = await client.PostAsJsonAsync("/api/v1/licenses/LIC-POC-0001/activate", offlineRequest);
        offline.EnsureSuccessStatusCode();
        var offlineResponse = await offline.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();
        Assert.Null(offlineResponse.LeaseExpiresAt);
        Assert.NotNull(LicenseVerifier.Verify(offlineResponse.SignedLicense));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LicenseStore>();
        Assert.True((await store.RevokeAsync("LIC-POC-0001", "automated integration test", "test-admin", DateTimeOffset.UtcNow)).Success);
        var rejected = await client.PostAsJsonAsync($"/api/v1/activations/{offlineResponse.ActivationId}/validate", new ActivationCredentialRequest(offlineRequest.ActivationToken, new string('B', 64)));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.NotNull(LicenseVerifier.Verify(offlineResponse.SignedLicense));
    }

    private static ActivateRequest Request(string device, string mode) => new(
        Guid.NewGuid().ToString("D"),
        "POC-DEMO-ACTIVATION-CODE",
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        mode,
        new DeviceRequest(DeviceIdentity.Scheme, device, "TEST-DEVICE"));
}
