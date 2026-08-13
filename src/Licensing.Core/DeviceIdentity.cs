using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SoftwareLicensing;

public sealed record LocalDeviceIdentity(
    string Scheme,
    string DeviceId,
    string DeviceName,
    string Source);

/// <summary>
/// Produces a privacy-preserving, product-scoped identifier for this OS installation.
/// This is a useful PoC binding, not an unspoofable hardware root of trust.
/// </summary>
public static class DeviceIdentity
{
    public const string Scheme = "os-machine-id-sha256-v1";
    private const string Namespace = "SoftwareLicensing.Poc.DeviceIdentity.v1";

    public static LocalDeviceIdentity GetCurrent()
    {
        var (source, stableValue) = ReadStableOsIdentifier();
        var normalized = stableValue.Trim().ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Namespace}\n{source}\n{normalized}"));

        return new LocalDeviceIdentity(
            Scheme,
            Convert.ToHexString(bytes),
            Environment.MachineName,
            source);
    }

    public static bool IsValidDeviceId(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static (string Source, string Value) ReadStableOsIdentifier()
    {
        if (OperatingSystem.IsWindows())
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography",
                writable: false);
            var machineGuid = key?.GetValue("MachineGuid") as string;

            if (!string.IsNullOrWhiteSpace(machineGuid))
                return ("windows-machine-guid", machineGuid);
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (!File.Exists(path))
                    continue;

                var machineId = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(machineId))
                    return ("linux-machine-id", machineId);
            }
        }

        // The fallback is deliberately explicit in Source so callers can reject it in production.
        return ("machine-name-fallback", Environment.MachineName);
    }
}
