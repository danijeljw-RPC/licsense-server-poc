namespace LicenseServer;
public static class EnvironmentExtensions
{
    public static string VersionLabel(this IWebHostEnvironment _) => $"{Environment.Version.Major}.{Environment.Version.Minor}";
}
