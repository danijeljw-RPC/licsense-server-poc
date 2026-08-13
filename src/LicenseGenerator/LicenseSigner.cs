using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoftwareLicensing;

namespace LicenseGenerator;

internal static class LicenseSigner
{
    public static int Sign(string[] args)
    {
        CommandLine.Validate(
            args,
            ["--input", "--output", "--private-key", "--key-id"],
            []);

        var inputPath = CommandLine.GetRequiredOption(args, "--input");
        var outputPath = CommandLine.GetRequiredOption(args, "--output");
        var privateKeyPath = CommandLine.GetRequiredOption(args, "--private-key");
        var keyId = CommandLine.GetRequiredOption(args, "--key-id");

        if (!TrustedPublicKeys.ByKeyId.TryGetValue(keyId, out var trustedPublicKeyPem))
        {
            throw new InvalidOperationException(
                $"Unknown signing key ID '{keyId}'. Add its public key to TrustedPublicKeys.cs first.");
        }

        var licenseNode = JsonNode.Parse(File.ReadAllText(inputPath))
            ?? throw new InvalidOperationException("Input JSON is empty.");

        if (licenseNode is not JsonObject licenseObject)
            throw new InvalidOperationException("The licence input must contain a JSON object.");

        // The signer and validator use the same strict schema implementation.
        LicenseSchema.Parse(licenseObject);

        var envelope = new JsonObject
        {
            ["format"] = LicenseConstants.Format,
            ["algorithm"] = LicenseConstants.Algorithm,
            ["keyId"] = keyId,
            ["license"] = licenseObject.DeepClone()
        };

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));

        var signingPayload = CanonicalJson.Serialize(envelope);
        var signature = ecdsa.SignData(
            signingPayload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        VerifyPrivateKey(signingPayload, signature, trustedPublicKeyPem, keyId);

        envelope["signature"] = Convert.ToBase64String(signature);
        FileSystem.EnsureParentDirectory(outputPath);

        File.WriteAllText(
            outputPath,
            envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine("Licence generated successfully.");
        Console.WriteLine($"Output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static void VerifyPrivateKey(
        byte[] signingPayload,
        byte[] signature,
        string trustedPublicKeyPem,
        string keyId)
    {
        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(trustedPublicKeyPem);

        if (!verifier.VerifyData(
                signingPayload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new InvalidOperationException(
                $"The private key does not match trusted key ID '{keyId}'.");
        }
    }
}
