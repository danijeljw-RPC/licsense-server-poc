using System.Security.Cryptography;
using System.Text;

namespace LicenseServer;

internal interface IActivationCodeGenerator
{
    string Generate();
}

internal sealed class ActivationCodeGenerator : IActivationCodeGenerator
{
    internal const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public string Generate()
    {
        Span<char> raw = stackalloc char[32];
        for (var index = 0; index < raw.Length; index++)
            raw[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return string.Create(36, raw.ToArray(), static (formatted, value) =>
        {
            value.AsSpan(0, 8).CopyTo(formatted);
            formatted[8] = '-';
            value.AsSpan(8, 4).CopyTo(formatted[9..]);
            formatted[13] = '-';
            value.AsSpan(12, 4).CopyTo(formatted[14..]);
            formatted[18] = '-';
            value.AsSpan(16, 4).CopyTo(formatted[19..]);
            formatted[23] = '-';
            value.AsSpan(20, 12).CopyTo(formatted[24..]);
        });
    }
}

internal readonly record struct VersionedActivationCodeHash(string Version, byte[] Value);

internal interface IActivationCodeHasher
{
    VersionedActivationCodeHash Hash(string activationCode);
    bool Verify(string? supplied, string version, byte[] expected);
}

internal sealed class ActivationCodeHasher : IActivationCodeHasher
{
    internal const string LegacySha256Version = "sha256-v0";
    internal const string CurrentVersion = "hmac-sha256-v1";
    private readonly byte[] pepper;

    public ActivationCodeHasher(byte[] pepper)
    {
        if (pepper.Length < 32)
            throw new ArgumentException("The activation-code pepper must contain at least 32 bytes.", nameof(pepper));
        this.pepper = pepper.ToArray();
    }

    public VersionedActivationCodeHash Hash(string activationCode) =>
        new(CurrentVersion, HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(activationCode)));

    public bool Verify(string? supplied, string version, byte[] expected)
    {
        // The selected version is stored server-side and is not secret. Every supported and
        // unsupported version still reaches the same fixed-time, fixed-length comparison.
        var candidate = version switch
        {
            CurrentVersion => HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(supplied ?? string.Empty)),
            LegacySha256Version => SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty)),
            _ => HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(supplied ?? string.Empty))
        };
        Span<byte> normalizedExpected = stackalloc byte[32];
        if (expected.Length == normalizedExpected.Length)
            expected.CopyTo(normalizedExpected);

        var equal = CryptographicOperations.FixedTimeEquals(candidate, normalizedExpected);
        return supplied is not null
            && expected.Length == normalizedExpected.Length
            && version is CurrentVersion or LegacySha256Version
            && equal;
    }
}

internal sealed class ActivationCodeOptions
{
    public string? Pepper { get; set; }
    public int IdempotencyWindowMinutes { get; set; } = 5;
}

