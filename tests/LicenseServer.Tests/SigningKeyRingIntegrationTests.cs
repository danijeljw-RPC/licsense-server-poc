using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftwareLicensing;

namespace LicenseServer.Tests;

[Collection(PostgresTestSuite.Name)]
[Trait("Suite", "SigningKeyRing")]
public sealed class SigningKeyRingIntegrationTests(PostgresWebFixture fixture)
{
    [Fact]
    public async Task DefaultKeyIsUsedWhenNoKeyIsRequested()
    {
        // A freshly created license, not the shared demo license: LicensingFlowTests revokes the
        // demo license as part of its own run, and test-class ordering within a shared collection
        // fixture is not guaranteed, so reusing it here would be an order-dependent flake.
        var licenseRecord = await RoadmapTestSupport.AddLicenseAsync(fixture, "default-key-check");
        var client = fixture.Factory.CreateClient();
        var device = new string('C', 64);
        var request = new ActivateRequest(
            Guid.NewGuid().ToString("D"), "PHASE0-default-key-check-ACTIVATION-CODE",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "offline",
            new DeviceRequest("os-machine-id-sha256-v1", device, "test-device"));
        var activated = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseRecord.LicenseId}/activate", request);
        activated.EnsureSuccessStatusCode();
        var response = await activated.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var ring = scope.ServiceProvider.GetRequiredService<ILicenseKeyRing>();
        Assert.Equal(ring.DefaultKeyId, LicenseVerifier.Verify(response.SignedLicense).KeyId);
    }

    [Fact]
    public async Task ActivatingWithNoUsableSigningKeyFailsWithoutCreatingAnActivationRow()
    {
        // Regression test: ActivateAsync used to commit the new Activation row before ever
        // attempting to sign the response artifact, so a signing failure discovered afterward
        // (e.g. the default key was just revoked) still left the activation committed with no
        // artifact ever issued - and its request ID permanently unable to retry, since a retry
        // would find the license already active. The store now checks ILicenseSigner.CanSign
        // before mutating anything, so a request that can never be signed leaves no trace at all.
        var licenseRecord = await RoadmapTestSupport.AddLicenseAsync(fixture, "no-usable-key-check");
        var client = fixture.Factory.CreateClient();
        var device = new string('E', 64);
        var request = new ActivateRequest(
            Guid.NewGuid().ToString("D"), "PHASE0-no-usable-key-check-ACTIVATION-CODE",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "offline",
            new DeviceRequest("os-machine-id-sha256-v1", device, "test-device"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var keyRing = scope.ServiceProvider.GetRequiredService<SigningKeyRingService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            await keyRing.RevokeAsync("primary-2026", "test: leave no usable default key", "test-admin");
            Assert.Null(keyRing.DefaultKeyId);

            var activated = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseRecord.LicenseId}/activate", request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, activated.StatusCode);

            var activationCount = await db.Activations.CountAsync(x => x.LicenseRecordId == licenseRecord.Id);
            Assert.Equal(0, activationCount);
        }
        finally
        {
            await using var restoreScope = fixture.Factory.Services.CreateAsyncScope();
            var restoreDb = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await restoreDb.SigningKeys.SingleAsync(x => x.KeyId == "primary-2026");
            row.RevokedAt = null;
            row.RevokedBy = null;
            row.RevocationReason = null;
            row.IsDefault = true;
            await restoreDb.SaveChangesAsync();
            await keyRing.ReloadAsync();
        }

        // The same request now succeeds once a default is restored, proving the earlier failed
        // attempt never consumed its request ID.
        var retried = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseRecord.LicenseId}/activate", request);
        retried.EnsureSuccessStatusCode();
    }

    [Fact]
    public void ExplicitlySelectedKeyIsUsedAndUnknownKeyFailsClosed()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();
        var license = BuildLicense("explicit-key");

        var withDefault = signer.Sign(license, requestedKeyId: null);
        Assert.True(withDefault.Success);

        var withSecondary = signer.Sign(license, requestedKeyId: "secondary-2026");
        Assert.True(withSecondary.Success);
        Assert.Equal("secondary-2026", withSecondary.Envelope!["keyId"]!.GetValue<string>());

        var unknown = signer.Sign(license, requestedKeyId: "does-not-exist");
        Assert.False(unknown.Success);
        Assert.Equal("unknown_key", unknown.ErrorCode);
    }

    [Fact]
    public void CanSignMatchesWhetherSignWouldSucceedWithoutSigningAnything()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();

        Assert.True(signer.CanSign(null));
        Assert.True(signer.CanSign("secondary-2026"));
        Assert.False(signer.CanSign("does-not-exist"));
    }

    [Fact]
    public void SignedLicenseVerifiesWithItsOwnKeyAndTamperingInvalidatesIt()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();
        var verifier = scope.ServiceProvider.GetRequiredService<ILicenseVerifier>();
        var license = BuildLicense("cross-key");

        var signed = signer.Sign(license, "secondary-2026");
        Assert.True(signed.Success);
        var signedJson = signed.Envelope!.ToJsonString();

        var verified = verifier.Verify(signedJson);
        Assert.Equal("secondary-2026", verified.KeyId);

        // Tampering with keyId post-signature invalidates the signature, matching the envelope's
        // own signed-field guarantee (keyId is covered by the signature).
        var tampered = JsonNode.Parse(signedJson)!.AsObject();
        tampered["keyId"] = "primary-2026";
        Assert.Throws<LicenseValidationException>(() => verifier.Verify(tampered.ToJsonString()));
    }

    [Fact]
    public async Task RevokingAKeyFailsKeyRingVerificationButNotTheEmbeddedTrustedPublicKeysPath()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var keyRing = scope.ServiceProvider.GetRequiredService<SigningKeyRingService>();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();
        var verifier = scope.ServiceProvider.GetRequiredService<ILicenseVerifier>();
        var license = BuildLicense("revoke-me");

        var signed = signer.Sign(license, "secondary-2026");
        Assert.True(signed.Success);
        var signedJson = signed.Envelope!.ToJsonString();
        Assert.Equal("secondary-2026", verifier.Verify(signedJson).KeyId);

        try
        {
            await keyRing.RevokeAsync("secondary-2026", "compromised in test", "test-admin");

            Assert.Throws<LicenseValidationException>(() => verifier.Verify(signedJson));
            // LicenseValidator's embedded TrustedPublicKeys trust store has no concept of revocation
            // and is explicitly out of scope for this feature: the same signature still validates.
            Assert.NotNull(LicenseVerifier.Verify(signedJson));

            var info = keyRing.Find("secondary-2026");
            Assert.NotNull(info);
            Assert.Equal(SigningKeyStatus.Revoked, info!.Status);
            Assert.False(info.CanSign);
            Assert.False(info.CanVerify);

            var afterRevoke = signer.Sign(license, "secondary-2026");
            Assert.False(afterRevoke.Success);
            Assert.Equal("cannot_sign", afterRevoke.ErrorCode);
        }
        finally
        {
            // Restore ring state (revocation is permanent through the public API) so other tests in
            // this shared-fixture collection that rely on secondary-2026 keep running independently.
            await using var restoreScope = fixture.Factory.Services.CreateAsyncScope();
            var db = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.SigningKeys.SingleAsync(x => x.KeyId == "secondary-2026");
            row.RevokedAt = null;
            row.RevokedBy = null;
            row.RevocationReason = null;
            await db.SaveChangesAsync();
            await keyRing.ReloadAsync();
        }
    }

    [Fact]
    public async Task RevokingTheDefaultKeyLeavesNoDefaultRatherThanSilentlyReelectingTheBootstrapSeed()
    {
        // Regression test: an earlier implementation re-ran the "no row has IsDefault=true, seed the
        // bootstrap default from configuration" check on every reload, not just the very first one
        // against an empty table. That meant revoking the default key - which intentionally clears
        // IsDefault and must never auto-substitute another key - got silently undone on the very next
        // reload, because Licensing:DefaultSigningKey ("primary-2026") never stops being present in
        // configuration. Revocation must fail closed: no default at all until an admin acts.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var keyRing = scope.ServiceProvider.GetRequiredService<SigningKeyRingService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal("primary-2026", keyRing.DefaultKeyId);

        try
        {
            await keyRing.RevokeAsync("primary-2026", "regression test: revoking the default key", "test-admin");

            Assert.Null(keyRing.DefaultKeyId);
            Assert.Equal(0, await db.SigningKeys.CountAsync(x => x.IsDefault));

            // A second reload (the periodic timer's equivalent) must not resurrect a default either.
            await keyRing.ReloadAsync();
            Assert.Null(keyRing.DefaultKeyId);
        }
        finally
        {
            await using var restoreScope = fixture.Factory.Services.CreateAsyncScope();
            var restoreDb = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await restoreDb.SigningKeys.SingleAsync(x => x.KeyId == "primary-2026");
            row.RevokedAt = null;
            row.RevokedBy = null;
            row.RevocationReason = null;
            row.IsDefault = true;
            await restoreDb.SaveChangesAsync();
            await keyRing.ReloadAsync();
            Assert.Equal("primary-2026", keyRing.DefaultKeyId);
        }
    }

    [Fact]
    public async Task SetDefaultRotatesTheDefaultKeyAndLeavesExactlyOneDefaultRow()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var keyRing = scope.ServiceProvider.GetRequiredService<SigningKeyRingService>();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var license = BuildLicense("rotate-default");

        Assert.Equal("primary-2026", keyRing.DefaultKeyId);

        try
        {
            await keyRing.SetDefaultAsync("secondary-2026", "test-admin");
            Assert.Equal("secondary-2026", keyRing.DefaultKeyId);

            var signedAfterRotate = signer.Sign(license, requestedKeyId: null);
            Assert.True(signedAfterRotate.Success);
            Assert.Equal("secondary-2026", signedAfterRotate.Envelope!["keyId"]!.GetValue<string>());

            Assert.Equal(1, await db.SigningKeys.CountAsync(x => x.IsDefault));
        }
        finally
        {
            await keyRing.SetDefaultAsync("primary-2026", "test-admin");
            Assert.Equal("primary-2026", keyRing.DefaultKeyId);
        }
    }

    [Fact]
    public async Task SetDefaultAndRevokePublishTheNewSnapshotBeforeReturning()
    {
        // The design's original hot-reload section listed only two things that republish the
        // in-memory snapshot: the filesystem watcher and the periodic timer. Neither is triggered by
        // set-default or revoke, which write straight to Postgres - so between a successful admin
        // mutation and the next timer tick the ring would still be serving the old answer. Signing
        // would keep using the previous default key after set-default reported success, and
        // verification would keep accepting a just-revoked key after revoke reported success, which
        // contradicts fail-closed revocation.
        //
        // Both mutations therefore await ReloadAsync before returning. Every assertion below runs
        // immediately after its await, with no sleep and no explicit reload; the periodic timer is
        // floored at 5 seconds and defaults to 30, so nothing but that synchronous republish could
        // have updated the snapshot in between. Deleting either ReloadAsync call fails this test.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var keyRing = scope.ServiceProvider.GetRequiredService<SigningKeyRingService>();
        var signer = scope.ServiceProvider.GetRequiredService<ILicenseSigner>();
        var verifier = scope.ServiceProvider.GetRequiredService<ILicenseVerifier>();
        var license = BuildLicense("no-stale-window");

        Assert.Equal("primary-2026", keyRing.DefaultKeyId);

        try
        {
            await keyRing.SetDefaultAsync("secondary-2026", "test-admin");

            Assert.Equal("secondary-2026", keyRing.DefaultKeyId);
            var signedWithNewDefault = signer.Sign(license, requestedKeyId: null);
            Assert.True(signedWithNewDefault.Success);
            Assert.Equal("secondary-2026", signedWithNewDefault.Envelope!["keyId"]!.GetValue<string>());

            var signedJson = signedWithNewDefault.Envelope.ToJsonString();
            Assert.Equal("secondary-2026", verifier.Verify(signedJson).KeyId);

            await keyRing.RevokeAsync("secondary-2026", "no-stale-window check", "test-admin");

            Assert.Throws<LicenseValidationException>(() => verifier.Verify(signedJson));
            Assert.False(signer.Sign(license, "secondary-2026").Success);
            // Revoking the default clears it rather than substituting another key, so the very next
            // default-key signing attempt fails closed instead of silently using a different key.
            Assert.Null(keyRing.DefaultKeyId);
            Assert.Equal("no_default_key", signer.Sign(license, requestedKeyId: null).ErrorCode);
        }
        finally
        {
            await using var restoreScope = fixture.Factory.Services.CreateAsyncScope();
            var restoreDb = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await restoreDb.SigningKeys.SingleAsync(x => x.KeyId == "secondary-2026");
            row.RevokedAt = null;
            row.RevokedBy = null;
            row.RevocationReason = null;
            await restoreDb.SaveChangesAsync();
            await keyRing.ReloadAsync();
            // Deliberately no assertion here: a failing assert inside finally would replace the
            // real failure from the try block with a confusing one about restore state.
            await keyRing.SetDefaultAsync("primary-2026", "test-admin");
        }
    }

    [Fact]
    public async Task AdminEndpointsEnforcePermissionsAndListsReflectTheRing()
    {
        var reader = fixture.CreateAuthenticatedClient(false, Permissions.LicensesRead);
        using var list = await reader.GetAsync("/api/v1/admin/signing-keys");
        list.EnsureSuccessStatusCode();
        var keys = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(keys.GetArrayLength() >= 1);

        // A missing-permission POST is rejected before it could ever succeed; matching the existing
        // convention in UserAdministrationTests, this asserts non-success rather than an exact status
        // code, since a request with no antiforgery token can be rejected for either reason first.
        using var forbiddenRescan = await reader.PostAsync("/api/v1/admin/signing-keys/rescan", null);
        Assert.False(forbiddenRescan.IsSuccessStatusCode);

        var manager = fixture.CreateAuthenticatedClient(true, Permissions.SigningKeysManage, Permissions.LicensesRead);
        using var rescan = await RoadmapTestSupport.PostAdminAsync(manager, "/api/v1/admin/signing-keys/rescan", new { });
        Assert.Equal(HttpStatusCode.NoContent, rescan.StatusCode);

        using var badSetDefault = await RoadmapTestSupport.PostAdminAsync(manager, "/api/v1/admin/signing-keys/does-not-exist/set-default", new { });
        Assert.Equal(HttpStatusCode.BadRequest, badSetDefault.StatusCode);
    }

    [Fact]
    public async Task RescanWritesAnAuditRecord()
    {
        // Direct call, exercising the same SigningKeyRingService.RescanAsync method the Blazor page's
        // "Rescan key directory" button calls - see RescanEndpointWritesAnAuditRecord below for the
        // /api/v1/admin/signing-keys/rescan HTTP path, which is a different caller of the same method.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var keyRing = scope.ServiceProvider.GetRequiredService<SigningKeyRingService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await keyRing.RescanAsync("rescan-audit-test-actor");

        var record = Assert.Single(await db.AuditRecords
            .Where(item => item.Actor == "rescan-audit-test-actor")
            .ToListAsync());
        Assert.Equal("signingKey.rescan", record.Action);
        Assert.Equal("signingKey", record.TargetType);
        Assert.Equal("success", record.Result);
    }

    [Fact]
    public async Task RescanEndpointWritesAnAuditRecord()
    {
        // Counts before/after rather than asserting existence: this fixture's database is shared
        // across tests in the collection, and AdminEndpointsEnforcePermissionsAndListsReflectTheRing
        // above writes a "signingKey.rescan" record under this same "phase0-test-operator" actor. An
        // existence check alone would stay green even if this endpoint stopped writing its own
        // record, as long as that other test ran first.
        await using var countScope = fixture.Factory.Services.CreateAsyncScope();
        var countDb = countScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = await countDb.AuditRecords.CountAsync(item =>
            item.Action == "signingKey.rescan" && item.Actor == "phase0-test-operator");

        var manager = fixture.CreateAuthenticatedClient(true, Permissions.SigningKeysManage, Permissions.LicensesRead);
        using var rescan = await RoadmapTestSupport.PostAdminAsync(manager, "/api/v1/admin/signing-keys/rescan", new { });
        Assert.Equal(HttpStatusCode.NoContent, rescan.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var after = await db.AuditRecords.CountAsync(item =>
            item.Action == "signingKey.rescan" && item.Actor == "phase0-test-operator");
        Assert.Equal(before + 1, after);
    }

    private static JsonObject BuildLicense(string suffix) => new()
    {
        ["licenseId"] = $"LIC-TEST-{suffix}",
        ["customer"] = $"Signing key ring test customer ({suffix})",
        ["issuedAt"] = "2026-01-01T00:00:00Z",
        ["entitlements"] = new JsonArray(new JsonObject
        {
            ["product"] = "gcexp",
            ["edition"] = "business",
            ["licenseType"] = "perpetual",
            ["seats"] = 1
        })
    };
}
