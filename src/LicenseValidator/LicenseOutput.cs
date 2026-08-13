using SoftwareLicensing;

namespace LicenseValidator;

internal static class LicenseOutput
{
    public static int WriteField(
        LicenseData license,
        ProductEntitlement? entitlement,
        string requestedField)
    {
        var value = entitlement is null
            ? JsonFieldResolver.Resolve(license.Json, requestedField)
            : JsonFieldResolver.Resolve(entitlement.Json, requestedField)
                ?? JsonFieldResolver.Resolve(license.Json, requestedField);

        if (value is null)
            return Invalid($"Licence field '{requestedField}' was not found.");

        // Field mode is machine-friendly: stdout contains only the requested value.
        Console.WriteLine(JsonFieldResolver.Format(value));
        return 0;
    }

    public static void WriteSummary(VerifiedLicense verifiedLicense)
    {
        var license = verifiedLicense.Data;

        Console.WriteLine("VALID LICENCE");
        Console.WriteLine();
        Console.WriteLine($"Key ID:       {verifiedLicense.KeyId}");
        Console.WriteLine($"License ID:   {license.LicenseId}");
        Console.WriteLine($"Customer:     {license.Customer}");
        Console.WriteLine($"Issued:       {license.IssuedAt:O}");

        if (license.DeviceBinding is not null && license.Activation is not null)
        {
            Console.WriteLine($"Activation:   {license.Activation.ActivationId} ({license.Activation.Mode})");
            Console.WriteLine($"Device:       {license.DeviceBinding.DeviceName ?? "unnamed"} " +
                              $"(...{license.DeviceBinding.DeviceId[^8..]})");
            if (license.Activation.LeaseExpiresAt is not null)
                Console.WriteLine($"Lease expires:{license.Activation.LeaseExpiresAt:O}");
        }

        Console.WriteLine();
        Console.WriteLine("Entitlements:");

        var now = DateTimeOffset.UtcNow;

        foreach (var item in license.Entitlements)
        {
            var runtime = item.ExpiresAt is null
                ? "no runtime expiry"
                : now >= item.ExpiresAt.Value
                    ? $"EXPIRED {item.ExpiresAt.Value:O}"
                    : $"expires {item.ExpiresAt.Value:O}";
            var updates = item.UpdatesUntil is null
                ? "updates not date-limited"
                : $"updates through {item.UpdatesUntil.Value:yyyy-MM-dd}";

            Console.WriteLine(
                $"  {item.Product} ({item.Edition}, {item.LicenseType}, " +
                $"{item.Seats} seat{(item.Seats == 1 ? "" : "s")}) " +
                $"[{runtime}; {updates}]");
        }
    }

    public static int Invalid(string reason)
    {
        Console.Error.WriteLine("INVALID LICENCE");
        Console.Error.WriteLine(reason);
        return 1;
    }
}
