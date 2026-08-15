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
        var publicKeyPem = File.ReadAllText(publicKeyPath);

        // Confirms the private key really is the one this key ID names, so an operator cannot issue
        // a licence stamped 'primary-2026' that was signed with some other key and that every
        // validator will therefore reject. The check is against the key pair on disk rather than the
        // compiled TrustedPublicKeys map, so a key that exists only by having been dropped into the
        // server's key directory - the whole point of the key ring - can be signed with here too.
        if (!EcdsaKeyPairs.TryValidatePair(privateKeyPem, publicKeyPem, out var pairError))
        {
            throw new InvalidOperationException(
                $"The private key does not match the public key for key ID '{keyId}' " +
                $"({publicKeyPath}). {pairError}");
        }

        // TrustedPublicKeys is consulted here only as a negative check, never as the source of
        // truth for which key IDs are *allowed* to sign - that would reintroduce the #24 gap,
        // where a key created the key-ring way (dropping <keyId>.private.pem / <keyId>.public.pem
        // into the key directory) needed a TrustedPublicKeys.cs edit and a CLI rebuild before it
        // could be signed with. A key ID absent from this map is the normal, supported key-ring
        // case and proceeds exactly as before, with no warning, no prompt, and no lookup cost
        // beyond the dictionary miss.
        //
        // What it does catch: this key ID already being one that shipped products trust, under a
        // *different* public key than the one about to sign. That is unambiguously an operator
        // mistake - most likely the key pair was regenerated locally (e.g. reusing an existing ID
        // with 'keygen --id primary-2026 --force') - and every released validator will reject
        // anything signed with it. This also covers an explicit --public-key that points at some
        // other key's counterpart under a trusted --key-id: the comparison is against the resolved
        // public key's bytes, not against how that path was resolved, so the same check applies
        // regardless of the convention path or --public-key.
        if (TrustedPublicKeys.ByKeyId.TryGetValue(keyId, out var trustedPublicKeyPem) &&
            !EcdsaKeyPairs.PublicKeysMatch(publicKeyPem, trustedPublicKeyPem))
        {
            throw new InvalidOperationException(
                $"Key ID '{keyId}' is already trusted by shipped products under a different public " +
                $"key. The public key at '{publicKeyPath}' does not match the compiled " +
                $"TrustedPublicKeys entry for '{keyId}'; licences signed with it will be rejected by " +
                "every released validator. If this key pair was regenerated locally, choose a " +
                "different --key-id for it. If this is unexpected, restore the original key pair for " +
                $"'{keyId}'.");
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
    /// precisely the mistake this check exists to catch. An explicit --public-key can still be
    /// pointed at that same tautology for a key ID <c>TrustedPublicKeys</c> has never heard of;
    /// it exists for key material stored outside the naming convention, and for that case it
    /// still moves responsibility for the pairing onto the operator. It stops being tautological
    /// for a trusted key ID, though: the caller in <see cref="Sign"/> checks the resolved public
    /// key's bytes against the compiled trust map regardless of how this path was found, so
    /// '--key-id primary-2026 --public-key secondary-2026.public.pem' is caught there.
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
