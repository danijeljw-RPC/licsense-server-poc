namespace LicenseValidator;

internal sealed record ValidatorOptions(
    string LicensePath,
    string? ExpectedProduct,
    string? RequestedField,
    string? ReleaseDateText)
{
    private static readonly string[] ProductFields =
    [
        "product", "edition", "licenseType", "seats", "expiresAt", "updatesUntil"
    ];

    public bool RequestsProductFieldWithoutProduct =>
        ProductFields.Contains(RequestedField, StringComparer.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(ExpectedProduct);

    public static bool HasHelpFlag(string[] args)
    {
        return HasFlag(args, "--help");
    }

    public static ValidatorOptions Parse(string[] args)
    {
        ValidateArguments(args);

        var expectedProduct = GetOptionalOption(args, "--product");
        var requestedField = GetOptionalOption(args, "--field");
        var releaseDateText = GetOptionalOption(args, "--release-date");

        if (HasFlag(args, "--get-edition"))
        {
            if (requestedField is not null)
                throw new ArgumentException("Use either --field or --get-edition, not both.");

            requestedField = "edition";
        }

        if (!string.IsNullOrWhiteSpace(releaseDateText)
            && string.IsNullOrWhiteSpace(expectedProduct))
        {
            throw new ArgumentException("--release-date requires --product.");
        }

        return new ValidatorOptions(
            GetRequiredOption(args, "--license"),
            expectedProduct,
            requestedField,
            releaseDateText);
    }

    private static void ValidateArguments(string[] args)
    {
        var valueOptions = new[] { "--license", "--product", "--field", "--release-date" };
        var flags = new[] { "--get-edition", "--help" };

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--public-key", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "--public-key is no longer accepted. " +
                    "The validator uses embedded trusted public keys.");
            }

            if (flags.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                continue;

            if (!valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown option: {args[i]}");

            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option requires a value: {args[i - 1]}");
        }
    }

    private static bool HasFlag(string[] args, string name)
    {
        return args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetRequiredOption(string[] args, string name)
    {
        return GetOptionalOption(args, name)
            ?? throw new ArgumentException($"Required option missing: {name}");
    }

    private static string? GetOptionalOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
