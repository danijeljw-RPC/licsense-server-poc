using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoftwareLicensing;

public static class CanonicalJson
{
    public static byte[] Serialize(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, document.RootElement);

        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                    Write(writer, item);

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON value: {element.ValueKind}");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out var signed))
        {
            writer.WriteNumberValue(signed);
            return;
        }

        if (element.TryGetUInt64(out var unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteNumberValue(decimalValue);
            return;
        }

        writer.WriteNumberValue(element.GetDouble());
    }
}
