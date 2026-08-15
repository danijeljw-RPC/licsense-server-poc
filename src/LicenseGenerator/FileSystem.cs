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
    /// Writes private key material owner-read/write only, so the key is never world-readable even
    /// momentarily. Two cases have to be handled separately: for a new file the mode is requested at
    /// creation time, but <see cref="FileStreamOptions.UnixCreateMode"/> is ignored for an inode
    /// that already exists, so a --force overwrite is narrowed to mode 600 *before* the stream
    /// truncates it and writes the new key. Best-effort by design: Windows has no POSIX mode, so
    /// restricting the file there is the operator's job via NTFS ACLs (the caller says so on stdout).
    /// </summary>
    public static void WritePrivateKey(string path, string pem)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, pem);
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        if (File.Exists(path))
            File.SetUnixFileMode(path, ownerOnly);

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
