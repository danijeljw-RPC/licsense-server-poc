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

/// <summary>
/// The filename convention is the contract between the server's key directory scanner and the
/// offline CLI: the CLI's "keygen --id" must produce names the scanner discovers, and the CLI's
/// "sign" derives a key ID back out of a private key filename. These tests pin both directions.
/// </summary>
public sealed class SigningKeyFilesTests
{
    [Theory]
    [InlineData("primary-2026")]
    [InlineData("abc")]
    [InlineData("a1b2c3")]
    [InlineData("one-two-three-2026")]
    public void ConventionalKeyIdsAreAccepted(string keyId) =>
        Assert.True(SigningKeyFiles.IsValidKeyId(keyId));

    [Theory]
    [InlineData(null)]          // absent
    [InlineData("")]            // empty
    [InlineData("ab")]          // shorter than 3
    [InlineData("Primary-2026")] // uppercase collides on case-insensitive filesystems
    [InlineData("bad_id")]      // underscore is not part of the convention
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("../escape")]   // path traversal
    [InlineData("with/slash")]
    [InlineData("with space")]
    [InlineData("primary-2026\n")] // trailing newline: .NET's `$` matches before it, `\z` must not
    [InlineData("primary-2026\r\n")]
    public void NonConformingKeyIdsAreRejected(string? keyId) =>
        Assert.False(SigningKeyFiles.IsValidKeyId(keyId));

    [Fact]
    public void KeyIdLongerThanSixtyFourCharactersIsRejected() =>
        Assert.False(SigningKeyFiles.IsValidKeyId(new string('a', 65)));

    [Fact]
    public void KeyIdOfExactlySixtyFourCharactersIsAccepted() =>
        Assert.True(SigningKeyFiles.IsValidKeyId(new string('a', 64)));

    [Fact]
    public void KeyIdIsDerivedFromAConventionalPrivateKeyPath()
    {
        Assert.True(SigningKeyFiles.TryGetKeyIdFromPrivateKeyPath(
            Path.Combine("keys", "primary-2026.private.pem"), out var keyId));
        Assert.Equal("primary-2026", keyId);
    }

    [Theory]
    [InlineData("keys/primary-2026.public.pem")]   // public half, not private
    [InlineData("keys/primary-2026.pem")]          // missing the .private segment
    [InlineData("keys/Primary-2026.private.pem")]  // key ID fails the convention
    [InlineData("keys/.private.pem")]              // empty key ID
    [InlineData("")]
    public void UnconventionalPrivateKeyPathsYieldNoKeyId(string path)
    {
        Assert.False(SigningKeyFiles.TryGetKeyIdFromPrivateKeyPath(path, out var keyId));
        Assert.Null(keyId);
    }

    [Fact]
    public void GeneratedPathsAreExactlyWhatTheScannerDiscovers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"key-files-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(
                SigningKeyFiles.PrivateKeyPath(directory, "round-trip-key"), key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(
                SigningKeyFiles.PublicKeyPath(directory, "round-trip-key"), key.ExportSubjectPublicKeyInfoPem());

            var found = Assert.Single(KeyDirectoryScanner.Scan(directory));
            Assert.Equal("round-trip-key", found.KeyId);
            Assert.True(found.Valid);
            Assert.NotNull(found.PrivatePem);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
