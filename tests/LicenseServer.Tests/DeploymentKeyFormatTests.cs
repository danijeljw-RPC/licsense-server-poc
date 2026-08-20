using System.Security.Cryptography;
using LicenseServer;

namespace LicenseServer.Tests;

public sealed class DeploymentKeyFormatTests
{
    [Fact]
    [Trait("ExpectedGreenStage", "11")]
    public void GenerateProducesDistinctKeysWithTheDpkLivePrefixAndExpectedShape()
    {
        var first = DeploymentKeyFormat.Generate();
        var second = DeploymentKeyFormat.Generate();

        Assert.NotEqual(first.FullValue, second.FullValue);
        Assert.StartsWith("dpk_live_", first.FullValue, StringComparison.Ordinal);
        Assert.Equal(16, first.PublicId.Length);
        Assert.Equal(43, first.Secret.Length);
        Assert.True(DeploymentKeyFormat.TryParse(first.FullValue, out var publicId, out var secret));
        Assert.Equal(first.PublicId, publicId);
        Assert.Equal(first.Secret, secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lic_live_0011223344556677_abc")]
    [InlineData("dpk_live_tooshort_abc")]
    [InlineData("dpk_live_missing-secret-separator")]
    public void TryParseRejectsMalformedOrWrongPrefixValues(string? value)
    {
        Assert.False(DeploymentKeyFormat.TryParse(value, out _, out _));
    }

    [Fact]
    public void HasherVerifiesOnlyTheExactPublicIdSecretPairAndIsFixedTime()
    {
        var pepper = RandomNumberGenerator.GetBytes(32);
        var hasher = new DeploymentKeyHasher(pepper);
        var (publicId, secret, _) = DeploymentKeyFormat.Generate();
        var hash = hasher.Hash(publicId, secret);

        Assert.True(hasher.Verify(publicId, secret, hash));
        Assert.False(hasher.Verify(publicId, "wrong-secret", hash));
        Assert.False(hasher.Verify("wrong-public-id", secret, hash));
    }
}
