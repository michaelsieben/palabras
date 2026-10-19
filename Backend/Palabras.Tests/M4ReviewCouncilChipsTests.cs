using Microsoft.EntityFrameworkCore;
using Palabras.Content.Council;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

// M3b C3/T-12, reviewer-facing half: a council word must reach the sheet with one chip per
// alternative field even though the importer lands before the endpoint's `field` preference (C4).
// The queue still resolves a chip through ReviewTransitions.FieldForAlternativeKind (the legacy
// `kind` path), which C3 taught the ΔF field vocabulary.
public sealed partial class M4ReviewApiTests
{
    [Fact]
    public async Task CouncilWordQueueShowsAChipForEveryReviewableAlternativeFieldAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var result = await new CouncilImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(Path.Combine(CouncilFixtureRoot(), "valid-propose"), new CouncilImportOptions(),
                CancellationToken.None);
        Assert.Empty(result.Refusals);
        Assert.Equal(2, result.Created);

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);

        var patata = items.Single(item => item.GetProperty("key").GetString() == "es-patata-01");
        var chips = patata.GetProperty("alternatives").EnumerateArray().ToArray();
        Assert.Equal(new[] { "example", "gloss", "gloss", "lemma", "regional_variant" },
            chips.Select(chip => chip.GetProperty("field").GetString()).ToArray());
        Assert.Equal(new[] { "Compro patatas en el mercado.", "potato (the vegetable)",
                "potatoes (as a bar dish)", "papa", "patatilla" },
            chips.Select(chip => chip.GetProperty("text").GetString()).ToArray());
        Assert.All(chips, chip => Assert.False(string.IsNullOrWhiteSpace(chip.GetProperty("reason").GetString())));

        var nevera = items.Single(item => item.GetProperty("key").GetString() == "es-nevera-01");
        var neveraChips = nevera.GetProperty("alternatives").EnumerateArray().ToArray();
        Assert.Equal(7, neveraChips.Length);
        Assert.Contains(neveraChips, chip => chip.GetProperty("field").GetString() == "register_variant");
        Assert.Equal("household", nevera.GetProperty("topic").GetString());

        // The chip is not only visible: choosing it swaps the gloss and re-approves the word, which
        // also proves the imported snapshot has the shape the review state machine reads.
        var glossChip = chips.First(chip => chip.GetProperty("field").GetString() == "gloss"
            && chip.GetProperty("text").GetString() == "potato (the vegetable)");
        using var action = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(),
                    wordId = patata.GetProperty("wordId").GetInt32(),
                    wordVersion = patata.GetProperty("version").GetInt32(),
                    action = "approve_alt",
                    field = "gloss",
                    senseOrder = 0,
                    alternativeId = glossChip.GetProperty("id").GetInt32()
                }
            }
        });
        using var actionBody = await JsonAsync(action);
        Assert.Equal("applied", Assert.Single(actionBody.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("result").GetString());
        await using var database = factory.OpenDatabase();
        var approved = await database.Words.SingleAsync(candidate => candidate.Key == "es-patata-01");
        Assert.Equal("approved", approved.Status);
        Assert.Equal(2, approved.Version);
        Assert.Equal("potato (the vegetable)",
            (await database.Senses.SingleAsync(sense => sense.WordId == approved.Id)).EnGloss);
    }

    private static string CouncilFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "council", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate tools/council/fixtures.");
    }
}
