namespace LicenseGenerator;

internal static class CommandLine
{
    public static void Validate(
        string[] args,
        string[] valueOptions,
        string[] flags)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (flags.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                continue;

            if (!valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown option: {args[i]}");

            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option requires a value: {args[i - 1]}");
        }
    }

    public static bool HasFlag(string[] args, string name)
    {
        return args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetRequiredOption(string[] args, string name)
    {
        return GetOptionalOption(args, name)
            ?? throw new ArgumentException($"Required option missing: {name}");
    }

    public static string? GetOptionalOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
