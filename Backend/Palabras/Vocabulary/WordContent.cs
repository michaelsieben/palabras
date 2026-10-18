using System.Globalization;
using System.Text;
using System.Text.Json;
using Palabras.Persistence;

namespace Palabras.Vocabulary;

internal static class WordContent
{
    internal static string CanonicalJson(JsonElement entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { WriteCanonical(writer, entry); }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) { WriteCanonical(writer, item); }
            writer.WriteEndArray();
        }
        else { element.WriteTo(writer); }
    }

    internal static void Apply(Word word, JsonElement entry)
    {
        word.ExternalId = entry.GetProperty("id").GetString()!;
        word.Headword = entry.GetProperty("headword").GetString()!;
        word.Article = Optional(entry, "article");
        word.Gender = Optional(entry, "gender");
        word.PartOfSpeech = entry.GetProperty("partOfSpeech").GetString()!;
        word.Cefr = entry.GetProperty("cefr").GetString()!;
        var region = entry.GetProperty("region");
        word.RegionVariant = region.GetProperty("variant").GetString()!;
        word.RegionScope = region.GetProperty("scope").GetString()!;
        word.RegionNotes = Optional(region, "notes");
        word.Register = entry.GetProperty("register").GetString()!;
        word.RegisterNotes = Optional(entry, "registerNotes");
        word.Status = entry.GetProperty("status").GetString()!;
        word.CreatedAt = DateTimeOffset.Parse(entry.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture);
        word.UpdatedAt = Optional(entry, "updatedAt") is { } updated ? DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture) : word.CreatedAt;
        word.ContentJson = CanonicalJson(entry);
        var position = 0;
        foreach (var item in entry.GetProperty("senses").EnumerateArray())
        {
            var sense = word.Senses.SingleOrDefault(sense => sense.Position == position);
            if (sense == null) { sense = new Sense { Position = position }; word.Senses.Add(sense); }
            sense.Gloss = item.GetProperty("gloss").GetString()!;
            sense.Example = item.GetProperty("example").GetString()!;
            sense.ExampleGloss = item.GetProperty("exampleGloss").GetString()!;
            sense.Register = Optional(item, "register");
            sense.Notes = Optional(item, "notes");
            position++;
        }
        word.Senses.RemoveAll(sense => sense.Position >= position);
        position = 0;
        foreach (var item in entry.GetProperty("alternatives").EnumerateArray())
        {
            var alternative = word.Alternatives.SingleOrDefault(alternative => alternative.Position == position);
            if (alternative == null) { alternative = new Alternative { Position = position }; word.Alternatives.Add(alternative); }
            alternative.Kind = item.GetProperty("kind").GetString()!;
            alternative.Value = item.GetProperty("value").GetString()!;
            alternative.Gloss = Optional(item, "gloss");
            alternative.Reason = item.GetProperty("reason").GetString()!;
            alternative.TargetId = Optional(item, "targetId");
            alternative.Example = Optional(item, "example");
            position++;
        }
        word.Alternatives.RemoveAll(alternative => alternative.Position >= position);
        var topics = entry.GetProperty("topics").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        word.Topics.RemoveAll(topic => !topics.Contains(topic.Topic));
        foreach (var topic in topics)
        {
            if (!word.Topics.Any(existing => existing.Topic == topic)) { word.Topics.Add(new WordTopic { Topic = topic }); }
        }
    }

    private static string? Optional(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) ? value.GetString() : null;
}
