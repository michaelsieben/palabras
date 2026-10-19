using System.Text.Json.Nodes;
using Json.Schema;

namespace Palabras.Content;

// Reuse import schema constraints on the fields M4 can change, without linting or revalidating
// unrelated legacy snapshot/undo metadata. Validate before mutating any tracked entity.
internal static class ReviewContentValidation
{
    private static readonly Lazy<JsonObject> Schema = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "content", "schema", "word.schema.json")))!.AsObject());

    internal static bool IsValid(string field, JsonNode value, string pos, string? article)
    {
        var root = Schema.Value;
        JsonNode constraint = field switch
        {
            "lemma" => root["properties"]!["headword"]!,
            "article" => root["properties"]!["article"]!,
            "gender" => root["properties"]!["gender"]!,
            "regional_note" => root["properties"]!["region"]!["properties"]!["notes"]!,
            "gloss" => root["$defs"]!["sense"]!["properties"]!["gloss"]!,
            "example" => root["$defs"]!["sense"]!["properties"]!["example"]!,
            "accepted" => root["$defs"]!["sense"]!["properties"]!["acceptedEs"]!,
            _ => throw new ArgumentException("Unknown editable field.", nameof(field))
        };
        if (!JsonSchema.FromText(constraint.ToJsonString()).Evaluate(value).IsValid)
            return false;
        if (field != "gender") return true;

        // The import schema also constrains gender by part of speech (noun / phrase).
        var grammarSchema = new JsonObject { ["allOf"] = root["allOf"]!.DeepClone() };
        var grammar = new JsonObject { ["partOfSpeech"] = pos, ["gender"] = value.DeepClone() };
        if (article is not null) grammar["article"] = article;
        return JsonSchema.FromText(grammarSchema.ToJsonString()).Evaluate(grammar).IsValid;
    }
}
