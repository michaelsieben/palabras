using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content.Council;

// Turns one validated bundle directory into council_runs, council_model_results, words (+senses,
// alternatives, topics, revisions) and word_provenance, in ONE immediate transaction.
//
// Deliberate properties:
// - The known-word lexicon is loaded here, not accepted as a parameter. A caller therefore cannot
//   validate a target_key against an empty set by accident; the set is every words.key, all statuses.
// - Every rejection is collected and reported per candidate line; only a database failure throws.
// - Re-importing a recorded run id is a strict no-op; a bundle that would move approved content
//   against a newer reviewer verdict is refused whole, never partially.
// - Nothing is shared with ContentImporter except the canonical hasher and ContentCardLifecycle: the
//   two importers must not grow the same special cases and drift apart.
internal sealed class CouncilImporter
{
    // Plan §2.5: a bundle alternative carries no word note, so the chip reason falls back to this
    // fixed sentence (the content schema requires a reason of at least 8 characters).
    private const string AlternativeReasonFallback = "Propuesta del consejo";

    private readonly DatabaseSettings _settings;

    internal CouncilImporter(DatabaseSettings settings)
    {
        _settings = settings;
    }

    internal async Task<CouncilBundleImportResult> ImportAsync(string bundleDirectory,
        CouncilImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(bundleDirectory))
        {
            throw new ArgumentException("Bundle directory does not exist: " + bundleDirectory, nameof(bundleDirectory));
        }
        var knownWordKeys = await LoadKnownWordKeysAsync(cancellationToken);
        var validation = await CouncilBundleValidator.ValidateAsync(bundleDirectory, knownWordKeys, cancellationToken);
        if (!validation.IsValid || validation.Manifest is null)
        {
            return Refused(validation.Manifest,
                validation.Issues.Select(issue => issue.Location + ": " + issue.Message).ToArray());
        }
        var manifest = validation.Manifest;

        // Q-M3-7: the startup sync may only add new words. A rework reaches the database solely
        // through the explicit CLI verb, so the importer enforces the restriction, not the caller.
        if (options.ProposeOnly && manifest.Mode != "propose")
        {
            return Refused(manifest,
                [$"manifest.json: mode is '{manifest.Mode}'; the startup sync imports propose bundles only (run import-council for a rework)"]);
        }

        if (options.DryRun)
        {
            return await PredictAsync(manifest, validation.Candidates, options, cancellationToken);
        }

