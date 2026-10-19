using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Palabras.Content.Council;

internal sealed record CouncilBundleIssue(string Location, string Message);

// One parsed candidates.jsonl line. Raw is the line as produced: the content hash and the import
// revision snapshot come from it, never from re-serialising Candidate.
internal sealed record CouncilBundleCandidate(int Line, JsonNode Raw, CouncilCandidate Candidate);

internal sealed record CouncilBundleValidation(
    CouncilManifest? Manifest,
    IReadOnlyList<CouncilBundleCandidate> Candidates,
    IReadOnlyList<CouncilBundleIssue> Issues)
{
    internal bool IsValid => Issues.Count == 0;
}

// Validates one bundle directory before anything is written. Diagnostics follow the corpus
// importer's shape: every reject is collected with a "manifest.json" or "candidates.jsonl:<line>"
// location, so a producer sees all broken lines in one run, not only the first.
internal static class CouncilBundleValidator
{
    private const string ManifestFileName = "manifest.json";
    private const string CandidatesFileName = "candidates.jsonl";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private static readonly Lazy<JsonSchema> ManifestSchema = new(() => LoadSchema("council-manifest.schema.json"));
    private static readonly Lazy<JsonSchema> CandidateSchema = new(() => LoadSchema("council-candidate.schema.json"));

