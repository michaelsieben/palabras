namespace Palabras.Content.Council;

// Per-candidate outcome of one bundle, in candidates.jsonl order. The codes are the CLI's summary
// vocabulary (plan §3.2); a reviewer reads them to know why a key did not move.
internal sealed record CouncilCandidateOutcome(string Key, int Line, string Outcome);

// One bundle's import outcome. Refusals are bundle-level lines (validator issues or the rework
// guards); when Refusals is non-empty the transaction was rolled back and nothing was written.
internal sealed record CouncilBundleImportResult(
    string RunId,
    string Mode,
    IReadOnlyList<string> Refusals,
    bool AlreadyImported,
    int Created,
    int Updated,
    int Unchanged,
    int SkippedExisting,
    int SkippedReviewerEditNewer,
    IReadOnlyList<CouncilCandidateOutcome> Candidates)
{
    internal bool Refused => Refusals.Count > 0;

    // "The bundle is in the database": either this call wrote it or a previous one did.
    internal bool Imported => !Refused && (AlreadyImported || Created + Updated > 0);

    internal int ImportedCandidates => Created + Updated;
}

internal static class CouncilImportOutcomes
{
    internal const string Created = "created";
    internal const string Updated = "updated";
    internal const string Unchanged = "unchanged";
    internal const string SkippedExisting = "skipped_existing";
    internal const string SkippedReviewerEditNewer = "skipped_reviewer_edit_newer";
    // Rework may only version a word that exists; a missing key is a bundle-level refusal, because
    // importing it as a new word would bypass the propose-only path (Q-M3-7).
    internal const string MissingWord = "missing_word";
}

// CLI/sync flags. ProposeOnly is the startup-sync guard (Q-M3-7): a rework bundle changes the status
// and version of approved content, so the importer itself refuses it for the sync path and only the
// explicit `import-council` verb passes both modes through.
internal sealed record CouncilImportOptions(
    bool DryRun = false,
    bool Force = false,
    bool Hold = false,
    bool ProposeOnly = false);
