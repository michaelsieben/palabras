using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Content.Council;
using Palabras.Persistence;
using Palabras.Review;
using Xunit;

namespace Palabras.Tests;

// M3b C3/C4, reviewer-facing half: a council word must reach the sheet with one chip per
// alternative field and the chip's support ratio ("2/3" = proposing models over the run's roster).
// The queue prefers the M3b `field` column and falls back to the legacy `kind` vocabulary.
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
        // T-12: support is "<proposing models>/<models_requested>" from the run that imported the word.
        Assert.Equal(new[] { "2/3", "2/3", "1/3", "0/3", "0/3" },
            chips.Select(chip => chip.GetProperty("support").GetString()).ToArray());

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

    [Fact]
    public async Task ChoosingAnExampleChipSwapsBothTheSentenceAndItsTranslationAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var imported = await new CouncilImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(Path.Combine(CouncilFixtureRoot(), "valid-propose"), new CouncilImportOptions(),
                CancellationToken.None);
        Assert.Empty(imported.Refusals);

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var patata = body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "es-patata-01");
        var exampleChip = patata.GetProperty("alternatives").EnumerateArray()
            .Single(chip => chip.GetProperty("field").GetString() == "example");
        // The chip's translation is the English sentence the alternative also carries.
        Assert.Equal("I buy potatoes at the market.", exampleChip.GetProperty("translation").GetString());

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
                    field = "example",
                    senseOrder = 0,
                    alternativeId = exampleChip.GetProperty("id").GetInt32()
                }
            }
        });
        using var actionBody = await JsonAsync(action);
        Assert.Equal("applied", Assert.Single(actionBody.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("result").GetString());

        await using var database = factory.OpenDatabase();
        var approved = await database.Words.SingleAsync(candidate => candidate.Key == "es-patata-01");
        Assert.Equal(2, approved.Version);
        var sense = await database.Senses.SingleAsync(row => row.WordId == approved.Id);
        // Swapping the sentence must not leave the English translation describing the old one.
        Assert.Equal("Compro patatas en el mercado.", sense.ExampleEs);
        Assert.Equal("I buy potatoes at the market.", sense.ExampleEn);
        var snapshot = JsonNode.Parse((await database.WordRevisions
            .Where(revision => revision.WordId == approved.Id && revision.Action == "edit")
            .OrderByDescending(revision => revision.Id).FirstAsync()).SnapshotJson)!;
        Assert.Equal("Compro patatas en el mercado.", snapshot["senses"]![0]!["example"]!.GetValue<string>());
        Assert.Equal("I buy potatoes at the market.", snapshot["senses"]![0]!["exampleGloss"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChipFieldPrefersTheStoredFieldOverTheLegacyKindAsync()
    {
        // `field` is the M3b truth; `kind` is the legacy vocabulary. A row where the two disagree
        // must project by `field`, and a legacy row with no field must still resolve by `kind`.
        using var factory = new ReviewApplicationFactory(Now, database =>
        {
            AddWord(database, 1, "es-abuela-01", "abuela");
            AddWord(database, 2, "es-casa-01", "casa");
            database.WordAlternatives.Add(new WordAlternative
            {
                WordId = 1, Kind = "synonym", Field = "gloss", Value = "grandmother", Reason = "stored field wins"
            });
            database.WordAlternatives.Add(new WordAlternative
            {
                WordId = 2, Kind = "synonym", Field = null, Value = "hogar", Reason = "legacy kind fallback"
            });
        });

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var abuela = items.Single(item => item.GetProperty("key").GetString() == "es-abuela-01");
        var casa = items.Single(item => item.GetProperty("key").GetString() == "es-casa-01");

        var stored = Assert.Single(abuela.GetProperty("alternatives").EnumerateArray());
        Assert.Equal("gloss", stored.GetProperty("field").GetString());
        // A legacy word has no run, so its support denominator is unknown and the member is omitted.
        Assert.False(stored.TryGetProperty("support", out _));

        var legacy = Assert.Single(casa.GetProperty("alternatives").EnumerateArray());
        Assert.Equal("lemma", legacy.GetProperty("field").GetString());

        // The write path resolves the same way: choosing the stored-field chip swaps a gloss, and
        // its legacy `kind` (synonym -> lemma) does not misdirect it to the headword.
        using var action = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(),
                    wordId = abuela.GetProperty("wordId").GetInt32(),
                    wordVersion = abuela.GetProperty("version").GetInt32(),
                    action = "approve_alt",
                    field = "gloss",
                    senseOrder = 0,
                    alternativeId = stored.GetProperty("id").GetInt32()
                }
            }
        });
        using var actionBody = await JsonAsync(action);
        Assert.Equal("applied", Assert.Single(actionBody.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("result").GetString());
        await using var database = factory.OpenDatabase();
        Assert.Equal("grandmother", (await database.Senses.SingleAsync(sense => sense.WordId == 1)).EnGloss);
    }

    [Fact]
    public async Task UndoingAnExampleSwapRestoresTheSentenceAndItsTranslationAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var imported = await new CouncilImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(Path.Combine(CouncilFixtureRoot(), "valid-propose"), new CouncilImportOptions(),
                CancellationToken.None);
        Assert.Empty(imported.Refusals);

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var patata = body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "es-patata-01");
        var wordId = patata.GetProperty("wordId").GetInt32();
        var exampleChip = patata.GetProperty("alternatives").EnumerateArray()
            .Single(chip => chip.GetProperty("field").GetString() == "example");

        using (var swap = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId,
                    wordVersion = patata.GetProperty("version").GetInt32(), action = "approve_alt",
                    field = "example", senseOrder = 0,
                    alternativeId = exampleChip.GetProperty("id").GetInt32()
                }
            }
        }))
        {
            using var swapBody = await JsonAsync(swap);
            Assert.Equal("applied", Assert.Single(swapBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        int undoId;
        await using (var database = factory.OpenDatabase())
        {
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("Compro patatas en el mercado.", sense.ExampleEs);
            Assert.Equal("I buy potatoes at the market.", sense.ExampleEn);
            var undo = await database.WordAlternatives.SingleAsync(alternative =>
                alternative.WordId == wordId && alternative.Kind == "previous_example");
            // The old English rides on the undo row's gloss, so nothing extra is needed to restore it.
            Assert.Equal("¿Nos pones unas patatas bravas?", undo.Value);
            Assert.Equal("Can you bring us some patatas bravas?", undo.Gloss);
            undoId = undo.Id;
        }

        // approve_alt is a pending_review action; demote the freshly approved word first (the same
        // path the sheet's undo takes).
        using (var demote = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "demote", wordVersion: 2) }
        }))
        {
            using var demoteBody = await JsonAsync(demote);
            Assert.Equal("applied", Assert.Single(demoteBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var undo = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 2,
                    action = "approve_alt", field = "example", senseOrder = 0, alternativeId = undoId
                }
            }
        }))
        {
            using var undoBody = await JsonAsync(undo);
            Assert.Equal("applied", Assert.Single(undoBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync(candidate => candidate.Id == wordId);
            Assert.Equal(3, word.Version);
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("¿Nos pones unas patatas bravas?", sense.ExampleEs);
            Assert.Equal("Can you bring us some patatas bravas?", sense.ExampleEn);
            var snapshot = JsonNode.Parse((await database.WordRevisions
                .Where(revision => revision.WordId == wordId && revision.Version == 3)
                .OrderByDescending(revision => revision.Id).FirstAsync()).SnapshotJson)!;
            Assert.Equal("¿Nos pones unas patatas bravas?", snapshot["senses"]![0]!["example"]!.GetValue<string>());
            Assert.Equal("Can you bring us some patatas bravas?",
                snapshot["senses"]![0]!["exampleGloss"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ApproveEditOfAnExampleKeepsTheTranslationTheSchemaRequiresAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var imported = await new CouncilImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(Path.Combine(CouncilFixtureRoot(), "valid-propose"), new CouncilImportOptions(),
                CancellationToken.None);
        Assert.Empty(imported.Refusals);

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var patata = body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "es-patata-01");
        var wordId = patata.GetProperty("wordId").GetInt32();

        using (var edit = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "example", senseOrder = 0, after = "Compro patatas en el mercado." } }
                }
            }
        }))
        {
            using var editBody = await JsonAsync(edit);
            Assert.Equal("applied", Assert.Single(editBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        int undoId;
        await using (var database = factory.OpenDatabase())
        {
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("Compro patatas en el mercado.", sense.ExampleEs);
            // approve_edit carries no English input and word.schema.json requires a non-empty
            // exampleGloss, so the previous translation is kept. The old English also rides on the
            // undo row, so demote -> undo can still restore the exact pair.
            Assert.Equal("Can you bring us some patatas bravas?", sense.ExampleEn);
            var snapshot = JsonNode.Parse((await database.WordRevisions
                .Where(revision => revision.WordId == wordId && revision.Action == "edit")
                .OrderByDescending(revision => revision.Id).FirstAsync()).SnapshotJson)!;
            Assert.Equal("Can you bring us some patatas bravas?",
                snapshot["senses"]![0]!["exampleGloss"]!.GetValue<string>());
            var undo = await database.WordAlternatives.SingleAsync(alternative =>
                alternative.WordId == wordId && alternative.Kind == "previous_example");
            Assert.Equal("¿Nos pones unas patatas bravas?", undo.Value);
            Assert.Equal("Can you bring us some patatas bravas?", undo.Gloss);
            undoId = undo.Id;
        }

        using (var demote = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "demote", wordVersion: 2) }
        }))
        {
            using var demoteBody = await JsonAsync(demote);
            Assert.Equal("applied", Assert.Single(demoteBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var undo = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 2,
                    action = "approve_alt", field = "example", senseOrder = 0, alternativeId = undoId
                }
            }
        }))
        {
            using var undoBody = await JsonAsync(undo);
            Assert.Equal("applied", Assert.Single(undoBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        await using (var database = factory.OpenDatabase())
        {
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("¿Nos pones unas patatas bravas?", sense.ExampleEs);
            Assert.Equal("Can you bring us some patatas bravas?", sense.ExampleEn);
        }
    }

    [Fact]
    public async Task ApproveEditThenCouncilExampleSwapAndUndoRestoresBothLanguagesAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var imported = await new CouncilImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(Path.Combine(CouncilFixtureRoot(), "valid-propose"), new CouncilImportOptions(),
                CancellationToken.None);
        Assert.Empty(imported.Refusals);

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var patata = body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "es-patata-01");
        var wordId = patata.GetProperty("wordId").GetInt32();
        var exampleChip = patata.GetProperty("alternatives").EnumerateArray()
            .Single(chip => chip.GetProperty("field").GetString() == "example");

        // 1. approve_edit the Spanish example (the wire form has no English field).
        using (var edit = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "example", senseOrder = 0, after = "Siempre compro patatas en el mercado." } }
                }
            }
        }))
        {
            using var editBody = await JsonAsync(edit);
            Assert.Equal("applied", Assert.Single(editBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var demote = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "demote", wordVersion: 2) }
        }))
        {
            using var demoteBody = await JsonAsync(demote);
            Assert.Equal("applied", Assert.Single(demoteBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        // 2. Swap to the council example: both languages become the chip's pair.
        using (var swap = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 2, action = "approve_alt",
                    field = "example", senseOrder = 0, alternativeId = exampleChip.GetProperty("id").GetInt32()
                }
            }
        }))
        {
            using var swapBody = await JsonAsync(swap);
            Assert.Equal("applied", Assert.Single(swapBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        int undoId;
        await using (var database = factory.OpenDatabase())
        {
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("Compro patatas en el mercado.", sense.ExampleEs);
            Assert.Equal("I buy potatoes at the market.", sense.ExampleEn);
            // The newest previous_example row is the swap's, and it carries the pre-swap English.
            undoId = (await database.WordAlternatives
                .Where(alternative => alternative.WordId == wordId && alternative.Kind == "previous_example")
                .OrderByDescending(alternative => alternative.Id).FirstAsync()).Id;
        }
        using (var demote = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "demote", wordVersion: 3) }
        }))
        {
            using var demoteBody = await JsonAsync(demote);
            Assert.Equal("applied", Assert.Single(demoteBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        // 3. Undo the swap: the pre-swap pair comes back, both languages.
        using (var undo = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 3, action = "approve_alt",
                    field = "example", senseOrder = 0, alternativeId = undoId
                }
            }
        }))
        {
            using var undoBody = await JsonAsync(undo);
            Assert.Equal("applied", Assert.Single(undoBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync(candidate => candidate.Id == wordId);
            Assert.Equal(4, word.Version);
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("Siempre compro patatas en el mercado.", sense.ExampleEs);
            Assert.Equal("Can you bring us some patatas bravas?", sense.ExampleEn);
        }
    }

    [Fact]
    public async Task ApproveEditThenCouncilExampleSwapAndUndoRestoresAnEmptyTranslationAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var imported = await new CouncilImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(Path.Combine(CouncilFixtureRoot(), "valid-propose"), new CouncilImportOptions(),
                CancellationToken.None);
        Assert.Empty(imported.Refusals);

        int wordId;
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync(candidate => candidate.Key == "es-patata-01");
            wordId = word.Id;
            // A row whose English was already empty before the edit: the undo must put the empty
            // string back instead of leaving the swapped-in council translation in place.
            (await database.Senses.SingleAsync(row => row.WordId == word.Id)).ExampleEn = "";
            await database.SaveChangesAsync();
        }

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var patata = body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "es-patata-01");
        var exampleChip = patata.GetProperty("alternatives").EnumerateArray()
            .Single(chip => chip.GetProperty("field").GetString() == "example");

        using (var edit = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "example", senseOrder = 0, after = "Siempre compro patatas en el mercado." } }
                }
            }
        }))
        {
            using var editBody = await JsonAsync(edit);
            Assert.Equal("applied", Assert.Single(editBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var demote = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "demote", wordVersion: 2) }
        }))
        {
            using var demoteBody = await JsonAsync(demote);
            Assert.Equal("applied", Assert.Single(demoteBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var swap = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 2, action = "approve_alt",
                    field = "example", senseOrder = 0, alternativeId = exampleChip.GetProperty("id").GetInt32()
                }
            }
        }))
        {
            using var swapBody = await JsonAsync(swap);
            Assert.Equal("applied", Assert.Single(swapBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        int undoId;
        await using (var database = factory.OpenDatabase())
        {
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("I buy potatoes at the market.", sense.ExampleEn);
            var undo = await database.WordAlternatives
                .Where(alternative => alternative.WordId == wordId && alternative.Kind == "previous_example")
                .OrderByDescending(alternative => alternative.Id).FirstAsync();
            // The pre-swap English was a known empty string, not an unknown legacy gloss.
            Assert.Equal("", undo.Gloss);
            undoId = undo.Id;
        }
        using (var demote = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[] { Action(Guid.NewGuid().ToString(), wordId, "demote", wordVersion: 3) }
        }))
        {
            using var demoteBody = await JsonAsync(demote);
            Assert.Equal("applied", Assert.Single(demoteBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }
        using (var undo = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId, wordVersion = 3, action = "approve_alt",
                    field = "example", senseOrder = 0, alternativeId = undoId
                }
            }
        }))
        {
            using var undoBody = await JsonAsync(undo);
            Assert.Equal("applied", Assert.Single(undoBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        await using (var database = factory.OpenDatabase())
        {
            var sense = await database.Senses.SingleAsync(row => row.WordId == wordId);
            Assert.Equal("Siempre compro patatas en el mercado.", sense.ExampleEs);
            Assert.Equal("", sense.ExampleEn);
        }
    }

    [Fact]
    public async Task ApproveEditOfAnExampleWritesASchemaValidSnapshotAsync()
    {
        using var factory = new ReviewApplicationFactory(Now);
        var corpus = await SyntheticCorpus.WriteAsync(Path.Combine(factory.ImportDirectory),
            new SyntheticCorpus.Entry("es-casa-01", "casa", "A2", 10));
        var imported = await new ContentImporter(new DatabaseSettings(factory.DatabasePath))
            .ImportAsync(corpus, false, CancellationToken.None);
        Assert.Empty(imported.Rejected);

        using var client = await LoginAsync(factory, "reviewer");
        using var body = await JsonAsync(await client.GetAsync("/api/review/queue", CancellationToken.None));
        var casa = body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "es-casa-01");
        using (var edit = await PostAsync(client, "/api/review/actions", new
        {
            actions = new[]
            {
                new
                {
                    clientReviewId = Guid.NewGuid().ToString(), wordId = casa.GetProperty("wordId").GetInt32(),
                    wordVersion = 1, action = "approve_edit",
                    edits = new object[] { new { field = "example", senseOrder = 0, after = "Vivo en una casa muy tranquila." } }
                }
            }
        }))
        {
            using var editBody = await JsonAsync(edit);
            Assert.Equal("applied", Assert.Single(editBody.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("result").GetString());
        }

        string snapshotJson;
        await using (var database = factory.OpenDatabase())
        {
            var word = await database.Words.SingleAsync(candidate => candidate.Key == "es-casa-01");
            snapshotJson = (await database.WordRevisions
                .Where(revision => revision.WordId == word.Id && revision.Action == "edit")
                .OrderByDescending(revision => revision.Id).FirstAsync()).SnapshotJson;
        }

        // The revision snapshot stays a corpus document. Reuse the importer's schema file and
        // evaluation options rather than hand-checking the shape: exampleGloss is required with
        // minLength 1, so "" or an omitted key would make this fail.
        var snapshot = JsonNode.Parse(snapshotJson)!;
        Assert.Equal("The casa is here.",
            snapshot["senses"]![0]!["exampleGloss"]!.GetValue<string>());
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "content", "schema",
            "word.schema.json"));
        var evaluation = schema.Evaluate(snapshot, new EvaluationOptions
        {
            RequireFormatValidation = true,
            OutputFormat = OutputFormat.List
        });
        Assert.True(evaluation.IsValid, snapshotJson);
    }

    [Fact]
    public void RegionalNoteIsStoredButNotAReviewableChip()
    {
        // Recorded decision: the sheet has no regional_note tab and approve_alt has no swap target
        // for a note (it would fall through to ExampleEs), so the note is stored, not surfaced.
        Assert.Null(ReviewTransitions.FieldForAlternativeKind("regional_note"));
        Assert.Equal("example", ReviewTransitions.FieldForAlternativeKind("example"));
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
