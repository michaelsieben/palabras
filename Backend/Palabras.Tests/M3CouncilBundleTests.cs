using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Palabras.Content;
using Palabras.Content.Council;
using Xunit;

namespace Palabras.Tests;

// M3b C2: the bundle format both the council CLI (tools/council) and the importer must agree on.
// The fixtures under tools/council/fixtures are shared with the CLI's validator, so this class is
// the C# half of T-4. It proves three things the importer will rely on: every reject is reported
// with a bundle location instead of throwing, semantic rules JSON Schema cannot express are
// enforced, and a bundle that would import a dangling reference is refused before any write.
public sealed class M3CouncilBundleTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", "palabras-council-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> FixtureCases() =>
        FixtureSources().Select(fixture => new object[] { fixture.Dir, fixture.Valid });

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public async Task FixturesMatchTheirExpectedVerdictAsync(string directory, bool valid)
    {
        var result = await ValidateFixtureAsync(directory);

        Assert.True(valid == result.IsValid, Describe(result));
        if (valid)
        {
            Assert.NotNull(result.Manifest);
            return;
        }
        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, issue =>
        {
            Assert.False(string.IsNullOrWhiteSpace(issue.Location));
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        });
    }

    [Fact]
    public void EveryFixtureDirectoryHasAnExpectedVerdict()
    {
        var expected = Directory.GetDirectories(FixtureRoot())
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var listed = FixtureSources().Select(fixture => fixture.Dir).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, listed);
    }

    [Fact]
    public async Task ValidProposeExposesTheTypedManifestAndCandidatesAsync()
    {
        var result = await ValidateFixtureAsync("valid-propose");

        Assert.True(result.IsValid, Describe(result));
        var manifest = result.Manifest ?? throw new InvalidOperationException("manifest did not parse");
        Assert.Equal("palabras.council.manifest.v1", manifest.Schema);
        Assert.Equal("01JAB3KZ8Q4VX2M7Y6T5R4S3N2", manifest.RunId);
        Assert.Equal("propose", manifest.Mode);
        Assert.Equal("v1", manifest.PromptVersion.Propose);
        Assert.Equal("food", manifest.Request.Topic);
        Assert.Equal(3, manifest.Models.Count);
        Assert.Equal(3, manifest.ModelsSucceeded.Propose);
        Assert.Equal(3, manifest.ModelsSucceeded.Critique);
        Assert.False(manifest.ReducedConfidence);
        Assert.Equal(2, manifest.Counts.Candidates);
        Assert.Equal(64, manifest.CandidatesSha256!.Length);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            manifest.CreatedAtUnixMilliseconds);

        var patata = result.Candidates.Single(entry => entry.Candidate.Key == "es-patata-01");
        Assert.Equal(1, patata.Line);
        Assert.Equal("la", patata.Candidate.Article);
        Assert.Equal("feminine", patata.Candidate.Gender);
        Assert.Equal("ES", patata.Candidate.Region);
        Assert.Equal("media", patata.Candidate.Council.Band);
        Assert.Equal(0.67, patata.Candidate.Council.Agreement, 2);
        Assert.Equal("issue", patata.Candidate.Council.Critique[2].Verdict);
        Assert.Equal(new[] { "register" }, patata.Candidate.Council.Critique[2].Issues);
        Assert.Equal(5, patata.Candidate.Alternatives.Count);
        Assert.Equal("patatas", patata.Candidate.Forms?.Plural);

        var nevera = result.Candidates.Single(entry => entry.Candidate.Key == "es-nevera-01");
        Assert.Equal(2, nevera.Line);
        Assert.Equal("ES-NA", nevera.Candidate.Region);
        Assert.Equal("colloquial", nevera.Candidate.Register);
        Assert.Equal(2, nevera.Candidate.Senses.Count);
        Assert.Equal(7, nevera.Candidate.Alternatives.Count);
        Assert.Equal(1, nevera.Candidate.Alternatives[6].SenseIndex);
        Assert.Equal("register_variant", nevera.Candidate.Alternatives[6].Field);
        Assert.Equal("formal", nevera.Candidate.Alternatives[6].Register);
        Assert.Equal("alta", nevera.Candidate.Council.Band);
        Assert.Empty(nevera.Candidate.Council.LintWarnings);
    }

    [Fact]
    public async Task ValidReworkKeepsItsRunIdAndModeAsync()
    {
        var result = await ValidateFixtureAsync("valid-rework");

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal("rework", result.Manifest!.Mode);
        Assert.Single(result.Candidates);
        Assert.Equal(result.Manifest.RunId, result.Candidates[0].Candidate.Council.RunId);
    }

    [Fact]
    public async Task MissingRequiredFieldNamesTheCandidateLineAsync()
    {
        var result = await ValidateFixtureAsync("missing-required-field");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("candidates.jsonl:1", issue.Location);
        Assert.Contains("lemma", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateKeyNamesTheOffendingLineAsync()
    {
        var result = await ValidateFixtureAsync("duplicate-key");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("candidates.jsonl:2", issue.Location);
        Assert.Contains("duplicate key 'es-patata-01'", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DanglingTargetKeyIsRefusedUnlessTheCallerKnowsTheWordAsync()
    {
        var unresolved = await ValidateFixtureAsync("dangling-alternative-reference");
        Assert.False(unresolved.IsValid);
        Assert.Contains(unresolved.Issues,
            issue => issue.Message.Contains("target_key 'es-inexistente-01' does not resolve", StringComparison.Ordinal));

        var resolved = await ValidateFixtureAsync("dangling-alternative-reference",
            new HashSet<string>(["es-inexistente-01"], StringComparer.Ordinal));
        Assert.True(resolved.IsValid, Describe(resolved));
    }

    [Fact]
    public async Task AlternativeFieldOutsideTheWordAlternativeVocabularyIsRefusedAsync()
    {
        var result = await ValidateFixtureAsync("invalid-alternative-field");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("candidates.jsonl:1", issue.Location);
        Assert.Contains("alternatives/0/field", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateThatSetsTheWordSourceColumnIsRefusedAsync()
    {
        var result = await ValidateFixtureAsync("candidate-carries-source");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("candidates.jsonl:1", issue.Location);
        Assert.Contains("source", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownModelIdIsNamedInTheDiagnosticAsync()
    {
        var result = await ValidateFixtureAsync("unknown-model-id");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Location == "candidates.jsonl:1.alternatives[0]" &&
            issue.Message.Contains("'mistral:ghost-1'", StringComparison.Ordinal) &&
            issue.Message.Contains("is not declared in manifest.models", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HashMismatchIsReportedAsABundleLevelIssueAsync()
    {
        var result = await ValidateFixtureAsync("hash-mismatch");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("candidates.jsonl", issue.Location);
        Assert.Contains("candidates_sha256", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRunIsRefusedBeforeItsMissingCandidatesFileAsync()
    {
        var result = await ValidateFixtureAsync("failed-run");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("manifest.json", issue.Location);
        Assert.Contains("status is \"failed\"", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestThatOverstatesSucceededModelsIsRefusedAsync()
    {
        var bundle = CopyFixture("valid-propose");
        EditManifest(bundle, manifest => manifest["models_succeeded"]!["critique"] = 1);

        var result = await CouncilBundleValidator.ValidateAsync(bundle, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Location == "manifest.json" &&
            issue.Message.Contains("models_succeeded.critique", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryBrokenCandidateIsReportedInsteadOfStoppingAtTheFirstAsync()
    {
        var bundle = CopyFixture("valid-propose");
        var first = JsonNode.Parse(File.ReadAllLines(Path.Combine(FixtureRoot(), "valid-propose", "candidates.jsonl"))[0])!;
        var second = first.DeepClone();
        second["key"] = "es-broken-01";
        second.AsObject().Remove("lemma");
        var third = first.DeepClone();
        third["key"] = "es-broken-02";
        third["council"]!["band"] = "alta";
        var lines = new[] { first, second, third }.Select(line => line.ToJsonString());
        var text = string.Join('\n', lines) + '\n';
        File.WriteAllText(Path.Combine(bundle, "candidates.jsonl"), text, new UTF8Encoding(false));
        EditManifest(bundle, manifest =>
        {
            manifest["counts"]!["candidates"] = 3;
            manifest["candidates_sha256"] = CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(text));
        });

        var result = await CouncilBundleValidator.ValidateAsync(bundle, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Location == "candidates.jsonl:2");
        Assert.Contains(result.Issues, issue => issue.Location == "candidates.jsonl:3");
        Assert.DoesNotContain(result.Issues, issue => issue.Location == "candidates.jsonl:1");
    }

    [Fact]
    public void CanonicalBytesIgnorePropertyOrderAndStringEscapes()
    {
        var escaped = JsonNode.Parse("""{"b":1,"a":"\u00e1"}""")!;
        var literal = JsonNode.Parse("""{"a":"á","b":1}""")!;

        Assert.Equal(CanonicalJson.Bytes(escaped), CanonicalJson.Bytes(literal));
        var hash = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(escaped));
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void CanonicalContentBytesDropRootLifecycleFields()
    {
        var source = JsonNode.Parse("""{"a":1}""")!;
        var reviewed = JsonNode.Parse("""{"status":"approved","a":1,"review":{"x":1},"updatedAt":null}""")!;

        Assert.Equal(CanonicalJson.Bytes(source), CanonicalJson.ContentBytes(reviewed));
    }

    private static async Task<CouncilBundleValidation> ValidateFixtureAsync(
        string directory,
        IReadOnlySet<string>? knownWordKeys = null) =>
        await CouncilBundleValidator.ValidateAsync(
            Path.Combine(FixtureRoot(), directory), knownWordKeys, CancellationToken.None);

    private static string Describe(CouncilBundleValidation validation) =>
        string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Location + ": " + issue.Message));

    private static List<(string Dir, bool Valid)> FixtureSources()
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot(), "cases.json")))!;
        return cases["cases"]!.AsArray()
            .Select(item => (item!["dir"]!.GetValue<string>(), item["valid"]!.GetValue<bool>()))
            .ToList();
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

    private string NewBundle()
    {
        var bundle = Path.Combine(_temp, "bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        return bundle;
    }

    private string CopyFixture(string name)
    {
        var source = Path.Combine(FixtureRoot(), name);
        var target = NewBundle();
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return target;
    }

    private static void EditManifest(string bundle, Action<JsonObject> edit)
    {
        var path = Path.Combine(bundle, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(manifest);
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, true);
        }
    }
}
