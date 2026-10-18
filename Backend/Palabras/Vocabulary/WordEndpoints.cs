using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Vocabulary;

internal static class WordEndpoints
{
    internal static void MapWordEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/words").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{externalId}", GetAsync);
    }

    private static async Task<IResult> ListAsync(DatabaseSettings settings, string? status, string? level, string? topic, string? partOfSpeech,
        int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var currentPage = page ?? 1;
        var size = pageSize ?? 20;
        if (currentPage < 1 || size is < 1 or > 100 || currentPage > int.MaxValue / size)
        {
            return Results.BadRequest(new { error = "invalid_paging" });
        }
        await using var database = settings.CreateContext();
        // Authenticated catalog, NOT a learner session. Reviewer needs all lifecycle states.
        var query = database.Words.AsNoTracking().AsQueryable();
        if (status != null) { query = query.Where(word => word.Status == status); }
        if (level != null) { query = query.Where(word => word.Cefr == level); }
        if (topic != null) { query = query.Where(word => word.Topics.Any(tag => tag.Topic == topic)); }
        if (partOfSpeech != null) { query = query.Where(word => word.PartOfSpeech == partOfSpeech); }
        var total = await query.CountAsync(cancellationToken);
        var words = await query.Include(word => word.Senses).Include(word => word.Alternatives).Include(word => word.Topics)
            .OrderBy(word => word.ExternalId).Skip((currentPage - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new { items = words.Select(Content).ToArray(), total, page = currentPage, pageSize = size });
    }

    private static async Task<IResult> GetAsync(string externalId, DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var database = settings.CreateContext();
        var word = await database.Words.AsNoTracking().Include(word => word.Senses).Include(word => word.Alternatives).Include(word => word.Topics)
            .SingleOrDefaultAsync(word => word.ExternalId == externalId, cancellationToken);
        return word == null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(Content(word));
    }

    // Full schema-shaped DTO, not EF navigations (no cycles, no surrogate-id/headword confusion).
    private static JsonElement Content(Word word)
    {
        var content = JsonNode.Parse(word.ContentJson)!.AsObject();
        content["id"] = word.ExternalId;
        content["headword"] = word.Headword;
        SetOptional(content, "article", word.Article);
        SetOptional(content, "gender", word.Gender);
        content["partOfSpeech"] = word.PartOfSpeech;
        content["cefr"] = word.Cefr;
        content["status"] = word.Status;
        content["register"] = word.Register;
        SetOptional(content, "registerNotes", word.RegisterNotes);
        var region = new JsonObject { ["variant"] = word.RegionVariant, ["scope"] = word.RegionScope };
        SetOptional(region, "notes", word.RegionNotes);
        content["region"] = region;
        content["createdAt"] = word.CreatedAt.ToString("O");
        content["updatedAt"] = word.UpdatedAt.ToString("O");
        content["topics"] = new JsonArray(word.Topics.OrderBy(topic => topic.Topic, StringComparer.Ordinal).Select(topic => (JsonNode?)JsonValue.Create(topic.Topic)).ToArray());
        content["senses"] = new JsonArray(word.Senses.OrderBy(sense => sense.Position).Select(sense =>
        {
            var item = new JsonObject { ["gloss"] = sense.Gloss, ["example"] = sense.Example, ["exampleGloss"] = sense.ExampleGloss };
            SetOptional(item, "register", sense.Register);
            SetOptional(item, "notes", sense.Notes);
            return (JsonNode)item;
        }).ToArray());
        content["alternatives"] = new JsonArray(word.Alternatives.OrderBy(alternative => alternative.Position).Select(alternative =>
        {
            var item = new JsonObject { ["kind"] = alternative.Kind, ["value"] = alternative.Value, ["reason"] = alternative.Reason };
            SetOptional(item, "gloss", alternative.Gloss);
            SetOptional(item, "targetId", alternative.TargetId);
            SetOptional(item, "example", alternative.Example);
            return (JsonNode)item;
        }).ToArray());
        return JsonSerializer.SerializeToElement(content);
    }

    private static void SetOptional(JsonObject item, string field, string? value)
    {
        if (value == null) { item.Remove(field); }
        else { item[field] = value; }
    }
}
