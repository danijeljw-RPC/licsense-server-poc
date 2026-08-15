using System.Security.Cryptography;
using LicenseServer;
using SoftwareLicensing;

namespace LicenseServer.Tests;

public sealed class EcdsaKeyPairsTests
{
    [Fact]
    public void MatchingPairIsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = key.ExportPkcs8PrivateKeyPem();
        var publicPem = key.ExportSubjectPublicKeyInfoPem();

        Assert.True(EcdsaKeyPairs.TryValidatePair(privatePem, publicPem, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void MismatchedPairIsRejected()
    {
        using var key1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(EcdsaKeyPairs.TryValidatePair(
            key1.ExportPkcs8PrivateKeyPem(), key2.ExportSubjectPublicKeyInfoPem(), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void MalformedPemIsRejectedSafely()
    {
        Assert.False(EcdsaKeyPairs.TryValidatePair("not a pem", "also not a pem", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidPublicKeyAlonePasses()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(EcdsaKeyPairs.TryValidatePublicKey(key.ExportSubjectPublicKeyInfoPem(), out _));
    }
}

public sealed class KeyDirectoryScannerTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"key-scanner-test-{Guid.NewGuid():N}");

    public KeyDirectoryScannerTests() => Directory.CreateDirectory(directory);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    private static (string PrivatePem, string PublicPem) NewPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void ValidPairIsDiscoveredAsSignable()
    {
        var (privatePem, publicPem) = NewPair();
        File.WriteAllText(Path.Combine(directory, "example-key.private.pem"), privatePem);
        File.WriteAllText(Path.Combine(directory, "example-key.public.pem"), publicPem);

        var results = KeyDirectoryScanner.Scan(directory);

        var found = Assert.Single(results);
        Assert.Equal("example-key", found.KeyId);
        Assert.True(found.Valid);
        Assert.NotNull(found.PrivatePem);
        Assert.NotNull(found.PublicPem);
    }

    [Fact]
    public void PublicOnlyKeyIsDiscoveredAsVerificationOnly()
    {
        var (_, publicPem) = NewPair();
        File.WriteAllText(Path.Combine(directory, "retired-key.public.pem"), publicPem);

        var results = KeyDirectoryScanner.Scan(directory);

        var found = Assert.Single(results);
        Assert.Equal("retired-key", found.KeyId);
        Assert.True(found.Valid);
        Assert.Null(found.PrivatePem);
        Assert.NotNull(found.PublicPem);
    }

    [Fact]
    public void PrivateKeyWithNoMatchingPublicKeyIsExcluded()
    {
        var (privatePem, _) = NewPair();
        File.WriteAllText(Path.Combine(directory, "orphan-key.private.pem"), privatePem);

        var results = KeyDirectoryScanner.Scan(directory);

        var found = Assert.Single(results);
        Assert.False(found.Valid);
        Assert.Null(found.PrivatePem);
        Assert.Null(found.PublicPem);
    }

    [Fact]
    public void MismatchedPairIsExcludedAsInvalid()
    {
        var (privatePem, _) = NewPair();
        var (_, otherPublicPem) = NewPair();
        File.WriteAllText(Path.Combine(directory, "bad-key.private.pem"), privatePem);
        File.WriteAllText(Path.Combine(directory, "bad-key.public.pem"), otherPublicPem);

        var results = KeyDirectoryScanner.Scan(directory);

        var found = Assert.Single(results);
        Assert.False(found.Valid);
        Assert.NotNull(found.Error);
    }

    [Fact]
    public void InvalidKeyIdCharactersAreIgnored()
    {
        var (privatePem, publicPem) = NewPair();
        // Path-traversal-flavored / uppercase keyId shapes must never reach the ring.
        File.WriteAllText(Path.Combine(directory, "Bad_Id.private.pem"), privatePem);
        File.WriteAllText(Path.Combine(directory, "Bad_Id.public.pem"), publicPem);

        var results = KeyDirectoryScanner.Scan(directory);

        Assert.Empty(results);
    }

    [Fact]
    public void UnrelatedFilesAreIgnoredNotErrored()
    {
        File.WriteAllText(Path.Combine(directory, "README.md"), "not a key");
        File.WriteAllText(Path.Combine(directory, "checksums.txt"), "not a key either");

        var results = KeyDirectoryScanner.Scan(directory);

        Assert.Empty(results);
    }

    [Fact]
    public void MissingDirectoryReturnsEmptyRatherThanThrowing()
    {
        var results = KeyDirectoryScanner.Scan(Path.Combine(directory, "does-not-exist"));
        Assert.Empty(results);
    }

    [Fact]
    public void MultipleValidPairsAreAllDiscovered()
    {
        var (primaryPrivate, primaryPublic) = NewPair();
        var (secondaryPrivate, secondaryPublic) = NewPair();
        File.WriteAllText(Path.Combine(directory, "primary-2026.private.pem"), primaryPrivate);
        File.WriteAllText(Path.Combine(directory, "primary-2026.public.pem"), primaryPublic);
        File.WriteAllText(Path.Combine(directory, "secondary-2026.private.pem"), secondaryPrivate);
        File.WriteAllText(Path.Combine(directory, "secondary-2026.public.pem"), secondaryPublic);

        var results = KeyDirectoryScanner.Scan(directory);

        Assert.Equal(2, results.Count(r => r.Valid));
    }
}
