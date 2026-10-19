namespace Palabras.Persistence;

// M3b council import (design-amendment.md ΔF:556-583): per-model, per-phase outcome of one council run.
// A failed, timed-out or invalid model is still recorded — that is how reduced_confidence stays
// explainable after the bundle is imported and the raw responses are gone.
internal sealed class CouncilModelResult
{
    public int Id { get; set; }
    public int CouncilRunId { get; set; }
    public string ModelId { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Status { get; set; } = "";
    public int? LatencyMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public double? CostUsdEst { get; set; }
    public int InvalidCount { get; set; }
    public string? Error { get; set; }
}
