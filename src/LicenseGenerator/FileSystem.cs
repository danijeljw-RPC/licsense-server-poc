namespace LicenseGenerator;

internal static class FileSystem
{
    public static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (directory is not null)
            Directory.CreateDirectory(directory);
    }
}
