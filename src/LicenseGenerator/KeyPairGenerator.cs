using System.Security.Cryptography;

namespace LicenseGenerator;

internal static class KeyPairGenerator
{
    public static int Generate(string[] args)
    {
        CommandLine.Validate(
            args,
            ["--private-key", "--public-key"],
            ["--force"]);

        var privateKeyPath = Path.GetFullPath(
            CommandLine.GetRequiredOption(args, "--private-key"));
        var publicKeyPath = Path.GetFullPath(
            CommandLine.GetRequiredOption(args, "--public-key"));
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
        File.WriteAllText(privateKeyPath, ecdsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem());

        Console.WriteLine("ECDSA P-256 key pair generated.");
        Console.WriteLine($"Private key: {privateKeyPath}");
        Console.WriteLine($"Public key:  {publicKeyPath}");
        Console.WriteLine();
        Console.WriteLine("WARNING: Keep the private key secret.");
        return 0;
    }
}
