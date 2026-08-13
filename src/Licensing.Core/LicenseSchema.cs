using System.Globalization;
using System.Text.Json.Nodes;

namespace SoftwareLicensing;

public sealed record ProductEntitlement(
    string Product,
    string Edition,
    string LicenseType,
    int Seats,
    DateTimeOffset? ExpiresAt,
    DateOnly? UpdatesUntil,
    JsonObject Json);

public sealed record DeviceBinding(
    string Scheme,
    string DeviceId,
    string? DeviceName,
    JsonObject Json);

public sealed record ActivationData(
    string ActivationId,
    string Mode,
    DateTimeOffset ActivatedAt,
    DateTimeOffset? RefreshAfter,
    DateTimeOffset? LeaseExpiresAt,
    JsonObject Json);

public sealed record LicenseData(
    string LicenseId,
    string Customer,
    DateTimeOffset IssuedAt,
    JsonObject? Metadata,
    DeviceBinding? DeviceBinding,
    ActivationData? Activation,
    IReadOnlyList<ProductEntitlement> Entitlements,
    JsonObject Json);

public sealed class LicenseSchemaException : Exception
{
    public LicenseSchemaException() { }
    public LicenseSchemaException(string message) : base(message) { }
    public LicenseSchemaException(string message, Exception innerException) : base(message, innerException) { }
}

public static class LicenseSchema
{
    private static readonly HashSet<string> LicenseFields =
        new(StringComparer.Ordinal)
        {
            "licenseId", "customer", "issuedAt", "metadata", "deviceBinding",
            "activation", "entitlements"
        };

    private static readonly HashSet<string> EntitlementFields =
        new(StringComparer.Ordinal)
        {
            "product", "edition", "licenseType", "seats", "expiresAt", "updatesUntil"
        };

    private static readonly HashSet<string> DeviceBindingFields =
        new(StringComparer.Ordinal)
        {
            "scheme", "deviceId", "deviceName"
        };

    private static readonly HashSet<string> ActivationFields =
        new(StringComparer.Ordinal)
        {
            "activationId", "mode", "activatedAt", "refreshAfter", "leaseExpiresAt"
        };

    public static LicenseData Parse(JsonObject license)
    {
        ArgumentNullException.ThrowIfNull(license);
        EnsureUnambiguousNames(license, "licence");
        EnsureOnlyAllowedFields(license, LicenseFields, "licence");

        var licenseId = GetRequiredString(license, "licenseId", "licence");
        var customer = GetRequiredString(license, "customer", "licence");
        var issuedAt = GetRequiredInstant(license, "issuedAt", "licence");
        var metadata = GetOptionalMetadata(license);
        var deviceBinding = GetOptionalDeviceBinding(license);
        var activation = GetOptionalActivation(license);

        if ((deviceBinding is null) != (activation is null))
        {
            throw new LicenseSchemaException(
                "Licence fields 'deviceBinding' and 'activation' must either both be present or both be absent.");
        }

        if (license["entitlements"] is not JsonArray entitlementArray
            || entitlementArray.Count == 0)
        {
            throw new LicenseSchemaException(
                "Licence field 'entitlements' must be a non-empty array.");
        }

        var entitlements = new List<ProductEntitlement>();
        var products = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < entitlementArray.Count; index++)
        {
            if (entitlementArray[index] is not JsonObject entitlement)
            {
                throw new LicenseSchemaException(
                    $"Entitlement at index {index} must be a JSON object.");
            }

            var context = $"entitlement at index {index}";
            EnsureUnambiguousNames(entitlement, context);
            EnsureOnlyAllowedFields(entitlement, EntitlementFields, context);

            var product = GetRequiredString(entitlement, "product", context);

            if (!products.Add(product))
            {
                throw new LicenseSchemaException(
                    $"Licence contains more than one entitlement for product '{product}'.");
            }

            entitlements.Add(new ProductEntitlement(
                product,
                GetRequiredString(entitlement, "edition", context),
                GetRequiredString(entitlement, "licenseType", context),
                GetRequiredPositiveInteger(entitlement, "seats", context),
                GetOptionalInstant(entitlement, "expiresAt", context),
                GetOptionalDate(entitlement, "updatesUntil", context),
                entitlement));
        }