        await DatabaseLifecycle.InitializeAsync(_settings, cancellationToken);
        await using var database = _settings.CreateContext();
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)database.Database.GetDbConnection();
        // IMMEDIATE: the run-id check and every write share one write lock, so a startup sync and an
        // operator CLI cannot both import the same bundle. A refusal leaves the transaction uncommitted.
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await database.Database.UseTransactionAsync(transaction, cancellationToken);

        if (await database.CouncilRuns.AnyAsync(run => run.RunId == manifest.RunId, cancellationToken))
        {
            return new(manifest.RunId, manifest.Mode, [], AlreadyImported: true, 0, 0, 0, 0, 0, []);
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var decisions = await DecideAsync(database, manifest, validation.Candidates, options, cancellationToken);
        var blocking = decisions.Where(decision => BlocksBundle(decision.Outcome)).ToArray();
        if (blocking.Length > 0)
        {
            // Nothing has been written yet; the whole bundle is refused so a run id is never recorded
            // for a partially applied rework (T-10, T-16).
            return new(manifest.RunId, manifest.Mode, blocking.Select(RefusalFor).ToArray(), AlreadyImported: false,
                0, 0, 0, 0, blocking.Count(decision => decision.Outcome == CouncilImportOutcomes.SkippedReviewerEditNewer),
                decisions.Select(Outcome).ToArray());
        }

        var run = await AddRunAsync(database, manifest, validation.Candidates.Count, nowMs, cancellationToken);
        var status = manifest.Mode == "propose" && options.Hold ? "draft" : "pending_review";
        var created = 0;
        var updated = 0;
        foreach (var decision in decisions)
        {
            if (decision.Outcome is CouncilImportOutcomes.Created or CouncilImportOutcomes.Updated)
            {
                await PersistCandidateAsync(database, run, manifest, decision, status, nowMs, cancellationToken);
                if (decision.Outcome == CouncilImportOutcomes.Created)
                {
                    created++;
                }
                else
                {
                    updated++;
                }
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(manifest.RunId, manifest.Mode, [], AlreadyImported: false, created, updated,
            decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.Unchanged),
            decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.SkippedExisting),
            decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.SkippedReviewerEditNewer),
            decisions.Select(Outcome).ToArray());
    }

    // -- read side -------------------------------------------------------------------------------

    // The lexicon a target_key may resolve against. A missing database file means a fresh install, so
    // the set is genuinely empty; when the file exists the set is the whole words.key column.
    private async Task<IReadOnlySet<string>> LoadKnownWordKeysAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.FilePath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        await using var database = _settings.CreateContext(SqliteOpenMode.ReadOnly);
        if (!await HasTableAsync(database, "words", cancellationToken))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var keys = await database.Words.AsNoTracking().Select(word => word.Key).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> HasTableAsync(PalabrasDbContext database, string table,
        CancellationToken cancellationToken) =>
        await database.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name={0}", table)
            .SingleAsync(cancellationToken) > 0;

    // -- decisions -------------------------------------------------------------------------------

    private sealed record CandidateDecision(CouncilBundleCandidate Entry, Word? Word, string ContentHash, string Outcome);

    private static bool BlocksBundle(string outcome) =>
        outcome is CouncilImportOutcomes.MissingWord or CouncilImportOutcomes.SkippedReviewerEditNewer;

    private static CouncilCandidateOutcome Outcome(CandidateDecision decision) =>
        new(decision.Entry.Candidate.Key, decision.Entry.Line, decision.Outcome);

    private static string RefusalFor(CandidateDecision decision) => decision.Outcome switch
    {
        CouncilImportOutcomes.MissingWord =>
            $"candidates.jsonl:{decision.Entry.Line}: rework candidate '{decision.Entry.Candidate.Key}' does not exist in the database; rework may only version an existing word",
        // --force is bundle-wide, not per candidate: say so here, because the reviewer is about to
        // override the guard for every stale key in this bundle at once.
        _ =>
            $"candidates.jsonl:{decision.Entry.Line}: rework candidate '{decision.Entry.Candidate.Key}' is older than the newest reviewer revision; pass --force to override. --force is bundle-wide: it imports every rework candidate in this bundle, including ones you did not mean to override"
    };

    // A null database means "no words and no runs exist yet" (a dry run before the first start), so
    // the same decision code predicts the real outcome instead of a second approximation.
    private static async Task<List<CandidateDecision>> DecideAsync(PalabrasDbContext? database,
        CouncilManifest manifest, IReadOnlyList<CouncilBundleCandidate> candidates, CouncilImportOptions options,
        CancellationToken cancellationToken)
    {
        var keys = candidates.Select(entry => entry.Candidate.Key).ToArray();
        var words = new Dictionary<string, Word>(StringComparer.Ordinal);
        var reviewerEdits = new Dictionary<int, long>();
        if (database is not null)
        {
            words = await database.Words.Where(word => keys.Contains(word.Key))
                .ToDictionaryAsync(word => word.Key, StringComparer.Ordinal, cancellationToken);
            var wordIds = words.Values.Select(word => word.Id).ToArray();
            if (wordIds.Length > 0)
            {
                // Plan §3.3(4): a reviewer's newest edit/approval outranks an older bundle.
                reviewerEdits = await database.WordRevisions.AsNoTracking()
                    .Where(revision => wordIds.Contains(revision.WordId)
                        && (revision.Action == "edit" || revision.Action == "approve"))
                    .GroupBy(revision => revision.WordId)
                    .Select(group => new { WordId = group.Key, CreatedAt = group.Max(revision => revision.CreatedAt) })
                    .ToDictionaryAsync(row => row.WordId, row => row.CreatedAt, cancellationToken);
            }
        }
        var manifestCreatedAt = manifest.CreatedAtUnixMilliseconds;
        var result = new List<CandidateDecision>(candidates.Count);
        foreach (var entry in candidates)
        {
            var candidate = entry.Candidate;
            var hash = ContentHash(entry.Raw);
            if (!words.TryGetValue(candidate.Key, out var word))
            {
                result.Add(new(entry, null, hash,
                    manifest.Mode == "propose" ? CouncilImportOutcomes.Created : CouncilImportOutcomes.MissingWord));
                continue;
            }
            if (manifest.Mode == "propose")
            {
                // Q-M3-6: every existing key is skipped, whatever its status. The run is still
                // recorded so the bundle is never retried.
                result.Add(new(entry, word, hash, CouncilImportOutcomes.SkippedExisting));
                continue;
            }
            if (string.Equals(word.ContentHash, hash, StringComparison.Ordinal))
            {
                // Contract §3.3(1): the source did not change, so her edit, verdict, version and card
                // epochs stay exactly as they are.
                result.Add(new(entry, word, hash, CouncilImportOutcomes.Unchanged));
                continue;
            }
            if (!options.Force && reviewerEdits.TryGetValue(word.Id, out var editedAt) && editedAt > manifestCreatedAt)
            {
                result.Add(new(entry, word, hash, CouncilImportOutcomes.SkippedReviewerEditNewer));
                continue;
            }
            result.Add(new(entry, word, hash, CouncilImportOutcomes.Updated));
        }
        return result;
    }

    // Run identity is provenance, not source content: a rework run always carries a new run_id, so
    // hashing it would make the §3.3(1) "same source" case unreachable and bump (then demote) an
    // approved word on every re-run that produced identical content.
    //
    // `proposed_by` is a set (the schema makes it unique) and `models` on an alternative is one too;
    // CanonicalJson preserves array order, so sorting them here keeps an order-only permutation of
    // the same roster from faking a content change and demoting an approved word.
    private static string ContentHash(JsonNode raw)
    {
        var content = raw.DeepClone();
        if (content["council"] is JsonObject council)
        {
            council.Remove("run_id");
            NormalizeSet(council["proposed_by"]);
        }
        if (content["alternatives"] is JsonArray alternatives)
        {
            foreach (var alternative in alternatives)
            {
                if (alternative is JsonObject alternativeObject)
                {
                    NormalizeSet(alternativeObject["models"]);
                }
            }
        }
        return CanonicalJson.Sha256Hex(CanonicalJson.ContentBytes(content));
    }

    private static void NormalizeSet(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count < 2)
        {
            return;
        }
        var values = array.Select(item => item?.GetValue<string>()
                ?? throw new InvalidOperationException("A council model list may only contain model ids."))
            .Order(StringComparer.Ordinal)
            .ToArray();
        array.Clear();
        foreach (var value in values)
        {
            array.Add(value);
        }
    }

    // -- dry run ---------------------------------------------------------------------------------

    private async Task<CouncilBundleImportResult> PredictAsync(CouncilManifest manifest,
        IReadOnlyList<CouncilBundleCandidate> candidates, CouncilImportOptions options,
        CancellationToken cancellationToken)
    {
        PalabrasDbContext? database = null;
        try
        {
            if (File.Exists(_settings.FilePath))
            {
                database = _settings.CreateContext(SqliteOpenMode.ReadOnly);
                if (!await HasTableAsync(database, "council_runs", cancellationToken))
                {
                    await database.DisposeAsync();
                    database = null;
                }
            }
            if (database is not null
                && await database.CouncilRuns.AnyAsync(run => run.RunId == manifest.RunId, cancellationToken))
            {
                return new(manifest.RunId, manifest.Mode, [], AlreadyImported: true, 0, 0, 0, 0, 0, []);
            }
            var decisions = await DecideAsync(database, manifest, candidates, options, cancellationToken);
            var blocking = decisions.Where(decision => BlocksBundle(decision.Outcome)).ToArray();
            if (blocking.Length > 0)
            {
                return new(manifest.RunId, manifest.Mode, blocking.Select(RefusalFor).ToArray(), AlreadyImported: false,
                    0, 0, 0, 0,
                    blocking.Count(decision => decision.Outcome == CouncilImportOutcomes.SkippedReviewerEditNewer),
                    decisions.Select(Outcome).ToArray());
            }
            return new(manifest.RunId, manifest.Mode, [], AlreadyImported: false,
                decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.Created),
                decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.Updated),
                decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.Unchanged),
                decisions.Count(decision => decision.Outcome == CouncilImportOutcomes.SkippedExisting),
                0,
                decisions.Select(Outcome).ToArray());
        }
        finally
        {
            if (database is not null)
            {
                await database.DisposeAsync();
            }
        }
    }

    // -- write side ------------------------------------------------------------------------------

    private static async Task<CouncilRun> AddRunAsync(PalabrasDbContext database, CouncilManifest manifest,
        int candidatesCount, long nowMs, CancellationToken cancellationToken)
    {
        var run = new CouncilRun
        {
            RunId = manifest.RunId,
            Mode = manifest.Mode,
            PromptVersions = JsonSerializer.Serialize(manifest.PromptVersion),
            StyleGuideSha256 = manifest.StyleGuideSha256,
            Topic = manifest.Request.Topic,
            Cefr = manifest.Request.Cefr,
            RequestedCount = manifest.Request.Count,
            ModelsRequested = manifest.ModelsRequested,
            ModelsSucceededPropose = manifest.ModelsSucceeded.Propose,
            ModelsSucceededCritique = manifest.ModelsSucceeded.Critique,
            ReducedConfidence = manifest.ReducedConfidence ? 1 : 0,
            CandidatesCount = candidatesCount,
            DroppedCount = manifest.Counts.Dropped,
            CandidatesSha256 = manifest.CandidatesSha256!,
            CreatedAt = manifest.CreatedAtUnixMilliseconds,
            ImportedAt = nowMs
        };
        database.CouncilRuns.Add(run);
        // The surrogate id is needed by the model results and every candidate write below.
        await database.SaveChangesAsync(cancellationToken);
        foreach (var model in manifest.Models)
        {
            // Tokens and cost are per model, not per phase (manifest §models), so they ride on the
            // propose row only: duplicating them on critique would double-count the run.
            database.CouncilModelResults.Add(new CouncilModelResult
            {
                CouncilRunId = run.Id,
                ModelId = model.Id,
                Phase = "propose",
                Status = model.Phases.Propose,
                LatencyMs = model.LatencyMs?.Propose,
                InputTokens = model.Tokens?.In,
                OutputTokens = model.Tokens?.Out,
                CostUsdEst = model.CostUsdEst,
                InvalidCount = model.InvalidCount,
                Error = model.Phases.Propose == "ok" ? null : model.Error
            });
            database.CouncilModelResults.Add(new CouncilModelResult
            {
                CouncilRunId = run.Id,
                ModelId = model.Id,
                Phase = "critique",
                Status = model.Phases.Critique,
                LatencyMs = model.LatencyMs?.Critique,
                Error = model.Phases.Critique == "ok" ? null : model.Error
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return run;
    }

    private static async Task PersistCandidateAsync(PalabrasDbContext database, CouncilRun run,
        CouncilManifest manifest, CandidateDecision decision, string status, long nowMs,
        CancellationToken cancellationToken)
    {
        var candidate = decision.Entry.Candidate;
        Word word;
        if (decision.Word is null)
        {
            word = new Word
            {
                Key = candidate.Key,
                Version = 1,
                CreatedAt = manifest.CreatedAtUnixMilliseconds
            };
            database.Words.Add(word);
        }
        else
        {
            // Changed source wins: bump from the database's current version (never a bundle number)
            // and demote the learner cards through the same lifecycle the manual importer uses.
            word = decision.Word;
            await ContentCardLifecycle.DemoteAsync(database, word.Id, cancellationToken);
            word.Version = checked(word.Version + 1);
        }
        ApplyWordFields(word, manifest, candidate, run, decision.ContentHash, status, nowMs);
        await database.SaveChangesAsync(cancellationToken);
        await PopulateChildrenAsync(database, word, candidate, cancellationToken);
        database.WordRevisions.Add(new WordRevision
        {
            WordId = word.Id,
            Version = word.Version,
            Action = "import",
            SnapshotJson = Snapshot(manifest, candidate, status).ToJsonString(),
            CreatedAt = nowMs
        });
        AddProvenance(database, word, run, candidate);
        await database.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyWordFields(Word word, CouncilManifest manifest, CouncilCandidate candidate,
        CouncilRun run, string contentHash, string status, long nowMs)
    {
        word.Headword = candidate.Lemma;
        word.Article = candidate.Article;
        word.Gender = candidate.Gender;
        word.Pos = candidate.Pos;
        word.Cefr = candidate.Cefr;
        // The bundle carries no authoring rank: a council word is unranked, exactly like a corpus
        // file without `difficulty` (NULL sorts after every ranked word).
        word.Difficulty = null;
        word.RegionScope = RegionScopeFor(candidate.Region);
        word.RegionNotes = null;
        word.Register = candidate.Register;
        word.RegisterNotes = null;
        word.Notes = null;
        word.Plural = candidate.Forms?.Plural;
        word.Status = status;
        word.Source = "council";
        word.CouncilRunId = run.Id;
        word.CouncilAgreement = candidate.Council.Agreement;
        word.CouncilBand = candidate.Council.Band;
        word.ReducedConfidence = manifest.ReducedConfidence ? 1 : 0;
        word.LintWarnings = candidate.Council.LintWarnings.Count == 0
            ? null
            : JsonSerializer.Serialize(candidate.Council.LintWarnings);
        word.ProvenanceMethod = "council";
        word.ProvenanceRun = manifest.RunId;
        word.ContentHash = contentHash;
        word.StatusChangedAt = nowMs;
        word.UpdatedAt = nowMs;
    }

    // The bundle codes are a bijection onto the corpus scope vocabulary (C2 review §3): ES is
    // peninsular-generic and ES-NA is Navarre-specific. The alternatives table stores the codes.
    private static string RegionScopeFor(string region) => region switch
    {
        "ES" => "peninsular-generic",
        "ES-NA" => "navarre",
        _ => throw new InvalidOperationException("Unknown council region code: " + region)
    };

    private static async Task PopulateChildrenAsync(PalabrasDbContext database, Word word,
        CouncilCandidate candidate, CancellationToken cancellationToken)
    {
        // Retained sense ordinals keep their ids: learner cards reference them.
        var senses = await database.Senses.Where(sense => sense.WordId == word.Id).ToListAsync(cancellationToken);
        for (var index = 0; index < candidate.Senses.Count; index++)
        {
            var source = candidate.Senses[index];
            var sense = senses.SingleOrDefault(existing => existing.SenseOrder == index);
            if (sense is null)
            {
                sense = new Sense { WordId = word.Id, SenseOrder = index };
                database.Senses.Add(sense);
            }
            sense.EnGloss = source.EnGloss;
            // Same defaults as ContentImporter: gloss split on ", " and the lemma for Spanish answers.
            sense.EnAccepted = source.EnAccepted is { Count: > 0 }
                ? JsonSerializer.Serialize(source.EnAccepted)
                : JsonSerializer.Serialize(source.EnGloss.Split(", ", StringSplitOptions.None));
            sense.EsAccepted = source.EsAccepted is { Count: > 0 }
                ? JsonSerializer.Serialize(source.EsAccepted)
                : JsonSerializer.Serialize(new[] { word.Headword });
            sense.ExampleEs = source.ExampleEs;
            sense.ExampleEn = source.ExampleEn;
            sense.AccentsAreThePoint = source.AccentsAreThePoint;
            sense.Notes = source.Notes;
        }
        await ContentCardLifecycle.ReconcileSensesAsync(database, senses, candidate.Senses.Count,
            cancellationToken);

        database.WordAlternatives.RemoveRange(
            await database.WordAlternatives.Where(alternative => alternative.WordId == word.Id)
                .ToListAsync(cancellationToken));
        // Rank per plan §2.5: sense order, then support desc, then field. The bundle index breaks
        // the last tie so the ranks stay stable for identical input.
        var ordered = candidate.Alternatives.Select((alternative, index) => (Alternative: alternative, Index: index))
            .OrderBy(pair => pair.Alternative.SenseIndex)
            .ThenByDescending(pair => pair.Alternative.Support)
            .ThenBy(pair => pair.Alternative.Field, StringComparer.Ordinal)
            .ThenBy(pair => pair.Index)
            .ToArray();
        for (var rank = 0; rank < ordered.Length; rank++)
        {
            var alternative = ordered[rank].Alternative;
            database.WordAlternatives.Add(new WordAlternative
            {
                WordId = word.Id,
                // The ΔF field name goes into both columns. `kind` has no CHECK and the reviewer chip
                // resolver now understands the v2 vocabulary, so a council chip is visible before
                // C4's `field` preference lands.
                Kind = alternative.Field,
                Field = alternative.Field,
                Value = AlternativeValue(alternative),
                Gloss = alternative.Es is null ? null : alternative.En,
                Reason = alternative.Rationale ?? AlternativeReasonFallback,
                TargetKey = alternative.TargetKey,
                // word_alternatives.example means the Spanish sentence in use (word.schema.json:
                // "Optional Spanish sentence showing the alternative in use", and ContentImporter
                // copies the document's `example` verbatim). The English translation rides on Gloss
                // so the chip shows it and an example swap can refresh sense.ExampleEn.
                Example = alternative.Field == "example" ? alternative.Es : null,
                Rank = rank,
                Support = alternative.Support,
                Models = JsonSerializer.Serialize(alternative.Models),
                Region = alternative.Region,
                Register = alternative.Register,
                WordVersion = word.Version,
                Source = "council"
            });
        }

        var topics = new[] { candidate.Topic };
        var existingTopics = await database.WordTopics.Where(topic => topic.WordId == word.Id)
            .ToListAsync(cancellationToken);
        database.WordTopics.RemoveRange(
            existingTopics.Where(topic => !topics.Contains(topic.Topic, StringComparer.Ordinal)));
        foreach (var topic in topics.Except(existingTopics.Select(topic => topic.Topic), StringComparer.Ordinal))
        {
            database.WordTopics.Add(new WordTopic { WordId = word.Id, Topic = topic });
        }
    }

    // word_alternatives.value is NOT NULL: a gloss-only alternative has `en` only, a variant has `es`
    // only, an example has both.
    private static string AlternativeValue(CouncilAlternative alternative) =>
        alternative.Es ?? alternative.En ?? "";

    private static void AddProvenance(PalabrasDbContext database, Word word, CouncilRun run,
        CouncilCandidate candidate)
    {
        var seen = new HashSet<(string Model, string Role)>();
        // Proposers come from council.proposed_by, never from alternatives[].models: an alternative
        // is a value a model preferred over this candidate, so its models are the dissenters. The
        // schema requires a non-empty list, and the validator checked every id against the manifest
        // roster and the propose phase, so this loop is a straight projection.
        foreach (var model in candidate.Council.ProposedBy.Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (!seen.Add((model, "proposed")))
            {
                continue;
            }
            database.WordProvenances.Add(new WordProvenance
            {
                WordId = word.Id,
                WordVersion = word.Version,
                CouncilRunId = run.Id,
                ModelId = model,
                Role = "proposed"
            });
        }
        foreach (var critique in candidate.Council.Critique)
        {
            var role = critique.Verdict == "issue" ? "critique_issue" : "critique_ok";
            if (!seen.Add((critique.Model, role)))
            {
                continue;
            }
            database.WordProvenances.Add(new WordProvenance
            {
                WordId = word.Id,
                WordVersion = word.Version,
                CouncilRunId = run.Id,
                ModelId = critique.Model,
                Role = role,
                Issues = critique.Issues is { Count: > 0 } ? JsonSerializer.Serialize(critique.Issues) : null
            });
        }
    }

    // The revision snapshot is the reviewer gate's read model (ReviewTransitions reads senses[].gloss,
    // acceptedEs, region, topics...), so the candidate is mapped into the corpus document shape the
    // manual importer stores. A verbatim candidate line would make approve_edit/approve_alt fail on
    // every council word.
    private static JsonObject Snapshot(CouncilManifest manifest, CouncilCandidate candidate, string status)
    {
        var senses = new JsonArray();
        foreach (var sense in candidate.Senses)
        {
            IReadOnlyList<string> acceptedEn = sense.EnAccepted is { Count: > 0 }
                ? sense.EnAccepted
                : sense.EnGloss.Split(", ", StringSplitOptions.None);
            IReadOnlyList<string> acceptedEs = sense.EsAccepted is { Count: > 0 }
                ? sense.EsAccepted
                : new[] { candidate.Lemma };
            var node = new JsonObject
            {
                ["gloss"] = sense.EnGloss,
                ["acceptedEn"] = JsonSerializer.SerializeToNode(acceptedEn)!,
                ["acceptedEs"] = JsonSerializer.SerializeToNode(acceptedEs)!,
                ["example"] = sense.ExampleEs,
                ["exampleGloss"] = sense.ExampleEn,
                ["accentsAreThePoint"] = sense.AccentsAreThePoint
            };
            if (sense.Notes is { } notes)
            {
                node["notes"] = notes;
            }
            senses.Add(node);
        }
        var alternatives = new JsonArray();
        foreach (var alternative in candidate.Alternatives)
        {
            alternatives.Add(new JsonObject
            {
                ["kind"] = alternative.Field,
                ["value"] = AlternativeValue(alternative),
                ["gloss"] = alternative.Es is null ? null : alternative.En,
                ["reason"] = alternative.Rationale ?? AlternativeReasonFallback,
                ["targetId"] = alternative.TargetKey,
                ["example"] = alternative.Field == "example" ? alternative.Es : null
            });
        }
        return new JsonObject
        {
            ["id"] = candidate.Key,
            ["headword"] = candidate.Lemma,
            ["article"] = candidate.Article,
            ["gender"] = candidate.Gender,
            ["partOfSpeech"] = candidate.Pos,
            ["cefr"] = candidate.Cefr,
            ["plural"] = candidate.Forms?.Plural,
            ["region"] = new JsonObject
            {
                ["variant"] = "es-ES",
                ["scope"] = RegionScopeFor(candidate.Region),
                ["notes"] = null
            },
            ["register"] = candidate.Register,
            ["topics"] = new JsonArray(candidate.Topic),
            ["senses"] = senses,
            ["alternatives"] = alternatives,
            ["status"] = status,
            ["provenance"] = new JsonObject
            {
                ["method"] = "council",
                ["run"] = manifest.RunId
            },
            ["createdAt"] = DateTimeOffset.FromUnixTimeMilliseconds(manifest.CreatedAtUnixMilliseconds)
                .ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static CouncilBundleImportResult Refused(CouncilManifest? manifest, IReadOnlyList<string> refusals) =>
        new(manifest?.RunId ?? "", manifest?.Mode ?? "", refusals, AlreadyImported: false, 0, 0, 0, 0, 0, []);
}
