namespace LicenseGenerator;

internal static class FileSystem
{
    public static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (directory is not null)
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Writes private key material owner-read/write only. The mode is requested at creation time so
    /// the key material is never briefly world-readable, and re-applied afterwards because
    /// <see cref="FileStreamOptions.UnixCreateMode"/> has no effect when --force overwrites a file
    /// that already exists. Best-effort by design: Windows has no POSIX mode, so restricting the
    /// file there is the operator's job via NTFS ACLs (the caller says so on stdout).
    /// </summary>
    public static void WritePrivateKey(string path, string pem)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, pem);
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = ownerOnly
        };

        using (var writer = new StreamWriter(path, options))
            writer.Write(pem);

        File.SetUnixFileMode(path, ownerOnly);
    }
}
