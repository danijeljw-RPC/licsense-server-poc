using System.Security.Cryptography;
using System.Text.Json;
using SoftwareLicensing;
using System.Text.Json.Nodes;

namespace LicenseGenerator;

internal static class LicenseSigner
{
    public static int Sign(string[] args)
    {
        CommandLine.Validate(
            args,
            ["--input", "--output", "--private-key", "--public-key", "--key-id"],
            []);

        var inputPath = CommandLine.GetRequiredOption(args, "--input");
        var outputPath = CommandLine.GetRequiredOption(args, "--output");
        var privateKeyPath = CommandLine.GetRequiredOption(args, "--private-key");
        var keyId = ResolveKeyId(args, privateKeyPath);
        var publicKeyPath = ResolvePublicKeyPath(args, privateKeyPath, keyId);

        var privateKeyPem = File.ReadAllText(privateKeyPath);

        // Confirms the private key really is the one this key ID names, so an operator cannot issue
        // a licence stamped 'primary-2026' that was signed with some other key and that every
        // validator will therefore reject. The check is against the key pair on disk rather than the
        // compiled TrustedPublicKeys map, so a key that exists only by having been dropped into the
        // server's key directory - the whole point of the key ring - can be signed with here too.
        if (!EcdsaKeyPairs.TryValidatePair(privateKeyPem, File.ReadAllText(publicKeyPath), out var pairError))
        {
            throw new InvalidOperationException(
                $"The private key does not match the public key for key ID '{keyId}' " +
                $"({publicKeyPath}). {pairError}");
        }

        var licenseNode = JsonNode.Parse(File.ReadAllText(inputPath))
            ?? throw new InvalidOperationException("Input JSON is empty.");

        if (licenseNode is not JsonObject licenseObject)
            throw new InvalidOperationException("The licence input must contain a JSON object.");

        // Shared with the server's online signing path, so canonicalization and signature rules
        // cannot drift between the two. It applies the same strict schema validation the validator
        // enforces before anything is signed.
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var envelope = LicenseEnvelope.Sign(licenseObject, keyId, ecdsa);

        FileSystem.EnsureParentDirectory(outputPath);
        File.WriteAllText(
            outputPath,
            envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine("Licence generated successfully.");
        Console.WriteLine($"Key ID: {keyId}");
        Console.WriteLine($"Output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    /// <summary>
    /// <c>--key-id</c> is optional when the private key is named by the convention
    /// (<c>&lt;keyId&gt;.private.pem</c>); an explicit value always wins.
    /// </summary>
    private static string ResolveKeyId(string[] args, string privateKeyPath)
    {
        var explicitKeyId = CommandLine.GetOptionalOption(args, "--key-id");

        if (explicitKeyId is not null)
        {
            if (!SigningKeyFiles.IsValidKeyId(explicitKeyId))
            {
                throw new ArgumentException(
                    $"Invalid --key-id '{explicitKeyId}'. Key IDs are 3-64 characters of lowercase " +
                    "letters and digits, with single hyphens between segments.");
            }

            return explicitKeyId;
        }

        if (SigningKeyFiles.TryGetKeyIdFromPrivateKeyPath(privateKeyPath, out var derived))
            return derived;

        throw new ArgumentException(
            $"--key-id was not supplied and could not be derived from '{Path.GetFileName(privateKeyPath)}'. " +
            $"Name the private key '<keyId>{SigningKeyFiles.PrivateSuffix}' or pass --key-id explicitly.");
    }

    /// <summary>
    /// The public key to check the pair against is located by key ID, never by rewriting the
    /// private key's own filename. Deriving it from the private key's name would make the pair
    /// check tautological - signing with 'secondary-2026.private.pem' under
    /// '--key-id primary-2026' would compare secondary against secondary and pass, which is
    /// precisely the mistake this check exists to catch.
    /// </summary>
    private static string ResolvePublicKeyPath(string[] args, string privateKeyPath, string keyId)
    {
        var explicitPath = CommandLine.GetOptionalOption(args, "--public-key");

        if (explicitPath is not null)
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"Public key file does not exist: {explicitPath}", explicitPath);

            return explicitPath;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(privateKeyPath)) ?? ".";
        var derivedPath = SigningKeyFiles.PublicKeyPath(directory, keyId);

        if (!File.Exists(derivedPath))
        {
            throw new FileNotFoundException(
                $"Could not find '{keyId}{SigningKeyFiles.PublicSuffix}' next to the private key " +
                $"({derivedPath}). Signing verifies that the private key matches the key ID it will " +
                "be stamped with; place the public key beside the private key or pass --public-key.",
                derivedPath);
        }

        return derivedPath;
    }
}
