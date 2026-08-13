using System.Text.Json;
using System.Text.Json.Nodes;

namespace LicenseValidator;

internal static class JsonFieldResolver
{
    public static JsonNode? Resolve(JsonNode root, string path)
    {
        JsonNode? current = root;

        foreach (var segment in path.Split('.'))
        {
            if (string.IsNullOrWhiteSpace(segment) || current is not JsonObject objectNode)
                return null;

            current = GetProperty(objectNode, segment);

            if (current is null)
                return null;
        }

        return current;
    }

    public static string Format(JsonNode value)
    {
        return value is JsonValue scalar
            && scalar.TryGetValue<string>(out var text)
                ? text
                : value.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode? GetProperty(JsonObject node, string name)
    {
        foreach (var property in node)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }
}
