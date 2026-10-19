using System.Text.Json.Nodes;

namespace Palabras.Tests;

// Minimal schema-valid, one-sense entries whose ranks the caller chooses. CLI tests use these
// instead of the real corpus so a rank assertion cannot drift when a content batch lands.
internal static class SyntheticCorpus
{
    internal sealed record Entry(string Key, string Headword, string Cefr, int? Difficulty,
        string Status = "pending_review");

    internal static async Task<string> WriteAsync(string directory, params Entry[] entries)
    {
        Directory.CreateDirectory(directory);
        foreach (var entry in entries)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, entry.Key + ".json"), Json(entry));
        }
        return directory;
    }

    private static string Json(Entry entry)
    {
        var root = new JsonObject
        {
            ["id"] = entry.Key,
            ["headword"] = entry.Headword,
            ["partOfSpeech"] = "noun",
            ["article"] = "el",
            ["gender"] = "masculine",
            ["cefr"] = entry.Cefr,
            ["region"] = new JsonObject { ["variant"] = "es-ES", ["scope"] = "peninsular-generic" },
            ["register"] = "neutral",
            ["topics"] = new JsonArray(JsonValue.Create("household")),
            ["senses"] = new JsonArray(new JsonObject
            {
                ["gloss"] = "test gloss",
                ["example"] = "El " + entry.Headword + " está aquí.",
                ["exampleGloss"] = "The " + entry.Headword + " is here."
            }),
            ["alternatives"] = new JsonArray(
                new JsonObject
                {
                    ["kind"] = "synonym", ["value"] = "sinonimo", ["reason"] = "A valid synonym reason."
                },
                new JsonObject
                {
                    ["kind"] = "regional", ["value"] = "regional", ["reason"] = "A valid regional reason."
                }),
            ["status"] = entry.Status,
            ["provenance"] = new JsonObject { ["method"] = "hand-authored" },
            ["createdAt"] = "2026-10-17T15:30:00Z"
        };
        if (entry.Difficulty is { } difficulty)
        {
            root["difficulty"] = difficulty;
        }
        return root.ToJsonString();
    }
}
