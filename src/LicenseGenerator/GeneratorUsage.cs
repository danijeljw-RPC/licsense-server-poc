namespace LicenseGenerator;

internal static class GeneratorUsage
{
    public static void Print()
    {
        Console.WriteLine(
            """
            LicenseGenerator

            Generate a new signing key pair (refuses to overwrite by default).

            Convention form, producing <dir>/<key-id>.private.pem and
            <dir>/<key-id>.public.pem - the exact names the server's key
            directory scanner discovers:

              LicenseGenerator keygen
                --id <key-id>
                --output <dir>
                [--force]

            Explicit form:

              LicenseGenerator keygen
                --private-key <path>
                --public-key <path>
                [--force]

            Private keys are written mode 600 where POSIX permissions exist.

            Sign validated licence data:

              LicenseGenerator sign
                --input <license-data.json>
                --output <customer.license>
                --private-key <private-key.pem>
                [--key-id <key-id>]
                [--public-key <public-key.pem>]

            --key-id defaults to the private key's filename when that follows
            <key-id>.private.pem. --public-key defaults to <key-id>.public.pem
            beside the private key, and is used to confirm the private key
            really is the one the key ID names.
            """);
    }
}
