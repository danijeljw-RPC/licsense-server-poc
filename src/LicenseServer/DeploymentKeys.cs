using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace LicenseServer;

internal sealed class DeploymentKeyHasher(byte[] pepper)
{
    public const string CurrentVersion = "hmac-sha256-v1";

    public byte[] Hash(string publicId, string secret)
    {
        using var hmac = new HMACSHA256(pepper);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"{publicId}.{secret}"));
    }

    public bool Verify(string publicId, string secret, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(Hash(publicId, secret), expected);
}

internal static class DeploymentKeyFormat
{
    public const string Prefix = "dpk_live_";

    public static (string PublicId, string Secret, string FullValue) Generate()
    {
        var publicId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (publicId, secret, $"{Prefix}{publicId}_{secret}");
    }

    public static bool TryParse(string? value, out string publicId, out string secret)
    {
        publicId = secret = "";
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var separator = value.IndexOf('_', Prefix.Length);
        if (separator < 0) return false;
        publicId = value[Prefix.Length..separator];
        secret = value[(separator + 1)..];
        return publicId.Length == 16 && secret.Length == 43;
    }
}
