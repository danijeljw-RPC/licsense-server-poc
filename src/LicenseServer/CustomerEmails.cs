using System.Net.Mail;

namespace LicenseServer;

internal static class CustomerEmails
{
    public const int MaximumLength = 320;

    public static bool TryNormalize(string? value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Customer email is required.";
            return false;
        }

        if (candidate.Length > MaximumLength
            || candidate.Any(char.IsWhiteSpace)
            || candidate.Count(character => character == '@') != 1)
        {
            error = "Customer email must be one valid email address.";
            return false;
        }

        try
        {
            var parsed = new MailAddress(candidate);
            if (!string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.IndexOf('.', candidate.IndexOf('@') + 2) < 0)
            {
                error = "Customer email must be one valid email address.";
                return false;
            }
        }
        catch (FormatException)
        {
            error = "Customer email must be one valid email address.";
            return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }
}
