using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LicenseServer.Tests;

public sealed class ActivationCodeTests
{
    private static readonly Regex Pattern = new(
        "^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{8}(?:-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}){3}-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{12}$",
        RegexOptions.CultureInvariant);

    [Fact]
    [Trait("ExpectedGreenStage", "05")]
    public void GeneratorProducesDistinctCodesWithTheExactShapeAndAlphabet()
    {
        var generator = new ActivationCodeGenerator();
        var codes = Enumerable.Range(0, 10_000).Select(_ => generator.Generate()).ToArray();

        Assert.All(codes, code => Assert.Matches(Pattern, code));
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("ExpectedGreenStage", "05")]
    public void HasherSupportsCurrentAndLegacyVersionsAndRejectsUnknownVersions()
    {
        var hasher = new ActivationCodeHasher(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        const string code = "ABCDEFGH-JKMN-PQRS-TUVW-XYZ23456789A";
        var current = hasher.Hash(code);
        var legacy = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        Assert.Equal(ActivationCodeHasher.CurrentVersion, current.Version);
        Assert.True(hasher.Verify(code, current.Version, current.Value));
        Assert.True(hasher.Verify(code, ActivationCodeHasher.LegacySha256Version, legacy));
        Assert.False(hasher.Verify(code + "B", current.Version, current.Value));
        Assert.False(hasher.Verify(code, "hmac-sha256-v99", current.Value));
        Assert.False(hasher.Verify(null, current.Version, current.Value));
    }
}
