using System.Security.Cryptography;
using SoftwareLicensing;

namespace LicenseGenerator;

internal static class KeyPairGenerator
{
    public static int Generate(string[] args)
    {
        CommandLine.Validate(
            args,
            ["--private-key", "--public-key", "--id", "--output"],
            ["--force"]);

        var (privateKeyPath, publicKeyPath) = ResolveOutputPaths(args);
        var force = CommandLine.HasFlag(args, "--force");

        if (string.Equals(privateKeyPath, publicKeyPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Private and public key paths must be different.");

        if (!force && (File.Exists(privateKeyPath) || File.Exists(publicKeyPath)))
        {
            throw new InvalidOperationException(
                "A key output file already exists. Choose new paths, or use --force only when " +
                "you deliberately intend to replace an unused key pair.");
        }

        FileSystem.EnsureParentDirectory(privateKeyPath);
        FileSystem.EnsureParentDirectory(publicKeyPath);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        FileSystem.WritePrivateKey(privateKeyPath, ecdsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem());

        Console.WriteLine("ECDSA P-256 key pair generated.");
        Console.WriteLine($"Private key: {privateKeyPath}");
        Console.WriteLine($"Public key:  {publicKeyPath}");
        Console.WriteLine();

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine(
                "WARNING: Keep the private key secret. POSIX file permissions are unavailable on " +
                "Windows; restrict the private key with NTFS ACLs yourself.");
        }
        else
        {
            Console.WriteLine("WARNING: Keep the private key secret. The private key file is mode 600.");
        }

        return 0;
    }

    /// <summary>
    /// Two mutually exclusive forms: the convention-based <c>--id &lt;keyId&gt; --output &lt;dir&gt;</c>,
    /// which produces the exact filenames the server's key directory scanner looks for, and the
    /// original explicit <c>--private-key</c>/<c>--public-key</c> pair of paths.
    /// </summary>
    private static (string PrivateKeyPath, string PublicKeyPath) ResolveOutputPaths(string[] args)
    {
        var keyId = CommandLine.GetOptionalOption(args, "--id");
        var outputDirectory = CommandLine.GetOptionalOption(args, "--output");
        var explicitPrivate = CommandLine.GetOptionalOption(args, "--private-key");
        var explicitPublic = CommandLine.GetOptionalOption(args, "--public-key");

        var usesConventionForm = keyId is not null || outputDirectory is not null;
        var usesExplicitForm = explicitPrivate is not null || explicitPublic is not null;

        if (usesConventionForm && usesExplicitForm)
        {
            throw new ArgumentException(
                "Use either --id with --output, or --private-key with --public-key, not both.");
        }

        if (usesConventionForm)
        {
            if (keyId is null || outputDirectory is null)
                throw new ArgumentException("--id and --output must be supplied together.");

            if (!SigningKeyFiles.IsValidKeyId(keyId))
            {
                throw new ArgumentException(
                    $"Invalid --id '{keyId}'. Key IDs are 3-64 characters of lowercase letters and " +
                    "digits, with single hyphens between segments, so that they are safe to use " +
                    "directly as filenames.");
            }

            var directory = Path.GetFullPath(outputDirectory);
            return (
                SigningKeyFiles.PrivateKeyPath(directory, keyId),
                SigningKeyFiles.PublicKeyPath(directory, keyId));
        }

        return (
            Path.GetFullPath(CommandLine.GetRequiredOption(args, "--private-key")),
            Path.GetFullPath(CommandLine.GetRequiredOption(args, "--public-key")));
    }
}
