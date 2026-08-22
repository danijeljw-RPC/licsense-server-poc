using System.Text.Json.Serialization;
using SoftwareLicensing;

namespace LicenseValidator;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LocalDeviceIdentity))]
internal sealed partial class DeviceIdentityJsonContext : JsonSerializerContext;