        return new LicenseData(
            licenseId,
            customer,
            issuedAt,
            metadata,
            deviceBinding,
            activation,
            entitlements,
            license);
    }

    public static DateOnly ParseDate(string value, string fieldName)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new LicenseSchemaException(
                $"{fieldName} must use the ISO date format yyyy-MM-dd.");
        }

        return parsed;
    }

    private static string GetRequiredString(
        JsonObject node,
        string name,
        string context)
    {
        if (node[name] is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new LicenseSchemaException(
                $"Required field '{name}' in {context} must be a non-empty string.");
        }

        return text;
    }

    private static JsonObject? GetOptionalMetadata(JsonObject license)
    {
        if (!license.TryGetPropertyValue("metadata", out var value))
            return null;

        if (value is not JsonObject metadata)
        {
            throw new LicenseSchemaException(
                "Optional licence field 'metadata' must be a JSON object.");
        }

        EnsureUnambiguousNames(metadata, "licence metadata");

        foreach (var property in metadata)
        {
            if (!IsLowerCamelCase(property.Key))
            {
                throw new LicenseSchemaException(
                    $"Metadata field '{property.Key}' must use lowerCamelCase and contain " +
                    "only ASCII letters and digits.");
            }

            if (property.Value is not JsonValue)
            {
                throw new LicenseSchemaException(
                    $"Metadata field '{property.Key}' must be a string, number, or boolean. " +
                    "Nested objects, arrays, and null are not supported.");
            }
        }

        return metadata;
    }

    private static DeviceBinding? GetOptionalDeviceBinding(JsonObject license)
    {
        if (!license.TryGetPropertyValue("deviceBinding", out var value))
            return null;

        if (value is not JsonObject binding)
        {
            throw new LicenseSchemaException(
                "Optional licence field 'deviceBinding' must be a JSON object.");
        }

        EnsureUnambiguousNames(binding, "device binding");
        EnsureOnlyAllowedFields(binding, DeviceBindingFields, "device binding");

        var scheme = GetRequiredString(binding, "scheme", "device binding");
        if (!string.Equals(scheme, DeviceIdentity.Scheme, StringComparison.Ordinal))
        {
            throw new LicenseSchemaException(
                $"Unsupported device-binding scheme: '{scheme}'.");
        }

        var deviceId = GetRequiredString(binding, "deviceId", "device binding");
        if (!DeviceIdentity.IsValidDeviceId(deviceId))
        {
            throw new LicenseSchemaException(
                "Device binding field 'deviceId' must be a 64-character SHA-256 hexadecimal value.");
        }

        return new DeviceBinding(
            scheme,
            deviceId.ToUpperInvariant(),
            GetOptionalString(binding, "deviceName", "device binding"),
            binding);
    }

    private static ActivationData? GetOptionalActivation(JsonObject license)
    {
        if (!license.TryGetPropertyValue("activation", out var value))
            return null;

        if (value is not JsonObject activation)
        {
            throw new LicenseSchemaException(
                "Optional licence field 'activation' must be a JSON object.");
        }

        EnsureUnambiguousNames(activation, "activation");
        EnsureOnlyAllowedFields(activation, ActivationFields, "activation");

        var mode = GetRequiredString(activation, "mode", "activation");
        if (mode is not ("online" or "offline"))
        {
            throw new LicenseSchemaException(
                "Activation field 'mode' must be either 'online' or 'offline'.");
        }

        var activatedAt = GetRequiredInstant(activation, "activatedAt", "activation");
        var refreshAfter = GetOptionalInstant(activation, "refreshAfter", "activation");
        var leaseExpiresAt = GetOptionalInstant(activation, "leaseExpiresAt", "activation");

        if (mode == "online" && (refreshAfter is null || leaseExpiresAt is null))
        {
            throw new LicenseSchemaException(
                "Online activations require both 'refreshAfter' and 'leaseExpiresAt'.");
        }

        if (refreshAfter is not null && refreshAfter <= activatedAt)
            throw new LicenseSchemaException("Activation 'refreshAfter' must be after 'activatedAt'.");

        if (leaseExpiresAt is not null
            && (leaseExpiresAt <= activatedAt
                || (refreshAfter is not null && leaseExpiresAt <= refreshAfter)))
        {
            throw new LicenseSchemaException(
                "Activation 'leaseExpiresAt' must be after 'activatedAt' and 'refreshAfter'.");
        }

        return new ActivationData(
            GetRequiredString(activation, "activationId", "activation"),
            mode,
            activatedAt,
            refreshAfter,
            leaseExpiresAt,
            activation);
    }

    private static string? GetOptionalString(JsonObject node, string name, string context)
    {
        if (!node.TryGetPropertyValue(name, out var value))
            return null;

        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new LicenseSchemaException(
                $"Optional field '{name}' in {context} must be a non-empty string when present.");
        }

        return text;
    }

    private static int GetRequiredPositiveInteger(
        JsonObject node,
        string name,
        string context)
    {
        if (node[name] is not JsonValue value
            || !value.TryGetValue<int>(out var number)
            || number <= 0)
        {
            throw new LicenseSchemaException(
                $"Required field '{name}' in {context} must be a positive integer.");
        }

        return number;
    }

    private static DateTimeOffset GetRequiredInstant(
        JsonObject node,
        string name,
        string context)
    {
        if (!node.TryGetPropertyValue(name, out var value))
        {
            throw new LicenseSchemaException(
                $"Required field '{name}' is missing from {context}.");
        }

        return ParseInstant(value, name, context);
    }

    private static DateTimeOffset? GetOptionalInstant(
        JsonObject node,
        string name,
        string context)
    {
        return node.TryGetPropertyValue(name, out var value)
            ? ParseInstant(value, name, context)
            : null;
    }

    private static DateTimeOffset ParseInstant(
        JsonNode? value,
        string name,
        string context)
    {
        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
            || !HasExplicitOffset(text)
            || !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw new LicenseSchemaException(
                $"Field '{name}' in {context} must be an ISO-8601 timestamp " +
                "with a timezone, such as 2030-12-31T23:59:59Z.");
        }

        return parsed.ToUniversalTime();
    }

    private static DateOnly? GetOptionalDate(
        JsonObject node,
        string name,
        string context)
    {
        if (!node.TryGetPropertyValue(name, out var value))
            return null;

        if (value is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new LicenseSchemaException(
                $"Field '{name}' in {context} must use the ISO date format yyyy-MM-dd.");
        }

        return ParseDate(text, $"Field '{name}' in {context}");
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;

        if (value.Length < 6)
            return false;

        var offsetStart = value.Length - 6;

        return (value[offsetStart] == '+' || value[offsetStart] == '-')
            && char.IsDigit(value[offsetStart + 1])
            && char.IsDigit(value[offsetStart + 2])
            && value[offsetStart + 3] == ':'
            && char.IsDigit(value[offsetStart + 4])
            && char.IsDigit(value[offsetStart + 5]);
    }

    private static void EnsureUnambiguousNames(JsonObject node, string context)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in node)
        {
            if (!names.Add(property.Key))
            {
                throw new LicenseSchemaException(
                    $"{context} contains ambiguous field names that differ only by case: " +
                    $"'{property.Key}'.");
            }
        }
    }

    private static void EnsureOnlyAllowedFields(
        JsonObject node,
        HashSet<string> allowedFields,
        string context)
    {
        foreach (var property in node)
        {
            if (!allowedFields.Contains(property.Key))
            {
                throw new LicenseSchemaException(
                    $"Unsupported field '{property.Key}' in {context}. " +
                    "Custom fields are allowed only inside licence metadata.");
            }
        }
    }

    private static bool IsLowerCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] is < 'a' or > 'z')
            return false;

        return name.All(character =>
            character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9');
    }
}
