using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SoftwareLicensing;

namespace LicenseServer.Tests;

/// <summary>
/// Covers <see cref="LicenseEnvelope"/>, the single signing implementation shared by the server's
/// online path and the offline LicenseGenerator CLI. The point of the shared helper is that the two
/// cannot drift, so these tests pin the envelope shape and the canonicalization rules the signature
/// depends on - not just "a signature comes out".
/// </summary>
public sealed class LicenseEnvelopeTests
{
    private static JsonObject BuildLicense() => new()
    {
        ["licenseId"] = "LIC-ENVELOPE-TEST",
        ["customer"] = "Envelope Test Pty Ltd",
        ["issuedAt"] = "2026-01-01T00:00:00Z",
        ["entitlements"] = new JsonArray(new JsonObject
        {
            ["product"] = "gcexp",
            ["edition"] = "business",
            ["licenseType"] = "perpetual",
            ["seats"] = 3
        })
    };

    private static (ECDsa Key, Dictionary<string, string> Trusted) NewKey(string keyId)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [keyId] = key.ExportSubjectPublicKeyInfoPem()
        });
    }

    [Fact]
    public void SignProducesTheAgreedEnvelopeShape()
    {
        var (key, _) = NewKey("shape-key");
        using var _key = key;

        var envelope = LicenseEnvelope.Sign(BuildLicense(), "shape-key", key);

        Assert.Equal(LicenseConstants.Format, envelope["format"]!.GetValue<string>());
        Assert.Equal(LicenseConstants.Algorithm, envelope["algorithm"]!.GetValue<string>());
        Assert.Equal("shape-key", envelope["keyId"]!.GetValue<string>());
        Assert.NotNull(envelope["license"] as JsonObject);
        Assert.NotEmpty(envelope["signature"]!.GetValue<string>());
        // Exactly the five envelope fields LicenseVerifier accepts - an extra one would be rejected
        // by its unsupported-field check at verification time.
        Assert.Equal(5, envelope.Count);
    }

    [Fact]
    public void SignedEnvelopeVerifiesUnderTheSharedVerifier()
    {
        var (key, trusted) = NewKey("verify-key");
        using var _key = key;

        var envelope = LicenseEnvelope.Sign(BuildLicense(), "verify-key", key);
        var verified = LicenseVerifier.Verify(envelope.ToJsonString(), trusted);

        Assert.Equal("verify-key", verified.KeyId);
        Assert.Equal("LIC-ENVELOPE-TEST", verified.Data.LicenseId);
    }

    [Fact]
    public void SignDoesNotMutateTheCallersLicenseObject()
    {
        var (key, _) = NewKey("no-mutate-key");
        using var _key = key;
        var license = BuildLicense();

        var envelope = LicenseEnvelope.Sign(license, "no-mutate-key", key);

        // The envelope holds a deep clone, so the caller's object gains no envelope fields and can
        // be handed to a second Sign call (e.g. re-signing under a rotated key) unchanged.
        Assert.Null(license.Parent);
        Assert.Equal(4, license.Count);
        Assert.NotSame(license, envelope["license"]);
    }

    [Fact]
    public void KeyIdIsCoveredBySignatureSoItCannotBeSwappedAfterwards()
    {
        var (key, trusted) = NewKey("bound-key");
        using var _key = key;
        var envelope = LicenseEnvelope.Sign(BuildLicense(), "bound-key", key);

        var tampered = envelope.DeepClone().AsObject();
        tampered["keyId"] = "some-other-key";
        trusted["some-other-key"] = trusted["bound-key"];

        Assert.Throws<LicenseValidationException>(
            () => LicenseVerifier.Verify(tampered.ToJsonString(), trusted));
    }

    [Fact]
    public void LicensePayloadIsCoveredBySignature()
    {
        var (key, trusted) = NewKey("payload-key");
        using var _key = key;
        var envelope = LicenseEnvelope.Sign(BuildLicense(), "payload-key", key);

        var tampered = envelope.DeepClone().AsObject();
        tampered["license"]!["entitlements"]![0]!["seats"] = 5000;

        Assert.Throws<LicenseValidationException>(
            () => LicenseVerifier.Verify(tampered.ToJsonString(), trusted));
    }

    [Fact]
    public void PropertyOrderOfTheInputDoesNotChangeWhatIsSigned()
    {
        // Canonical serialization sorts property names ordinally before hashing. This is what lets
        // the server (which builds its licence object programmatically) and the CLI (which reads
        // one from an operator-authored file) sign the same licence to mutually verifiable bytes.
        var (key, trusted) = NewKey("order-key");
        using var _key = key;

        var reordered = new JsonObject
        {
            ["entitlements"] = new JsonArray(new JsonObject
            {
                ["seats"] = 3,
                ["licenseType"] = "perpetual",
                ["edition"] = "business",
                ["product"] = "gcexp"
            }),
            ["issuedAt"] = "2026-01-01T00:00:00Z",
            ["customer"] = "Envelope Test Pty Ltd",
            ["licenseId"] = "LIC-ENVELOPE-TEST"
        };

        var fromOrdered = LicenseEnvelope.Sign(BuildLicense(), "order-key", key);
        var fromReordered = LicenseEnvelope.Sign(reordered, "order-key", key);

        // ECDSA signatures are randomized, so the two signature strings differ; what must match is
        // that each verifies, and that the canonical bytes being signed are identical.
        Assert.NotNull(LicenseVerifier.Verify(fromOrdered.ToJsonString(), trusted));
        Assert.NotNull(LicenseVerifier.Verify(fromReordered.ToJsonString(), trusted));

        fromOrdered.Remove("signature");
        fromReordered.Remove("signature");
        Assert.Equal(CanonicalJson.Serialize(fromOrdered), CanonicalJson.Serialize(fromReordered));
    }

    [Fact]
    public void SchemaInvalidLicenceIsRejectedBeforeAnythingIsSigned()
    {
        var (key, _) = NewKey("schema-key");
        using var _key = key;

        // Missing the required "entitlements" array: signing this would produce an artifact that
        // looks authentic but that every validator rejects.
        var invalid = new JsonObject
        {
            ["licenseId"] = "LIC-INVALID",
            ["customer"] = "Envelope Test Pty Ltd",
            ["issuedAt"] = "2026-01-01T00:00:00Z"
        };

        Assert.Throws<LicenseSchemaException>(() => LicenseEnvelope.Sign(invalid, "schema-key", key));
    }

    [Fact]
    public void BlankKeyIdIsRejected()
    {
        var (key, _) = NewKey("blank-key");
        using var _key = key;

        Assert.Throws<ArgumentException>(() => LicenseEnvelope.Sign(BuildLicense(), "  ", key));
    }
}
