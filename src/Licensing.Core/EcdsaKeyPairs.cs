using System.Security.Cryptography;

namespace SoftwareLicensing;

/// <summary>
/// Validates ECDSA key material used throughout the signing key ring: that a private/public PEM
/// pair belong together, that a standalone public key is well-formed, and that two public keys
/// are the same key - all by comparing decoded SubjectPublicKeyInfo bytes rather than PEM text
/// (line-wrap, CRLF/LF, and trailing whitespace differ between .NET's own PEM export and PEMs
/// produced by OpenSSL/Windows tooling even when both decode to the identical key). Every method
/// also rejects any key not on the NIST P-256 curve, since every key this codebase issues or
/// verifies against is P-256 and <c>LicenseConstants.Algorithm</c> is a hardcoded constant, not
/// derived from the key that actually signed.
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

            if (TryDescribeNonP256Curve(privateKey, "private key", out error)) return false;
            if (TryDescribeNonP256Curve(publicKey, "public key", out error)) return false;

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

            if (TryDescribeNonP256Curve(publicKey, "public key", out error)) return false;

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            error = $"Public key material could not be parsed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Compares two public keys by decoded SubjectPublicKeyInfo bytes, not PEM text - see the
    /// remarks on <see cref="TryValidatePair"/> for why byte comparison is required. Both
    /// inputs are expected to already be known-valid PEM (e.g. a compiled trusted key, or a
    /// key already checked with <see cref="TryValidatePublicKey"/>), so this throws rather
    /// than returning a Try-pattern result - including for the curve check: a caller that
    /// reaches this method is assumed to have already validated its inputs, so a non-P-256 key
    /// here means a caller skipped that step, which is a bug worth a hard failure rather than a
    /// bool a caller could silently ignore the way an `if (!PublicKeysMatch(...))` easily could.
    /// </summary>
    public static bool PublicKeysMatch(string publicKeyPemA, string publicKeyPemB)
    {
        using var keyA = ECDsa.Create();
        keyA.ImportFromPem(publicKeyPemA);
        using var keyB = ECDsa.Create();
        keyB.ImportFromPem(publicKeyPemB);

        if (TryDescribeNonP256Curve(keyA, "first public key", out var errorA))
            throw new CryptographicException(errorA);
        if (TryDescribeNonP256Curve(keyB, "second public key", out var errorB))
            throw new CryptographicException(errorB);

        return keyA.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(keyB.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Every key this codebase issues or accepts is ECDSA P-256 - see
    /// <c>LicenseConstants.Algorithm</c> and <c>KeyPairGenerator</c>, which only ever generates
    /// P-256 pairs. Without this check, a self-consistent key pair on some other curve (e.g.
    /// P-384) passes byte comparison cleanly while <c>LicenseEnvelope.Sign</c> still stamps the
    /// envelope "ECDSA-P256-SHA256" regardless of the curve actually used, producing a
    /// mislabeled artifact. Returns true (and sets <paramref name="error"/>) for any curve other
    /// than P-256, including ones with the same key size (e.g. brainpoolP256r1).
    /// </summary>
    private static bool TryDescribeNonP256Curve(ECDsa key, string label, out string? error)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        if (string.Equals(curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
        {
            error = null;
            return false;
        }

        var curveName = curve.Oid.FriendlyName ?? curve.Oid.Value ?? "unrecognized curve";
        error = $"The {label} is on curve '{curveName}', not P-256. Only ECDSA P-256 keys are supported.";
        return true;
    }
}