    // knownWordKeys is the set of keys the caller can resolve outside the bundle (the corpus
    // lexicon for the CLI, the words table for the importer). Without it a target_key may only
    // point inside the bundle, because an unresolvable reference would become a dangling
    // word_alternatives.target_key that the reviewer undo path cannot follow.
    internal static async Task<CouncilBundleValidation> ValidateAsync(
        string directory,
        IReadOnlySet<string>? knownWordKeys,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            throw new ArgumentException("Bundle directory does not exist: " + directory, nameof(directory));
        }
        var issues = new List<CouncilBundleIssue>();
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new(null, [], [new(ManifestFileName, "manifest.json is missing; a bundle is a directory with manifest.json and candidates.jsonl")]);
        }
        JsonNode manifestNode;
        try
        {
            manifestNode = ParseDocument(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(null, [], [new(ManifestFileName, "manifest.json is not valid JSON: " + exception.Message)]);
        }
        AddSchemaIssues(ManifestSchema.Value, manifestNode, ManifestFileName, issues);
        if (issues.Count > 0)
        {
            return new(null, [], issues);
        }
        var manifest = manifestNode.Deserialize<CouncilManifest>(SerializerOptions)!;
        ValidateManifestSemantics(manifest, issues);
        if (issues.Count > 0)
        {
            // A manifest whose counts or model roster contradict themselves cannot be used to
            // judge the candidates: every later rule (run_id, model ids, band) reads from it.
            return new(manifest, [], issues);
        }
        if (manifest.Status == "failed")
        {
            // The run never met the bundle contract (for example COUNCIL_MIN_MODELS). It is an
            // audit artefact, not importable input; the CLI and startup sync both refuse it.
            issues.Add(new(ManifestFileName, "manifest status is \"failed\"; the run produced no importable bundle"));
            return new(manifest, [], issues);
        }

        var candidatesPath = Path.Combine(directory, CandidatesFileName);
        if (!File.Exists(candidatesPath))
        {
            issues.Add(new(CandidatesFileName, "candidates.jsonl is missing; a status ok bundle must carry candidates"));
            return new(manifest, [], issues);
        }
        var rawBytes = await File.ReadAllBytesAsync(candidatesPath, cancellationToken);
        var expectedHash = manifest.CandidatesSha256!;
        var actualHash = CanonicalJson.Sha256Hex(rawBytes);
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            issues.Add(new(CandidatesFileName,
                $"candidates_sha256 {expectedHash} does not match the file's sha256 {actualHash}"));
        }

        var text = System.Text.Encoding.UTF8.GetString(rawBytes);
        var lines = text.Split('\n');
        var parsed = new List<CouncilBundleCandidate>();
        var linesWithContent = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            linesWithContent++;
            var lineNumber = index + 1;
            var at = $"{CandidatesFileName}:{lineNumber}";
            JsonNode node;
            try
            {
                node = ParseDocument(line);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                issues.Add(new(at, "not valid JSON: " + exception.Message));
                continue;
            }
            if (AddSchemaIssues(CandidateSchema.Value, node, at, issues))
            {
                continue;
            }
            var candidate = node.Deserialize<CouncilCandidate>(SerializerOptions)!;
            if (!keys.Add(candidate.Key))
            {
                issues.Add(new(at, $"duplicate key '{candidate.Key}'; keys are unique within one bundle"));
                continue;
            }
            parsed.Add(new(lineNumber, node, candidate));
        }
        if (manifest.Counts.Candidates != linesWithContent)
        {
            issues.Add(new(ManifestFileName,
                $"counts.candidates is {manifest.Counts.Candidates} but candidates.jsonl has {linesWithContent} lines"));
        }
        if (linesWithContent == 0)
        {
            issues.Add(new(CandidatesFileName, "candidates.jsonl holds no candidates; a status ok bundle must import at least one"));
            return new(manifest, [], issues);
        }

        // Resolve target_key against the bundle itself plus whatever the caller can see. The
        // bundle key set is complete only after the parse pass above.
        var resolvable = new HashSet<string>(keys, StringComparer.Ordinal);
        if (knownWordKeys != null)
        {
            resolvable.UnionWith(knownWordKeys);
        }
        foreach (var entry in parsed)
        {
            ValidateCandidateSemantics(entry, manifest, resolvable, issues);
        }
        return new(manifest, parsed, issues);
    }

    private static void ValidateManifestSemantics(CouncilManifest manifest, List<CouncilBundleIssue> issues)
    {
        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in manifest.Models)
        {
            if (!modelIds.Add(model.Id))
            {
                issues.Add(new(ManifestFileName, $"models[].id '{model.Id}' is declared more than once"));
            }
        }
        if (manifest.ModelsRequested != manifest.Models.Count)
        {
            issues.Add(new(ManifestFileName,
                $"models_requested is {manifest.ModelsRequested} but models[] lists {manifest.Models.Count} entries; a failed model is still listed"));
        }
        var proposeOk = manifest.Models.Count(model => model.Phases.Propose == "ok");
        var critiqueOk = manifest.Models.Count(model => model.Phases.Critique == "ok");
        if (manifest.ModelsSucceeded.Propose != proposeOk)
        {
            issues.Add(new(ManifestFileName,
                $"models_succeeded.propose is {manifest.ModelsSucceeded.Propose} but {proposeOk} models report phases.propose \"ok\""));
        }
        if (manifest.ModelsSucceeded.Critique != critiqueOk)
        {
            issues.Add(new(ManifestFileName,
                $"models_succeeded.critique is {manifest.ModelsSucceeded.Critique} but {critiqueOk} models report phases.critique \"ok\""));
        }
        // Reduced confidence is a fact about the run, not a producer choice: any phase short of
        // the requested roster means the reviewer is judging a thinner consensus.
        var reduced = proposeOk < manifest.ModelsRequested || critiqueOk < manifest.ModelsRequested;
        if (manifest.ReducedConfidence != reduced)
        {
            issues.Add(new(ManifestFileName,
                $"reduced_confidence is {manifest.ReducedConfidence} but propose ok={proposeOk}/{manifest.ModelsRequested} and critique ok={critiqueOk}/{manifest.ModelsRequested}"));
        }
    }

    private static void ValidateCandidateSemantics(
        CouncilBundleCandidate entry,
        CouncilManifest manifest,
        IReadOnlySet<string> resolvableKeys,
        List<CouncilBundleIssue> issues)
    {
        var at = $"{CandidatesFileName}:{entry.Line}";
        var candidate = entry.Candidate;
        var modelIds = manifest.Models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);

        if (!StringComparer.Ordinal.Equals(candidate.Council.RunId, manifest.RunId))
        {
            issues.Add(new(at,
                $"council.run_id '{candidate.Council.RunId}' does not match manifest.run_id '{manifest.RunId}'"));
        }
        var expectedBand = BandFor(candidate.Council.Agreement);
        if (expectedBand == null)
        {
            issues.Add(new(at,
                $"council.agreement {candidate.Council.Agreement} is below 0.50; a candidate that weak belongs in dropped.jsonl and is never queued"));
        }
        else if (!StringComparer.Ordinal.Equals(candidate.Council.Band, expectedBand))
        {
            issues.Add(new(at,
                $"council.band '{candidate.Council.Band}' disagrees with council.agreement {candidate.Council.Agreement}: expected '{expectedBand}'"));
        }
        foreach (var (critique, index) in candidate.Council.Critique.Select((critique, index) => (critique, index)))
        {
            if (!modelIds.Contains(critique.Model))
            {
                issues.Add(new(at, $"council.critique[{index}].model '{critique.Model}' is not declared in manifest.models"));
            }
        }

        var seenAlternatives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (alternative, index) in candidate.Alternatives.Select((alternative, index) => (alternative, index)))
        {
            var altAt = $"{at}.alternatives[{index}]";
            if (alternative.SenseIndex >= candidate.Senses.Count)
            {
                issues.Add(new(altAt,
                    $"sense {alternative.SenseIndex} does not exist; the candidate has {candidate.Senses.Count} sense(s)"));
            }
            foreach (var model in alternative.Models)
            {
                if (!modelIds.Contains(model))
                {
                    issues.Add(new(altAt, $"models contains '{model}', which is not declared in manifest.models"));
                }
            }
            if (alternative.Support != alternative.Models.Count)
            {
                issues.Add(new(altAt,
                    $"support is {alternative.Support} but models lists {alternative.Models.Count}; support is the number of proposing models"));
            }
            if (alternative.Field == "example" && (alternative.Es == null || alternative.En == null))
            {
                issues.Add(new(altAt, "an example alternative needs both es and en (E.3 pairs)"));
            }
            if (alternative.Field == "gloss" && alternative.En == null)
            {
                issues.Add(new(altAt, "a gloss alternative needs en"));
            }
            if (alternative.Field is ("lemma" or "regional_variant" or "register_variant") && alternative.Es == null)
            {
                issues.Add(new(altAt, $"a {alternative.Field} alternative needs es"));
            }
            if (alternative.TargetKey is { } targetKey && !resolvableKeys.Contains(targetKey))
            {
                issues.Add(new(altAt,
                    $"target_key '{targetKey}' does not resolve to a word in this bundle or the known lexicon"));
            }
            var identity = $"{alternative.Field}|{alternative.SenseIndex}|{alternative.Es}|{alternative.En}";
            if (!seenAlternatives.Add(identity))
            {
                issues.Add(new(altAt, "duplicate alternative: same field, sense and value as an earlier entry"));
            }
        }

        for (var senseIndex = 0; senseIndex < candidate.Senses.Count; senseIndex++)
        {
            var senseAlternatives = candidate.Alternatives
                .Where(alternative => alternative.SenseIndex == senseIndex)
                .ToList();
            var glossCount = senseAlternatives.Count(alternative => alternative.Field == "gloss");
            if (glossCount is < 2 or > 4)
            {
                issues.Add(new(at, $"senses[{senseIndex}] has {glossCount} gloss alternatives; E.3 requires 2-4"));
            }
            var exampleCount = senseAlternatives.Count(alternative => alternative.Field == "example");
            if (exampleCount is < 1 or > 3)
            {
                issues.Add(new(at, $"senses[{senseIndex}] has {exampleCount} example alternatives; E.3 requires 1-3"));
            }
            foreach (var field in (string[])["lemma", "regional_variant", "register_variant"])
            {
                var count = senseAlternatives.Count(alternative => alternative.Field == field);
                if (count > 2)
                {
                    issues.Add(new(at, $"senses[{senseIndex}] has {count} {field} alternatives; E.3 allows at most 2"));
                }
            }
        }
    }

    // Q-M3-11: the importer recomputes the band from agreement and refuses a bundle where the two
    // disagree, so a producer bug cannot become queue order or batch-approve eligibility.
    private static string? BandFor(double agreement) => agreement switch
    {
        >= 0.75 => "alta",
        >= 0.50 => "media",
        _ => null
    };

    private static bool AddSchemaIssues(JsonSchema schema, JsonNode node, string location, List<CouncilBundleIssue> issues)
    {
        var evaluation = schema.Evaluate(node, CreateEvaluationOptions());
        if (evaluation.IsValid)
        {
            return false;
        }
        var errors = evaluation.Details
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Key}: {error.Value}"));
        issues.Add(new(location, "JSON schema validation failed: " + string.Join("; ", errors)));
        return true;
    }

    private static JsonSchema LoadSchema(string fileName) =>
        JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "content", "schema", fileName));

    // Per evaluation: JsonSchema.Net documents options as reusable, but a fresh instance keeps a
    // parallel test run from sharing mutable evaluator state by accident.
    private static EvaluationOptions CreateEvaluationOptions() => new()
    {
        RequireFormatValidation = true,
        OutputFormat = OutputFormat.List
    };

    private static JsonNode ParseDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement);
        return JsonNode.Parse(json)!;
    }

    // Mirrors ContentImporter.RejectDuplicateProperties: a duplicate property silently keeps the
    // last value in System.Text.Json, which would make two readers disagree about one document.
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Duplicate JSON property: " + property.Name);
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}
