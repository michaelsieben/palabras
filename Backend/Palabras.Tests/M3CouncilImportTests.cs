using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Content.Council;
using Palabras.Persistence;
using Palabras.Review;
using Xunit;

namespace Palabras.Tests;

// M3b C3: the council bundle importer (plan §2.5, §3.2, §3.3, T-5…T-12, T-16). Throwaway databases
// live under /tmp/palabras-council, never /data. The bundle fixtures are the same ones the C2
// validator tests use, so the importer proves the contract the CLI is written against.
public sealed class M3CouncilImportTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T09:00:00Z");
    private static readonly long NowMs = Now.ToUnixTimeMilliseconds();
    private static readonly DateTimeOffset BundleAfterReview = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
    private static readonly DateTimeOffset BundleBeforeReview = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    private readonly string _root = Path.Combine("/tmp", "palabras-council", Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;

    public M3CouncilImportTests()
    {
        _settings = new DatabaseSettings(Path.Combine(_root, "data", "palabras.db"));
    }

    [Fact]
    public async Task ProposeBundleLandsPendingReviewWithItsRunAndModelResultsAsync()
    {
        var bundle = CopyFixture("valid-propose");
        var result = await ImportAsync(bundle);

        Assert.Empty(result.Refusals);
        Assert.False(result.AlreadyImported);
        Assert.Equal(2, result.Created);
        Assert.All(result.Candidates, candidate => Assert.Equal(CouncilImportOutcomes.Created, candidate.Outcome));
        await using var database = _settings.CreateContext();
        var manifest = ReadManifest(Path.Combine(bundle, "manifest.json"));

        var run = await database.CouncilRuns.SingleAsync();
        Assert.Equal(manifest["run_id"]!.GetValue<string>(), run.RunId);
        Assert.Equal("propose", run.Mode);
        Assert.Equal("""{"propose":"v1","critique":"v1"}""", run.PromptVersions);
        Assert.Equal(manifest["style_guide_sha256"]!.GetValue<string>(), run.StyleGuideSha256);
        Assert.Equal("food", run.Topic);
        Assert.Equal("A2", run.Cefr);
        Assert.Equal(2, run.RequestedCount);
        Assert.Equal(3, run.ModelsRequested);
        Assert.Equal(3, run.ModelsSucceededPropose);
        Assert.Equal(3, run.ModelsSucceededCritique);
        Assert.Equal(0, run.ReducedConfidence);
        Assert.Equal(2, run.CandidatesCount);
        Assert.Equal(0, run.DroppedCount);
        Assert.Equal(manifest["candidates_sha256"]!.GetValue<string>(), run.CandidatesSha256);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T18:00:00Z").ToUnixTimeMilliseconds(), run.CreatedAt);
        Assert.True(run.ImportedAt > 0);

        // One row per model per phase; tokens/cost are per model so they ride on the propose row.
        var modelResults = await database.CouncilModelResults.OrderBy(row => row.ModelId).ThenBy(row => row.Phase)
            .ToListAsync();
        Assert.Equal(6, modelResults.Count);
        var propose = modelResults.Where(row => row.Phase == "propose").ToList();
        var critique = modelResults.Where(row => row.Phase == "critique").ToList();
        Assert.All(propose, row =>
        {
            Assert.Equal("ok", row.Status);
            Assert.Equal(48000, row.LatencyMs);
            Assert.Equal(5000, row.InputTokens);
            Assert.Equal(20000, row.OutputTokens);
            Assert.Equal(0.14, row.CostUsdEst);
        });
        Assert.All(critique, row =>
        {
            Assert.Equal("ok", row.Status);
            Assert.Equal(31000, row.LatencyMs);
            Assert.Null(row.InputTokens);
        });

        var words = await database.Words.OrderBy(word => word.Key).ToListAsync();
        Assert.All(words, word =>
        {
            Assert.Equal("pending_review", word.Status);
            Assert.Equal("council", word.Source);
            Assert.Equal(run.Id, word.CouncilRunId);
            Assert.Equal("council", word.ProvenanceMethod);
            Assert.Equal(run.RunId, word.ProvenanceRun);
            Assert.Equal(1, word.Version);
            Assert.Equal(64, word.ContentHash.Length);
            Assert.Equal(0, word.ReducedConfidence);
            Assert.Null(word.LintWarnings);
            Assert.Equal(run.CreatedAt, word.CreatedAt);
            Assert.NotNull(word.StatusChangedAt);
        });
        var patata = words.Single(word => word.Key == "es-patata-01");
        Assert.Equal("peninsular-generic", patata.RegionScope);
        Assert.Equal(0.67, patata.CouncilAgreement ?? 0, 2);
        Assert.Equal("media", patata.CouncilBand);
        Assert.Equal("patatas", patata.Plural);
        var nevera = words.Single(word => word.Key == "es-nevera-01");
        Assert.Equal("navarre", nevera.RegionScope);
        Assert.Equal("colloquial", nevera.Register);
        Assert.Equal(new[] { "food", "household" }, (await database.WordTopics.Select(topic => topic.Topic)
            .OrderBy(topic => topic).ToListAsync()).ToArray());

        var neveraSenses = await database.Senses.Where(sense => sense.WordId == nevera.Id)
            .OrderBy(sense => sense.SenseOrder).ToListAsync();
        Assert.Equal(2, neveraSenses.Count);
        Assert.Equal("fridge", neveraSenses[0].EnGloss);
        Assert.Equal("La leche está en la nevera.", neveraSenses[0].ExampleEs);
        // Defaults follow the corpus importer: gloss split on ", " and the lemma for Spanish answers.
        Assert.Equal(new[] { "fridge" }, JsonSerializer.Deserialize<string[]>(neveraSenses[0].EnAccepted));
        Assert.Equal(new[] { "nevera" }, JsonSerializer.Deserialize<string[]>(neveraSenses[0].EsAccepted));
        var patataSense = await database.Senses.SingleAsync(sense => sense.WordId == patata.Id);
        Assert.Equal(new[] { "potato", "spud" }, JsonSerializer.Deserialize<string[]>(patataSense.EnAccepted));
        Assert.Equal(new[] { "patata", "patatas" }, JsonSerializer.Deserialize<string[]>(patataSense.EsAccepted));

        // The revision snapshot is the reviewer gate's read model (corpus shape + status), not the
        // raw candidate line.
        var revisions = await database.WordRevisions.ToListAsync();
        Assert.Equal(2, revisions.Count);
        var snapshot = JsonNode.Parse(revisions.Single(row => row.WordId == patata.Id).SnapshotJson)!.AsObject();
        Assert.Equal("es-patata-01", snapshot["id"]!.GetValue<string>());
        Assert.Equal("patata", snapshot["headword"]!.GetValue<string>());
        Assert.Equal("noun", snapshot["partOfSpeech"]!.GetValue<string>());
        Assert.Equal("pending_review", snapshot["status"]!.GetValue<string>());
        Assert.Equal("peninsular-generic", snapshot["region"]!["scope"]!.GetValue<string>());
        Assert.Equal("potato", snapshot["senses"]![0]!["gloss"]!.GetValue<string>());
        Assert.Equal("¿Nos pones unas patatas bravas?", snapshot["senses"]![0]!["example"]!.GetValue<string>());
        Assert.Equal("council", snapshot["provenance"]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReimportingARecordedBundleWritesNothingAsync()
    {
        var bundle = CopyFixture("valid-propose");
        Assert.Equal(2, (await ImportAsync(bundle)).Created);
        var before = await RowCountsAsync();

        var second = await ImportAsync(bundle);

        Assert.Empty(second.Refusals);
        Assert.True(second.AlreadyImported);
        Assert.Equal(0, second.Created + second.Updated);
        Assert.Empty(second.Candidates);
        Assert.Equal(before, await RowCountsAsync());
    }

    [Fact]
    public async Task ProposeSkipsEveryExistingKeyWhateverItsStatusAsync()
    {
        var bundle = CopyFixture("valid-propose");
        // Third candidate so an approved, a rejected and a pending word are all skipped (Q-M3-6).
        var lines = ReadCandidates(bundle);
        var clone = lines[0].DeepClone();
        clone["key"] = "es-melocoton-01";
        clone["lemma"] = "melocotón";
        lines.Add(clone);
        WriteCandidates(bundle, lines);

        await DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None);
        await using (var database = _settings.CreateContext())
        {
            AddWord(database, "es-patata-01", "approved", 2);
            AddWord(database, "es-nevera-01", "rejected", 1);
            AddWord(database, "es-melocoton-01", "pending_review", 3);
            await database.SaveChangesAsync();
        }

        var result = await ImportAsync(bundle);

        Assert.Empty(result.Refusals);
        Assert.Equal(0, result.Created + result.Updated);
        Assert.Equal(3, result.SkippedExisting);
        Assert.All(result.Candidates, candidate => Assert.Equal(CouncilImportOutcomes.SkippedExisting, candidate.Outcome));
        await using var after = _settings.CreateContext();
        // The run is recorded so the bundle is never retried...
        Assert.Equal(1, await after.CouncilRuns.CountAsync());
        Assert.Empty(await after.WordProvenances.ToListAsync());
        // ...but no key moved: no new revisions, no version bump, no status change.
        Assert.Empty(await after.WordRevisions.ToListAsync());
        var words = await after.Words.OrderBy(word => word.Key).ToListAsync();
        Assert.Equal(new[] { "es-melocoton-01", "es-nevera-01", "es-patata-01" },
            words.Select(word => word.Key).ToArray());
        Assert.Equal(new[] { "pending_review", "rejected", "approved" },
            words.Select(word => word.Status).ToArray());
        Assert.Equal(new[] { 3, 1, 2 }, words.Select(word => word.Version).ToArray());
        Assert.All(words, word => Assert.Equal("manual", word.Source));
    }

    [Fact]
    public async Task HoldLandsAProposeBundleInDraftAsync()
    {
        var result = await ImportAsync(CopyFixture("valid-propose"), new CouncilImportOptions(Hold: true));

        Assert.Equal(2, result.Created);
        await using var database = _settings.CreateContext();
        Assert.All(await database.Words.ToListAsync(), word => Assert.Equal("draft", word.Status));
        Assert.All(await database.WordRevisions.ToListAsync(),
            revision => Assert.Equal("draft", JsonNode.Parse(revision.SnapshotJson)!["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ReworkBumpsFromCurrentVersionDemotesCardsAndKeepsSrsStateAsync()
    {
        await ImportAsync(CopyFixture("valid-propose"));
        await ApproveAsync("es-nevera-01");
        await AddCardAsync("es-nevera-01");

        var bundle = ReworkBundle("01JAB5R0NEWRUN000000000001", BundleAfterReview.ToString("O"), changeContent: true);
        var result = await ImportAsync(bundle);

        Assert.Empty(result.Refusals);
        Assert.Equal(1, result.Updated);
        await using var database = _settings.CreateContext();
        var word = await database.Words.SingleAsync(candidate => candidate.Key == "es-nevera-01");
        Assert.Equal(2, word.Version); // from the database's version, not from the bundle
        Assert.Equal("pending_review", word.Status);
        Assert.Equal(1, word.ApprovedVersion); // the last approval stays an audit fact
        var run = await database.CouncilRuns.SingleAsync(candidate => candidate.RunId == "01JAB5R0NEWRUN000000000001");
        Assert.Equal(run.Id, word.CouncilRunId);
        Assert.Equal("fridge (the appliance)",
            (await database.Senses.SingleAsync(sense => sense.WordId == word.Id && sense.SenseOrder == 0)).EnGloss);
        Assert.All(await database.WordAlternatives.Where(alternative => alternative.WordId == word.Id).ToListAsync(),
            alternative => Assert.Equal(2, alternative.WordVersion));
        var card = await database.Cards.SingleAsync();
        Assert.Equal(1, card.Epoch);
        Assert.Equal("word_not_approved", card.SuspendedReason);
        Assert.Equal(5.0, card.Stability ?? 0, 3);
        Assert.Equal(4.0, card.Difficulty ?? 0, 3);
        Assert.Equal(NowMs, card.DueMs);
        Assert.Equal(7, card.Reps);
        Assert.Equal(2, card.Lapses);
        // v1 import, v1 approve, v2 import: the old snapshot is never rewritten.
        Assert.Equal(new[] { 1, 1, 2 }, (await database.WordRevisions.Where(row => row.WordId == word.Id)
            .OrderBy(row => row.Id).ToListAsync()).Select(row => row.Version).ToArray());
        Assert.Equal(6, await database.WordProvenances.CountAsync(row =>
            row.WordId == word.Id && row.WordVersion == 2));
    }

    [Fact]
    public async Task UnchangedReworkKeepsTheReviewersEditAndVersionAsync()
    {
        await ImportAsync(CopyFixture("valid-propose"));
        await ApproveEditAsync("es-nevera-01", "refrigerator (the appliance)");
        await AddCardAsync("es-nevera-01");
        await using (var before = _settings.CreateContext())
        {
            var word = await before.Words.SingleAsync(candidate => candidate.Key == "es-nevera-01");
            Assert.Equal(2, word.Version);
            Assert.Equal("approved", word.Status);
        }

        // A fresh rework run with identical content: the run id is provenance, not source content.
        var bundle = ReworkBundle("01JAB5R0NEWRUN000000000002", BundleAfterReview.ToString("O"), changeContent: false);
        var result = await ImportAsync(bundle);

        Assert.Empty(result.Refusals);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, result.Updated);
        await using var database = _settings.CreateContext();
        var after = await database.Words.SingleAsync(candidate => candidate.Key == "es-nevera-01");
        Assert.Equal(2, after.Version);
        Assert.Equal("approved", after.Status);
        Assert.Equal("refrigerator (the appliance)",
            (await database.Senses.SingleAsync(sense => sense.WordId == after.Id && sense.SenseOrder == 0)).EnGloss);
        Assert.Equal(3, await database.WordRevisions.CountAsync(row => row.WordId == after.Id));
        Assert.Equal(0, (await database.Cards.SingleAsync()).Epoch);
        // The run itself is still recorded so the bundle is not retried.
        Assert.True(await database.CouncilRuns.AnyAsync(run => run.RunId == "01JAB5R0NEWRUN000000000002"));
    }

    [Fact]
    public async Task ReworkOlderThanTheReviewersEditIsRefusedUnlessForcedAsync()
    {
        await ImportAsync(CopyFixture("valid-propose"));
        await ApproveEditAsync("es-nevera-01", "refrigerator (the appliance)");
        var bundle = ReworkBundle("01JAB5R0NEWRUN000000000003", BundleBeforeReview.ToString("O"), changeContent: true);

        var refused = await ImportAsync(bundle);

        Assert.NotEmpty(refused.Refusals);
        Assert.Contains(refused.Refusals, line => line.Contains("older than the newest reviewer revision", StringComparison.Ordinal));
        // The flag is bundle-wide; the diagnostic must say so, not imply one stale key is skipped.
        Assert.Contains(refused.Refusals, line => line.Contains("--force is bundle-wide", StringComparison.Ordinal));
        Assert.Equal(1, refused.SkippedReviewerEditNewer);
        Assert.Equal(0, refused.Created + refused.Updated);
        await using (var database = _settings.CreateContext())
        {
            var word = await database.Words.SingleAsync(candidate => candidate.Key == "es-nevera-01");
            Assert.Equal(2, word.Version);
            Assert.Equal("approved", word.Status);
            Assert.Equal("refrigerator (the appliance)",
                (await database.Senses.SingleAsync(sense => sense.WordId == word.Id && sense.SenseOrder == 0)).EnGloss);
            Assert.Equal(3, await database.WordRevisions.CountAsync(row => row.WordId == word.Id));
            Assert.False(await database.CouncilRuns.AnyAsync(run => run.RunId == "01JAB5R0NEWRUN000000000003"));
        }

        var forced = await ImportAsync(bundle, new CouncilImportOptions(Force: true));

        Assert.Empty(forced.Refusals);
        Assert.Equal(1, forced.Updated);
        await using var after = _settings.CreateContext();
        var forcedWord = await after.Words.SingleAsync(candidate => candidate.Key == "es-nevera-01");
        Assert.Equal(3, forcedWord.Version);
        Assert.Equal("pending_review", forcedWord.Status);
        // The previous snapshot is still readable at its own version.
        var versionTwo = await after.WordRevisions.FirstAsync(row => row.WordId == forcedWord.Id
            && row.Version == 2 && row.Action == "edit");
        Assert.Equal("refrigerator (the appliance)",
            JsonNode.Parse(versionTwo.SnapshotJson)!["senses"]![0]!["gloss"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProvenanceRowsMatchTheModelsTheCandidateActuallyHadAsync()
    {
        await ImportAsync(CopyFixture("valid-propose"));

        await using var database = _settings.CreateContext();
        var run = await database.CouncilRuns.SingleAsync();
        var patata = await database.Words.SingleAsync(word => word.Key == "es-patata-01");
        var rows = await database.WordProvenances.Where(row => row.WordId == patata.Id)
            .OrderBy(row => row.Role).ThenBy(row => row.ModelId).ToListAsync();
        // es-patata's alternatives name all three models, but only two proposed the winning
        // candidate: alternatives are disagreements, so the dissenter must not get a `proposed` row.
        Assert.Equal(5, rows.Count);
        Assert.Equal(new[] { "anthropic:claude-sonnet-5-5", "openai:gpt-6-mini" },
            rows.Where(row => row.Role == "proposed").Select(row => row.ModelId).ToArray());
        Assert.DoesNotContain(rows, row => row.Role == "proposed" && row.ModelId == "google:gemini-3-flash");
        Assert.Equal(new[] { "critique_issue", "critique_ok", "critique_ok" },
            rows.Where(row => row.Role.StartsWith("critique", StringComparison.Ordinal)).Select(row => row.Role).ToArray());
        var issue = rows.Single(row => row.Role == "critique_issue");
        Assert.Equal("google:gemini-3-flash", issue.ModelId);
        Assert.Equal(new[] { "register" }, JsonSerializer.Deserialize<string[]>(issue.Issues!));
        Assert.All(rows, row =>
        {
            Assert.Equal(1, row.WordVersion);
            Assert.Equal(run.Id, row.CouncilRunId);
        });

        var nevera = await database.Words.SingleAsync(word => word.Key == "es-nevera-01");
        var neveraRows = await database.WordProvenances.Where(row => row.WordId == nevera.Id).ToListAsync();
        // All three models proposed this candidate (proposal_support 1), so all three get a row.
        Assert.Equal(6, neveraRows.Count);
        Assert.Equal(3, neveraRows.Count(row => row.Role == "proposed"));
        Assert.All(neveraRows.Where(row => row.Role != "proposed"), row => Assert.Null(row.Issues));
        Assert.DoesNotContain(neveraRows, row => row.ModelId == "mistral:ghost-1");
    }

    [Fact]
    public async Task BundleWithoutProposedByIsRefusedBeforeAnyWriteAsync()
    {
        // An older bundle must fail loudly: inferring proposers from alternatives would name the
        // wrong models (and name none when a candidate carries no alternatives), so the importer
        // refuses rather than inventing provenance.
        var result = await ImportAsync(Path.Combine(FixtureRoot(), "missing-proposed-by"));

        Assert.Contains(result.Refusals,
            line => line.Contains("council.proposed_by is missing or empty", StringComparison.Ordinal));
        Assert.Equal(0, result.Created + result.Updated);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task AlternativesCarryFieldSupportModelsRegionAndWordVersionAsync()
    {
        await ImportAsync(CopyFixture("valid-propose"));
        await using var database = _settings.CreateContext();
        var patata = await database.Words.SingleAsync(word => word.Key == "es-patata-01");
        var alternatives = await database.WordAlternatives.Where(alternative => alternative.WordId == patata.Id)
            .OrderBy(alternative => alternative.Rank).ToListAsync();

        // Rank per plan §2.5: sense, support desc, field; every row is a chip target, so kind = field.
        Assert.Equal(new[] { "example", "gloss", "gloss", "lemma", "regional_variant" },
            alternatives.Select(alternative => alternative.Field).ToArray());
        Assert.All(alternatives, alternative =>
        {
            Assert.Equal(alternative.Field, alternative.Kind);
            Assert.Equal(1, alternative.WordVersion);
            Assert.Equal("council", alternative.Source);
            Assert.False(string.IsNullOrWhiteSpace(alternative.Reason));
        });
        Assert.Equal(new[] { 2, 2, 1, 0, 0 }, alternatives.Select(alternative => alternative.Support).ToArray());
        Assert.Equal(new[] { "Compro patatas en el mercado.", "potato (the vegetable)",
                "potatoes (as a bar dish)", "papa", "patatilla" },
            alternatives.Select(alternative => alternative.Value).ToArray());
        Assert.Equal("I buy potatoes at the market.", alternatives[0].Gloss);
        // word_alternatives.example is the Spanish sentence (word.schema.json); the chip translation
        // rides on Gloss, which is what the reviewer sees as `translation`.
        Assert.Equal("Compro patatas en el mercado.", alternatives[0].Example);
        Assert.Equal("Propuesta del consejo", alternatives[0].Reason);
        Assert.Null(alternatives[1].Gloss);
        Assert.Equal("ES-NA", alternatives[4].Region);
        Assert.Equal(new[] { "openai:gpt-6-mini", "google:gemini-3-flash" },
            JsonSerializer.Deserialize<string[]>(alternatives[0].Models!));
        Assert.Equal("Latin-American form, never taught as Peninsular Spanish", alternatives[3].Reason);
        Assert.Equal(new[] { "anthropic:claude-sonnet-5-5", "openai:gpt-6-mini" },
            JsonSerializer.Deserialize<string[]>(alternatives[1].Models!));
        Assert.Equal(new[] { "google:gemini-3-flash" },
            JsonSerializer.Deserialize<string[]>(alternatives[2].Models!));

        // The register variant of the second word keeps its own narrower vocabulary.
        var nevera = await database.Words.SingleAsync(word => word.Key == "es-nevera-01");
        var register = await database.WordAlternatives.SingleAsync(
            alternative => alternative.WordId == nevera.Id && alternative.Field == "register_variant");
        Assert.Equal("frigorífico", register.Value);
        Assert.Equal("formal", register.Register);
        Assert.Null(register.Region);
    }

    [Fact]
    public async Task ExampleAlternativeStoresItsSpanishSentenceAndRegionalNoteIsStoredWithoutAChipTargetAsync()
    {
        var bundle = CopyFixture("valid-propose");
        var lines = ReadCandidates(bundle);
        lines[0]["alternatives"]!.AsArray().Add(JsonNode.Parse("""
            {"field":"regional_note","sense":0,"es":"En Navarra se dice también patatilla.","support":1,
             "models":["google:gemini-3-flash"],"rationale":"regional note the sheet has no tab for"}
            """));
        WriteCandidates(bundle, lines);

        Assert.Empty((await ImportAsync(bundle)).Refusals);

        await using var database = _settings.CreateContext();
        var patata = await database.Words.SingleAsync(word => word.Key == "es-patata-01");
        // word_alternatives.example means the Spanish sentence (word.schema.json and
        // ContentImporter agree); the English translation is the chip's `gloss`/translation.
        var example = Assert.Single(await database.WordAlternatives
            .Where(alternative => alternative.WordId == patata.Id && alternative.Field == "example")
            .ToListAsync());
        Assert.Equal("Compro patatas en el mercado.", example.Value);
        Assert.Equal("Compro patatas en el mercado.", example.Example);
        Assert.Equal("I buy potatoes at the market.", example.Gloss);
        // The regional note is stored for the audit trail; ReviewTransitions has no chip/swap target
        // for it, which M4ReviewCouncilChipsTests pins. Storage is the decision, not an omission.
        var note = Assert.Single(await database.WordAlternatives
            .Where(alternative => alternative.WordId == patata.Id && alternative.Field == "regional_note")
            .ToListAsync());
        Assert.Equal("En Navarra se dice también patatilla.", note.Value);
        Assert.Null(note.Example);
    }

    [Fact]
    public async Task InvalidCandidateRefusesTheWholeBundleBeforeAnyWriteAsync()
    {
        var bundle = CopyFixture("valid-propose");
        var lines = ReadCandidates(bundle);
        lines[0]["alternatives"]![0]!["support"] = 7; // no longer models.length
        WriteCandidates(bundle, lines);

        var result = await ImportAsync(bundle);

        Assert.NotEmpty(result.Refusals);
        Assert.Contains(result.Refusals, line => line.Contains("support is 7", StringComparison.Ordinal));
        Assert.Equal(0, result.Created + result.Updated);
        // Validation runs before the database is touched at all.
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task AlternativeFieldOutsideTheColumnCheckIsRefusedBeforeAnySqlAsync()
    {
        // The only way a CHECK can fire mid-transaction is a mapping bug: every DB CHECK vocabulary
        // is mirrored by the schema, so a bundle outside it is refused at the gate, before any SQL.
        var result = await ImportAsync(CopyFixture("invalid-alternative-field"));

        Assert.NotEmpty(result.Refusals);
        Assert.Contains(result.Refusals,
            line => line.Contains("alternatives/0/field", StringComparison.Ordinal));
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task TargetKeyResolvesAgainstTheWholeWordsTableAsync()
    {
        var bundle = CopyFixture("valid-propose");
        var lines = ReadCandidates(bundle);
        lines[0]["alternatives"]![0]!["target_key"] = "es-inexistente-99";
        WriteCandidates(bundle, lines);

        var refused = await ImportAsync(bundle);

        Assert.Contains(refused.Refusals,
            line => line.Contains("does not resolve to a word in this bundle or the known lexicon", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Refusals,
            line => line.Contains("no known-key set was supplied", StringComparison.Ordinal));

        // Every status counts as known: a rejected word is still a word the undo path can follow.
        await DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None);
        await using (var database = _settings.CreateContext())
        {
            AddWord(database, "es-inexistente-99", "rejected", 1);
            await database.SaveChangesAsync();
        }

        var accepted = await ImportAsync(bundle);

        Assert.Empty(accepted.Refusals);
        Assert.Equal(2, accepted.Created);
    }

    [Fact]
    public async Task ReworkOfAWordThatDoesNotExistIsRefusedAsync()
    {
        var result = await ImportAsync(CopyFixture("valid-rework"));

        Assert.Contains(result.Refusals,
            line => line.Contains("does not exist in the database", StringComparison.Ordinal));
        await using var database = _settings.CreateContext();
        Assert.Empty(await database.Words.ToListAsync());
        Assert.Empty(await database.CouncilRuns.ToListAsync());
    }

    [Fact]
    public async Task ProposeOnlyRefusesAReworkBundleBeforeAnyWriteAsync()
    {
        var bundle = ReworkBundle("01JAB5R0NEWRUN000000000004", BundleAfterReview.ToString("O"), changeContent: true);

        var result = await ImportAsync(bundle, new CouncilImportOptions(ProposeOnly: true));

        Assert.Contains(result.Refusals,
            line => line.Contains("startup sync imports propose bundles only", StringComparison.Ordinal));
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task DryRunPredictsCountsWithoutCreatingADatabaseAsync()
    {
        var result = await ImportAsync(CopyFixture("valid-propose"), new CouncilImportOptions(DryRun: true));

        Assert.Empty(result.Refusals);
        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public async Task OrderOnlyChangeInProposedByIsNotAContentChangeAsync()
    {
        Assert.Equal(2, (await ImportAsync(CopyFixture("valid-propose"))).Created);
        var first = ReworkBundle("01JAB5R0NEWRUN000000000005", BundleAfterReview.ToString("O"), changeContent: true);
        var updated = await ImportAsync(first);
        Assert.Equal(1, updated.Updated);
        int revisions;
        await using (var database = _settings.CreateContext())
        {
            revisions = await database.WordRevisions.CountAsync();
        }

        // Same content, same roster, only the order of the proposer set differs. CanonicalJson keeps
        // array order, so the importer sorts the set-valued lists before hashing; otherwise this
        // rework would bump and demote an approved word for no content change.
        var second = ReworkBundle("01JAB5R0NEWRUN000000000006", BundleAfterReview.ToString("O"), changeContent: true);
        var lines = ReadCandidates(second);
        foreach (var line in lines)
        {
            var council = line["council"]!.AsObject();
            var proposed = council["proposed_by"]!.AsArray().Select(model => model!.GetValue<string>()).Reverse();
            council["proposed_by"] = new JsonArray(proposed.Select(model => (JsonNode?)JsonValue.Create(model)).ToArray());
        }
        WriteCandidates(second, lines);

        var unchanged = await ImportAsync(second);

        Assert.Empty(unchanged.Refusals);
        Assert.Equal(1, unchanged.Unchanged);
        Assert.Equal(0, unchanged.Updated);
        await using var after = _settings.CreateContext();
        var nevera = await after.Words.SingleAsync(word => word.Key == "es-nevera-01");
        Assert.Equal(2, nevera.Version);
        Assert.Equal(revisions, await after.WordRevisions.CountAsync());
    }

    // Pins the reason the importer normalises set-valued arrays: the shared canonicaliser does not.
    [Fact]
    public void CanonicalJsonPreservesArrayOrderSoSetValuedListsMustBeNormalisedBeforeHashing()
    {
        var first = JsonNode.Parse("""{"proposed_by":["anthropic:one","openai:two"]}""")!;
        var second = JsonNode.Parse("""{"proposed_by":["openai:two","anthropic:one"]}""")!;

        Assert.NotEqual(CanonicalJson.Sha256Hex(CanonicalJson.ContentBytes(first)),
            CanonicalJson.Sha256Hex(CanonicalJson.ContentBytes(second)));
    }

    // The retired ContentImporter canonicaliser (deleted in C3) kept as a frozen reference: if
    // CanonicalJson ever stops producing the same bytes, every stored content_hash would change on
    // the next corpus import.
    [Fact]
    public void ContentBytesReproduceTheRetiredContentImporterCanonicaliser()
    {
        var files = M2ContentImportTests.CorpusFiles();
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            var legacy = Convert.ToHexStringLower(SHA256.HashData(LegacyCanonicalBytes(root)));
            Assert.Equal(legacy, CanonicalJson.Sha256Hex(CanonicalJson.ContentBytes(root)));
        }
    }

    private static byte[] LegacyCanonicalBytes(JsonNode root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteLegacyCanonical(writer, root, true);
        }
        return stream.ToArray();
    }

    private static void WriteLegacyCanonical(Utf8JsonWriter writer, JsonNode? node, bool isRoot = false)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var property in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                if (isRoot && property.Key is "status" or "review" or "updatedAt")
                {
                    continue;
                }
                writer.WritePropertyName(property.Key);
                WriteLegacyCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (var item in array)
            {
                WriteLegacyCanonical(writer, item);
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

    // -- helpers ---------------------------------------------------------------------------------

    private Task<CouncilBundleImportResult> ImportAsync(string bundle, CouncilImportOptions? options = null) =>
        new CouncilImporter(_settings).ImportAsync(bundle, options ?? new CouncilImportOptions(), CancellationToken.None);

    private sealed record RowCounts(int Words, int Revisions, int Alternatives, int Provenance, int Runs,
        int ModelResults, int Cards);

    private async Task<RowCounts> RowCountsAsync()
    {
        await using var database = _settings.CreateContext();
        return new RowCounts(await database.Words.CountAsync(), await database.WordRevisions.CountAsync(),
            await database.WordAlternatives.CountAsync(), await database.WordProvenances.CountAsync(),
            await database.CouncilRuns.CountAsync(), await database.CouncilModelResults.CountAsync(),
            await database.Cards.CountAsync());
    }

    private async Task ApproveAsync(string key)
    {
        await using var database = _settings.CreateContext();
        var word = await database.Words.SingleAsync(candidate => candidate.Key == key);
        var outcome = await ReviewTransitions.ApplyAsync(database, word,
            new ReviewTransitionRequest(word.Version, ReviewActionNames.Approve, null, null, null, null),
            ReviewTransitions.ReviewerAllowedFrom(ReviewActionNames.Approve), actorUserId: null, nowMs: NowMs,
            applyCardEffects: false, CancellationToken.None);
        Assert.Equal(ReviewResults.Applied, outcome.Result);
    }

    private async Task ApproveEditAsync(string key, string gloss)
    {
        await using var database = _settings.CreateContext();
        var word = await database.Words.SingleAsync(candidate => candidate.Key == key);
        var outcome = await ReviewTransitions.ApplyAsync(database, word,
            new ReviewTransitionRequest(word.Version, ReviewActionNames.ApproveEdit, null, 0, null,
                new[] { new ReviewEditRequest(ReviewFieldNames.Gloss, 0, gloss) }),
            ReviewTransitions.ReviewerAllowedFrom(ReviewActionNames.ApproveEdit), actorUserId: null, nowMs: NowMs,
            applyCardEffects: false, CancellationToken.None);
        Assert.Equal(ReviewResults.Applied, outcome.Result);
    }

    private async Task AddCardAsync(string key)
    {
        await using var database = _settings.CreateContext();
        var word = await database.Words.SingleAsync(candidate => candidate.Key == key);
        var sense = await database.Senses.OrderBy(sense => sense.SenseOrder)
            .FirstAsync(sense => sense.WordId == word.Id);
        database.Users.Add(new User
        {
            Id = 1, Username = "learner", Role = "owner", PasswordHash = "x", CreatedAt = Now, UpdatedAt = Now
        });
        database.Cards.Add(new Card
        {
            UserId = 1,
            SenseId = sense.Id,
            Direction = "es_en",
            State = Palabras.Srs.CardState.Review,
            Stability = 5.0,
            Difficulty = 4.0,
            DueMs = NowMs,
            Reps = 7,
            Lapses = 2,
            CreatedAt = NowMs
        });
        await database.SaveChangesAsync();
    }

    private static void AddWord(PalabrasDbContext database, string key, string status, int version)
    {
        database.Words.Add(new Word
        {
            Key = key,
            Headword = key,
            Pos = "noun",
            Cefr = "A1",
            RegionScope = "peninsular-generic",
            Register = "neutral",
            Status = status,
            Version = version,
            ContentHash = "seeded-" + key,
            ProvenanceMethod = "hand-authored",
            CreatedAt = 1,
            UpdatedAt = 1
        });
    }

    private string ReworkBundle(string runId, string createdAt, bool changeContent)
    {
        var bundle = CopyFixture("valid-rework");
        EditManifest(bundle, manifest =>
        {
            manifest["run_id"] = runId;
            manifest["created_at"] = createdAt;
        });
        var lines = ReadCandidates(bundle);
        foreach (var line in lines)
        {
            line["council"]!["run_id"] = runId;
        }
        if (changeContent)
        {
            lines[0]["senses"]![0]!["en_gloss"] = "fridge (the appliance)";
        }
        WriteCandidates(bundle, lines);
        return bundle;
    }

    private static List<JsonNode> ReadCandidates(string bundle) => File.ReadAllLines(Path.Combine(bundle, "candidates.jsonl"))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonNode.Parse(line)!)
        .ToList();

    private static void WriteCandidates(string bundle, List<JsonNode> lines)
    {
        var text = string.Join('\n', lines.Select(line => line.ToJsonString())) + "\n";
        File.WriteAllText(Path.Combine(bundle, "candidates.jsonl"), text, new UTF8Encoding(false));
        EditManifest(bundle, manifest =>
        {
            manifest["counts"]!["candidates"] = lines.Count;
            manifest["candidates_sha256"] = CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(text));
        });
    }

    private static JsonObject ReadManifest(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void EditManifest(string bundle, Action<JsonObject> edit)
    {
        var path = Path.Combine(bundle, "manifest.json");
        var manifest = ReadManifest(path);
        edit(manifest);
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private string CopyFixture(string name)
    {
        var target = Path.Combine(_root, "bundles", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(Path.Combine(FixtureRoot(), name)))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return target;
    }

    private static string FixtureRoot()
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
