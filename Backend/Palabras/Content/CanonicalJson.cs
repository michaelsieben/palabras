using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Palabras.Content;

// Canonical JSON used as the identity of a content document: object keys sorted ordinally, strings
// decoded before writing (so literal UTF-8 and equivalent \u escapes hash identically), array order
// preserved. ContentImporter still carries its private copy of the same algorithm until the council
// importer lands (C3); the two must never drift, so ContentBytes is the shape ContentImporter needs.
internal static class CanonicalJson
{
    private static readonly string[] MutableContentRootProperties = ["review", "status", "updatedAt"];

    internal static byte[] Bytes(JsonNode root) => Write(root, skipRootProperties: null);

    // Root lifecycle fields are reviewer/import state, not source content: the content hash keeps
    // tracking the source document after the reviewer changes status or writes review metadata.
    internal static byte[] ContentBytes(JsonNode root) => Write(root, MutableContentRootProperties);

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Write(JsonNode root, IReadOnlyCollection<string>? skipRootProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNode(writer, root, skipRootProperties, true);
        }
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node, IReadOnlyCollection<string>? skipRootProperties, bool isRoot)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var property in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                if (isRoot && skipRootProperties != null && skipRootProperties.Contains(property.Key, StringComparer.Ordinal))
                {
                    continue;
                }
                writer.WritePropertyName(property.Key);
                WriteNode(writer, property.Value, skipRootProperties, false);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (var item in array)
            {
                WriteNode(writer, item, skipRootProperties, false);
            }
            writer.WriteEndArray();
        }
        else if (node == null)
        {
            writer.WriteNullValue();
        }
        else if (node.GetValueKind() == JsonValueKind.String)
        {
            writer.WriteStringValue(node.GetValue<string>());
        }
        else
        {
            node.WriteTo(writer);
        }
    }
}
