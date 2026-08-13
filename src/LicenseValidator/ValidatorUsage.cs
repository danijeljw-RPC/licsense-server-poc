namespace LicenseValidator;

internal static class ValidatorUsage
{
    public static void Print()
    {
        Console.WriteLine(
            """
            LicenseValidator

            Display this installation's privacy-preserving device identifier:

              LicenseValidator --device-id

            Validate and display a licence:

              LicenseValidator --license <file.license>

            Validate a product's runtime entitlement:

              LicenseValidator --license <file.license> --product <product>

            Check whether a product release is covered by updatesUntil:

              LicenseValidator --license <file.license> --product <product>
                --release-date <yyyy-MM-dd>

            Read a licence-level field (case-insensitive):

              LicenseValidator --license <file.license> --field <field-name>

            Read custom metadata using a dotted field path:

              LicenseValidator --license <file.license>
                --field metadata.purchaseOrder

            Read a product field after enforcing its expiry:

              LicenseValidator --license <file.license> --product <product>
                --field <field-name>

            --get-edition is an alias for --field edition and requires --product.
            Public keys are selected from TrustedPublicKeys.cs using the licence keyId.

            Exit codes:

              0  Licence, product, or release is valid
              1  Invalid, expired, not covered, or requested data was not found
              2  Invalid command-line arguments or runtime error
            """);
    }
}
