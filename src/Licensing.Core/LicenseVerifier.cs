using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SoftwareLicensing;

public sealed record VerifiedLicense(string KeyId, LicenseData Data);

public sealed class LicenseValidationException(string message) : Exception(message);

public static class LicenseVerifier
{
    private static readonly HashSet<string> EnvelopeFields =
        new(StringComparer.Ordinal)
        {
            "format", "algorithm", "keyId", "license", "signature"
        };

    public static VerifiedLicense VerifyFile(string licensePath)
    {
        return Verify(File.ReadAllText(licensePath));
    }

    public static VerifiedLicense Verify(string signedLicenseJson)
    {
        JsonObject root;

        try
        {
            root = JsonNode.Parse(signedLicenseJson) as JsonObject
                ?? throw new LicenseValidationException(
                    "Licence file must contain a JSON object.");
        }
        catch (LicenseValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LicenseValidationException($"Licence JSON is invalid: {ex.Message}");
        }

        EnsureEnvelopeFields(root);

        var format = GetRequiredString(root, "format");
        var algorithm = GetRequiredString(root, "algorithm");
        var keyId = GetRequiredString(root, "keyId");
        var signatureText = GetRequiredString(root, "signature");

        if (!string.Equals(format, LicenseConstants.Format, StringComparison.Ordinal))
            throw new LicenseValidationException($"Unsupported licence format: {format}");

        if (!string.Equals(algorithm, LicenseConstants.Algorithm, StringComparison.Ordinal))
            throw new LicenseValidationException($"Unsupported algorithm: {algorithm}");

        if (!TrustedPublicKeys.ByKeyId.TryGetValue(keyId, out var publicKeyPem))
            throw new LicenseValidationException($"Unknown signing key: {keyId}");

        if (root["license"] is not JsonObject licenseJson)
            throw new LicenseValidationException("Licence payload is missing.");

        byte[] signature;

        try
        {
            signature = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            throw new LicenseValidationException("Signature is not valid Base64.");
        }

        var unsignedEnvelope = root.DeepClone().AsObject();
        unsignedEnvelope.Remove("signature");

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);

        if (!ecdsa.VerifyData(
                CanonicalJson.Serialize(unsignedEnvelope),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new LicenseValidationException(
                "Cryptographic signature is invalid.");
        }

        try
        {
            return new VerifiedLicense(keyId, LicenseSchema.Parse(licenseJson));
        }
        catch (LicenseSchemaException ex)
        {
            throw new LicenseValidationException(ex.Message);
        }
    }

    public static ProductEntitlement ValidateProduct(
        VerifiedLicense verifiedLicense,
        string product,
        DateOnly? releaseDate = null,
        DateTimeOffset? currentTimeUtc = null,
        LocalDeviceIdentity? currentDevice = null)
    {
        ValidateActivation(verifiedLicense, currentDevice, currentTimeUtc);

        var entitlement = verifiedLicense.Data.Entitlements.FirstOrDefault(x =>
            string.Equals(x.Product, product, StringComparison.OrdinalIgnoreCase));

        if (entitlement is null)
        {
            throw new LicenseValidationException(
                $"Licence does not contain an entitlement for '{product}'.");
        }

        var currentTime = currentTimeUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

        if (entitlement.ExpiresAt is not null
            && currentTime >= entitlement.ExpiresAt.Value)
        {
            throw new LicenseValidationException(
                $"Entitlement for '{entitlement.Product}' expired at " +
                $"{entitlement.ExpiresAt.Value:O}.");
        }

        if (releaseDate is not null
            && entitlement.UpdatesUntil is not null
            && releaseDate.Value > entitlement.UpdatesUntil.Value)
        {
            throw new LicenseValidationException(
                $"Entitlement for '{entitlement.Product}' includes releases through " +
                $"{entitlement.UpdatesUntil.Value:yyyy-MM-dd}; " +
                $"release {releaseDate.Value:yyyy-MM-dd} is not covered.");
        }

        return entitlement;
    }

    public static void ValidateActivation(
        VerifiedLicense verifiedLicense,
        LocalDeviceIdentity? currentDevice = null,
        DateTimeOffset? currentTimeUtc = null)
    {
        var binding = verifiedLicense.Data.DeviceBinding;
        var activation = verifiedLicense.Data.Activation;

        if (binding is null && activation is null)
            return;

        if (binding is null || activation is null)
            throw new LicenseValidationException("Licence activation data is incomplete.");

        var device = currentDevice ?? DeviceIdentity.GetCurrent();

        if (!string.Equals(binding.Scheme, device.Scheme, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(binding.DeviceId),
                Convert.FromHexString(device.DeviceId)))
        {
            throw new LicenseValidationException(
                $"Licence is activated for a different device (expected ...{binding.DeviceId[^8..]}, " +
                $"this device is ...{device.DeviceId[^8..]}). Deactivate it before transferring.");
        }

        var currentTime = currentTimeUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        if (activation.LeaseExpiresAt is not null && currentTime >= activation.LeaseExpiresAt)
        {
            throw new LicenseValidationException(
                $"Activation lease expired at {activation.LeaseExpiresAt.Value:O}; connect to refresh it.");
        }
    }

    private static string GetRequiredString(JsonObject root, string name)
    {
        if (root[name] is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new LicenseValidationException(
                $"Licence does not contain a valid {name}.");
        }

        return text;
    }

    private static void EnsureEnvelopeFields(JsonObject root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in root)
        {
            if (!names.Add(property.Key))
            {
                throw new LicenseValidationException(
                    $"Licence envelope contains an ambiguous field: '{property.Key}'.");
            }

            if (!EnvelopeFields.Contains(property.Key))
            {
                throw new LicenseValidationException(
                    $"Licence envelope contains an unsupported field: '{property.Key}'.");
            }
        }
    }
}
