using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palabras.Vocabulary;

// Deliberately supports only keywords used by the committed schema, not arbitrary JSON Schema.
internal static class WordSchema
{
    private static readonly JsonDocument Schema = Load();
    internal static string SchemaText => Schema.RootElement.GetRawText();

    private static JsonDocument Load()
    {
        using var stream = typeof(WordSchema).Assembly.GetManifestResourceStream("Palabras.Vocabulary.word.schema.json")
            ?? throw new InvalidOperationException("Embedded word schema missing.");
        return JsonDocument.Parse(stream);
    }

    internal static void Validate(JsonElement entry)
    {
        Validate(entry, Schema.RootElement, "$", Schema.RootElement);
        if (entry.GetProperty("register").GetString() == "vulgar")
        {
            Require(entry.TryGetProperty("registerNotes", out var notes) && !string.IsNullOrWhiteSpace(notes.GetString()), "$.registerNotes", "vulgar requires registerNotes");
        }
        Require(!entry.TryGetProperty("article", out _) || entry.GetProperty("partOfSpeech").GetString() == "noun", "$.article", "article only allowed for nouns");
    }

    private static void Validate(JsonElement value, JsonElement schema, string path, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var resolved = root;
            foreach (var segment in reference.GetString()![2..].Split('/'))
            {
                resolved = resolved.GetProperty(segment);
            }
            Validate(value, resolved, path, root);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()) : [type.GetString()];
            var actual = value.ValueKind switch
            {
                JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
                JsonValueKind.Null => "null", _ => "unsupported"
            };
            Require(types.Contains(actual), path, "wrong JSON type");
        }
        if (schema.TryGetProperty("enum", out var choices))
        {
            Require(choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)), path, "value not in enum");
        }
        if (schema.TryGetProperty("const", out var constant))
        {
            Require(JsonElement.DeepEquals(constant, value), path, "wrong constant value");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(seen.Add(property.Name), path + "." + property.Name, "duplicate field");
            }
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var field in required.EnumerateArray())
                {
                    Require(value.TryGetProperty(field.GetString()!, out _), path, "missing " + field.GetString());
                }
            }
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out var definition))
                    {
                        Validate(property.Value, definition, path + "." + property.Name, root);
                    }
                    else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    {
                        Require(false, path + "." + property.Name, "unknown field");
                    }
                }
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var length = text.EnumerateRunes().Count();
            if (schema.TryGetProperty("minLength", out var minimum))
            {
                Require(length >= minimum.GetInt32(), path, "string too short");
            }
            if (schema.TryGetProperty("maxLength", out var maximum))
            {
                Require(length <= maximum.GetInt32(), path, "string too long");
            }
            if (schema.TryGetProperty("pattern", out var pattern))
            {
                Require(Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), path, "invalid pattern");
            }
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time")
            {
                Require(Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), path, "invalid date-time");
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var minimum))
            {
                Require(value.GetArrayLength() >= minimum.GetInt32(), path, "too few items");
            }
            if (schema.TryGetProperty("maxItems", out var maximum))
            {
                Require(value.GetArrayLength() <= maximum.GetInt32(), path, "too many items");
            }
            var items = value.EnumerateArray().ToArray();
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
            {
                for (var index = 0; index < items.Length; index++)
                {
                    Require(!items.Take(index).Any(previous => JsonElement.DeepEquals(previous, items[index])), path, "duplicate item");
                }
            }
            if (schema.TryGetProperty("items", out var definition))
            {
                for (var index = 0; index < items.Length; index++)
                {
                    Validate(items[index], definition, path + "[" + index + "]", root);
                }
            }
        }
        if (schema.TryGetProperty("allOf", out var rules))
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.TryGetProperty("if", out var condition))
                {
                    var matches = true;
                    try { Validate(value, condition, path, root); }
                    catch (InvalidDataException) { matches = false; }
                    if (matches) { Validate(value, rule.GetProperty("then"), path, root); }
                }
                else { Validate(value, rule, path, root); }
            }
        }
    }

    private static void Require(bool valid, string path, string message)
    {
        if (!valid) { throw new InvalidDataException(path + ": " + message); }
    }
}
