using System.Security.Cryptography;

namespace SoftwareLicensing;

/// <summary>
/// Cryptographically confirms that a private/public PEM pair belong together, by comparing
/// decoded SubjectPublicKeyInfo bytes rather than PEM text (line-wrap, CRLF/LF, and trailing
/// whitespace differ between .NET's own PEM export and PEMs produced by OpenSSL/Windows tooling
/// even when both decode to the identical key).
/// </summary>
public static class EcdsaKeyPairs
{
    public static bool TryValidatePair(string privateKeyPem, string publicKeyPem, out string? error)
    {
        try
        {
            using var privateKey = ECDsa.Create();
            privateKey.ImportFromPem(privateKeyPem);
            using var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(publicKeyPem);

            var derivedPublicKeyBytes = privateKey.ExportSubjectPublicKeyInfo();
            var suppliedPublicKeyBytes = publicKey.ExportSubjectPublicKeyInfo();

            if (!derivedPublicKeyBytes.AsSpan().SequenceEqual(suppliedPublicKeyBytes))
            {
                error = "The private and public keys do not form a matching pair.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            error = $"Key material could not be parsed: {ex.Message}";
            return false;
        }
    }

    public static bool TryValidatePublicKey(string publicKeyPem, out string? error)
    {
        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(publicKeyPem);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            error = $"Public key material could not be parsed: {ex.Message}";
            return false;
        }
    }
}
