namespace LicenseGenerator;

internal static class GeneratorUsage
{
    public static void Print()
    {
        Console.WriteLine(
            """
            LicenseGenerator

            Generate a new signing key pair (refuses to overwrite by default):

              LicenseGenerator keygen
                --private-key <path>
                --public-key <path>
                [--force]

            Sign validated licence data:

              LicenseGenerator sign
                --input <license-data.json>
                --output <customer.license>
                --private-key <private-key.pem>
                --key-id <key-id>
            """);
    }
}
