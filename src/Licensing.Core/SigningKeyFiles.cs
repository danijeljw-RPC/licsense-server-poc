using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SoftwareLicensing;

/// <summary>
/// The signing-key file naming convention, in one place. The server discovers keys by scanning a
/// directory for these names and the offline CLI creates and resolves the same names, so both sides
/// must agree on exactly what a key ID may look like and which file belongs to it.
/// </summary>
public static partial class SigningKeyFiles
{
    public const string PrivateSuffix = ".private.pem";
    public const string PublicSuffix = ".public.pem";

    /// <summary>
    /// Lowercase letters, digits, and single hyphens between segments, 3-64 characters. Restrictive
    /// enough that a key ID can be interpolated straight into a filename with no further escaping:
    /// it admits no path separator, no "..", and no leading or trailing hyphen, and it rules out
    /// case-collision ambiguity on case-insensitive filesystems.
    /// </summary>
    public static bool IsValidKeyId([NotNullWhen(true)] string? keyId) =>
        keyId is { Length: >= 3 and <= 64 } && KeyIdPattern().IsMatch(keyId);

    /// <summary>
    /// Recovers the key ID from a "&lt;keyId&gt;.private.pem" path. Used by the CLI to make
    /// <c>--key-id</c> optional when the private key already follows the convention.
    /// </summary>
    public static bool TryGetKeyIdFromPrivateKeyPath(string path, [NotNullWhen(true)] out string? keyId)
    {
        keyId = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var name = Path.GetFileName(path);
        if (!name.EndsWith(PrivateSuffix, StringComparison.Ordinal)) return false;

        var candidate = name[..^PrivateSuffix.Length];
        if (!IsValidKeyId(candidate)) return false;

        keyId = candidate;
        return true;
    }

    public static string PrivateKeyPath(string directory, string keyId) =>
        Path.Combine(directory, keyId + PrivateSuffix);

    public static string PublicKeyPath(string directory, string keyId) =>
        Path.Combine(directory, keyId + PublicSuffix);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyIdPattern();
}
