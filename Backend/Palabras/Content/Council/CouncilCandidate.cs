using System.Text.Json.Serialization;

namespace Palabras.Content.Council;

// Typed view of content/schema/council-candidate.schema.json (one JSONL line). Validated before it
// is deserialized, so members stay plain. The raw JsonNode is kept alongside it by the validator:
// words.content_hash and the import revision snapshot are computed from the candidate as produced,
// not from a re-serialisation of this class.
internal sealed class CouncilCandidate
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("lemma")]
    public string Lemma { get; set; } = "";

    [JsonPropertyName("article")]
    public string? Article { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("pos")]
    public string Pos { get; set; } = "";

    [JsonPropertyName("cefr")]
    public string Cefr { get; set; } = "";

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("register")]
    public string Register { get; set; } = "";

    [JsonPropertyName("latam_equivalent")]
    public string? LatamEquivalent { get; set; }

    [JsonPropertyName("forms")]
    public CouncilCandidateForms? Forms { get; set; }

    [JsonPropertyName("senses")]
    public List<CouncilSense> Senses { get; set; } = [];

    [JsonPropertyName("alternatives")]
    public List<CouncilAlternative> Alternatives { get; set; } = [];

    [JsonPropertyName("council")]
    public CouncilCandidateVerdict Council { get; set; } = new();
}

internal sealed class CouncilCandidateForms
{
    [JsonPropertyName("plural")]
    public string? Plural { get; set; }
}

internal sealed class CouncilSense
{
    [JsonPropertyName("en_gloss")]
    public string EnGloss { get; set; } = "";

    [JsonPropertyName("en_accepted")]
    public List<string>? EnAccepted { get; set; }

    [JsonPropertyName("es_accepted")]
    public List<string>? EsAccepted { get; set; }

    [JsonPropertyName("example_es")]
    public string ExampleEs { get; set; } = "";

    [JsonPropertyName("example_en")]
    public string ExampleEn { get; set; } = "";

    [JsonPropertyName("accents_are_the_point")]
    public bool AccentsAreThePoint { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

internal sealed class CouncilAlternative
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("sense")]
    public int? Sense { get; set; }

    [JsonPropertyName("es")]
    public string? Es { get; set; }

    [JsonPropertyName("en")]
    public string? En { get; set; }

    [JsonPropertyName("support")]
    public int Support { get; set; }

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = [];

    [JsonPropertyName("rationale")]
    public string? Rationale { get; set; }

    [JsonPropertyName("target_key")]
    public string? TargetKey { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("register")]
    public string? Register { get; set; }

    internal int SenseIndex => Sense ?? 0;
}

internal sealed class CouncilCandidateVerdict
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("agreement")]
    public double Agreement { get; set; }

    [JsonPropertyName("band")]
    public string Band { get; set; } = "";

    [JsonPropertyName("proposal_support")]
    public double ProposalSupport { get; set; }

    // The models that proposed the winning candidate. Deliberately separate from
    // CouncilAlternative.Models: an alternative's models dissented from this candidate, so inferring
    // `proposed` provenance from alternatives names the wrong models (and names none when a
    // candidate has no alternatives).
    [JsonPropertyName("proposed_by")]
    public List<string> ProposedBy { get; set; } = [];

    [JsonPropertyName("critique")]
    public List<CouncilCritique> Critique { get; set; } = [];

    [JsonPropertyName("lint_warnings")]
    public List<string> LintWarnings { get; set; } = [];

    [JsonPropertyName("lint_actions")]
    public List<string> LintActions { get; set; } = [];
}

internal sealed class CouncilCritique
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "";

    [JsonPropertyName("issues")]
    public List<string>? Issues { get; set; }
}
