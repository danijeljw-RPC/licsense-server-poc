using SoftwareLicensing;

namespace LicenseValidator;

internal static class ValidatorApplication
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 1
                && string.Equals(args[0], "--device-id", StringComparison.OrdinalIgnoreCase))
            {
                var device = DeviceIdentity.GetCurrent();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                    device,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            if (args.Length == 0 || ValidatorOptions.HasHelpFlag(args))
            {
                ValidatorUsage.Print();
                return args.Length == 0 ? 2 : 0;
            }

            var options = ValidatorOptions.Parse(args);

            if (options.RequestsProductFieldWithoutProduct)
            {
                return LicenseOutput.Invalid(
                    $"Field '{options.RequestedField}' belongs to an entitlement. " +
                    "Specify --product.");
            }

            VerifiedLicense verifiedLicense;

            try
            {
                verifiedLicense = LicenseVerifier.VerifyFile(options.LicensePath);
                LicenseVerifier.ValidateActivation(verifiedLicense);
            }
            catch (LicenseValidationException ex)
            {
                return LicenseOutput.Invalid(ex.Message);
            }

            var releaseDate = ParseReleaseDate(options.ReleaseDateText);
            ProductEntitlement? entitlement = null;

            if (!string.IsNullOrWhiteSpace(options.ExpectedProduct))
            {
                try
                {
                    entitlement = LicenseVerifier.ValidateProduct(
                        verifiedLicense,
                        options.ExpectedProduct,
                        releaseDate);
                }
                catch (LicenseValidationException ex)
                {
                    return LicenseOutput.Invalid(ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(options.RequestedField))
            {
                return LicenseOutput.WriteField(
                    verifiedLicense.Data,
                    entitlement,
                    options.RequestedField);
            }

            LicenseOutput.WriteSummary(verifiedLicense);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine();
            ValidatorUsage.Print();
            return 2;
        }
    }

    private static DateOnly? ParseReleaseDate(string? releaseDateText)
    {
        if (string.IsNullOrWhiteSpace(releaseDateText))
            return null;

        try
        {
            return LicenseSchema.ParseDate(releaseDateText, "--release-date");
        }
        catch (LicenseSchemaException ex)
        {
            throw new ArgumentException(ex.Message);
        }
    }

}
