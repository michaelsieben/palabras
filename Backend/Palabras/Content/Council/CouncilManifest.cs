using System.Globalization;
using System.Text.Json.Serialization;

namespace Palabras.Content.Council;

// Typed view of content/schema/council-manifest.schema.json. Validation happens first (schema +
// CouncilBundleValidator's semantic rules), so these properties are plain DTO members with no
// defensive parsing: a null here means the caller skipped validation, which is a bug, not data.
internal sealed class CouncilManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("prompt_version")]
    public CouncilPromptVersions PromptVersion { get; set; } = new();

    [JsonPropertyName("style_guide_sha256")]
    public string StyleGuideSha256 { get; set; } = "";

    [JsonPropertyName("request")]
    public CouncilRequest Request { get; set; } = new();

    [JsonPropertyName("models")]
    public List<CouncilModelManifest> Models { get; set; } = [];

    [JsonPropertyName("models_requested")]
    public int ModelsRequested { get; set; }

    [JsonPropertyName("models_succeeded")]
    public CouncilPhaseCounts ModelsSucceeded { get; set; } = new();

    [JsonPropertyName("reduced_confidence")]
    public bool ReducedConfidence { get; set; }

    [JsonPropertyName("counts")]
    public CouncilCounts Counts { get; set; } = new();

    [JsonPropertyName("candidates_sha256")]
    public string? CandidatesSha256 { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    // The schema validated the format, so a failed parse here would mean the schema and this
    // mapper disagree; let it throw rather than invent a timestamp.
    internal long CreatedAtUnixMilliseconds =>
        DateTimeOffset.Parse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds();
}

internal sealed class CouncilPromptVersions
{
    [JsonPropertyName("propose")]
    public string Propose { get; set; } = "";

    [JsonPropertyName("critique")]
    public string Critique { get; set; } = "";
}

internal sealed class CouncilRequest
{
    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("cefr")]
    public string? Cefr { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }
}

internal sealed class CouncilModelManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("phases")]
    public CouncilPhases Phases { get; set; } = new();

    [JsonPropertyName("latency_ms")]
    public CouncilLatency? LatencyMs { get; set; }

    [JsonPropertyName("tokens")]
    public CouncilTokens? Tokens { get; set; }

    [JsonPropertyName("cost_usd_est")]
    public double? CostUsdEst { get; set; }

    [JsonPropertyName("invalid_count")]
    public int InvalidCount { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

internal sealed class CouncilPhases
{
    [JsonPropertyName("propose")]
    public string Propose { get; set; } = "";

    [JsonPropertyName("critique")]
    public string Critique { get; set; } = "";
}

internal sealed class CouncilPhaseCounts
{
    [JsonPropertyName("propose")]
    public int Propose { get; set; }

    [JsonPropertyName("critique")]
    public int Critique { get; set; }
}

internal sealed class CouncilCounts
{
    [JsonPropertyName("candidates")]
    public int Candidates { get; set; }

    [JsonPropertyName("dropped")]
    public int Dropped { get; set; }
}

internal sealed class CouncilLatency
{
    [JsonPropertyName("propose")]
    public int? Propose { get; set; }

    [JsonPropertyName("critique")]
    public int? Critique { get; set; }
}

internal sealed class CouncilTokens
{
    [JsonPropertyName("in")]
    public int? In { get; set; }

    [JsonPropertyName("out")]
    public int? Out { get; set; }
}
