namespace LicenseServer;

internal static class LicenseTerms
{
    public const string Perpetual = "perpetual";
    public const string Subscription = "subscription";
    public const string Evaluation = "evaluation";

    public static readonly DateTimeOffset PerpetualExpiry =
        new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_990);

    public static bool IsSupportedType(string? value) =>
        value is Perpetual or Subscription or Evaluation;

    public static bool TryCanonicalizeIssuanceExpiry(
        string? licenseType,
        DateTimeOffset? requested,
        DateTimeOffset now,
        out DateTimeOffset canonical,
        out string? error)
    {
        canonical = default;
        error = null;
        if (!IsSupportedType(licenseType))
        {
            error = "License type must be exactly 'perpetual', 'subscription', or 'evaluation'.";
            return false;
        }

        if (licenseType == Perpetual)
        {
            canonical = PerpetualExpiry;
            return true;
        }

        if (requested is null || requested.Value.ToUniversalTime() <= now.ToUniversalTime())
        {
            error = $"A future expiry is required for a {licenseType} license.";
            return false;
        }

        canonical = requested.Value.ToUniversalTime();
        return true;
    }
}
