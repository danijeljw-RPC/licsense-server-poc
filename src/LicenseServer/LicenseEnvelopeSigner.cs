using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SoftwareLicensing;

namespace LicenseServer;

internal sealed class LicenseEnvelopeSigner
{
    private readonly string privateKeyPem;

    public LicenseEnvelopeSigner(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration["Licensing:PrivateKeyPath"];
        var privateKeyPath = string.IsNullOrWhiteSpace(configuredPath)
            ? FindDevelopmentKey(environment.ContentRootPath)
            : Path.GetFullPath(configuredPath);

        if (!File.Exists(privateKeyPath))
        {
            throw new InvalidOperationException(
                $"Signing key was not found at '{privateKeyPath}'. Configure Licensing:PrivateKeyPath.");
        }

        privateKeyPem = File.ReadAllText(privateKeyPath);
    }

    private static string FindDevelopmentKey(string contentRoot)
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "keys", "license-primary-2026-private.pem"),
            Path.Combine(contentRoot, "..", "..", "keys", "license-primary-2026-private.pem")
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists)
            ?? Path.GetFullPath(candidates[0]);
    }

    public JsonObject Sign(JsonObject license)
    {
        LicenseSchema.Parse(license);

        var envelope = new JsonObject
        {
            ["format"] = LicenseConstants.Format,
            ["algorithm"] = LicenseConstants.Algorithm,
            ["keyId"] = "primary-2026",
            ["license"] = license.DeepClone()
        };

        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(
            CanonicalJson.Serialize(envelope),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        envelope["signature"] = Convert.ToBase64String(signature);

        return envelope;
    }
}
