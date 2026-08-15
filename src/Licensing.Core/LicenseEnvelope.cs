using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SoftwareLicensing;

/// <summary>
/// The single implementation of "wrap a licence payload in a signed envelope". Both the server's
/// online signing path and the offline <c>LicenseGenerator</c> CLI call this, so canonicalization
/// and signature rules cannot drift between the two - a drift that would produce artifacts one
/// side signs and the other refuses to verify.
/// </summary>
public static class LicenseEnvelope
{
    /// <summary>
    /// Validates <paramref name="license"/> against the strict licence schema, wraps it in a
    /// <c>format</c>/<c>algorithm</c>/<c>keyId</c>/<c>license</c> envelope, and appends the
    /// detached <c>signature</c> over the canonical serialization of that envelope.
    /// </summary>
    /// <remarks>
    /// The signature covers <c>keyId</c> (and every other envelope field), so the key identity of
    /// a signed licence cannot be swapped post-signature without invalidating it. Signature bytes
    /// are order-independent: <see cref="CanonicalJson"/> sorts property names ordinally before
    /// hashing, so the insertion order used here affects only the readability of a written file.
    /// </remarks>
    public static JsonObject Sign(JsonObject license, string keyId, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(privateKey);

        // Refuse to sign anything the validator would later reject: a signature over a malformed
        // payload is worse than no artifact at all, because it looks authentic.
        LicenseSchema.Parse(license);

        var envelope = new JsonObject
        {
            ["format"] = LicenseConstants.Format,
            ["algorithm"] = LicenseConstants.Algorithm,
            ["keyId"] = keyId,
            ["license"] = license.DeepClone()
        };

        var signature = privateKey.SignData(
            CanonicalJson.Serialize(envelope),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        envelope["signature"] = Convert.ToBase64String(signature);
        return envelope;
    }
}
