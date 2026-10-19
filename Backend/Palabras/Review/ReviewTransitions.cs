using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Palabras.Content;
using Palabras.Persistence;

namespace Palabras.Review;

// One reviewer decision for one word, already validated by the HTTP layer as far as the wire shape
// goes. WordVersion is the version the verdict was made against; a mismatch is stale_version and
// the transition is never applied.
internal sealed record ReviewTransitionRequest(
    int WordVersion,
    string Action,
    string? Field,
    int? SenseOrder,
    int? AlternativeId,
    IReadOnlyList<ReviewEditRequest?>? Edits);

internal sealed record ReviewTransitionOutcome(
    string Result,
    int? Version = null,
    string? Field = null,
    int? AlternativeId = null,
    string? EditDiff = null);

// M4 state machine (plan §2.2, design-amendment.md ΔE.1:334-347). The CLI and the reviewer API
// share this core; only the allowed-from set and the caller of the card effects differ. The caller
// owns the transaction: content status, revisions, verdict row and card eligibility commit together.
internal static class ReviewTransitions
{
    // CLI approval keeps its shipped "any status" semantics; the reviewer API is strict.
    internal static readonly IReadOnlySet<string> AnyStatus = new HashSet<string>(StringComparer.Ordinal)
    {
        "draft", "pending_review", "approved", "needs_change", "rejected", "retired"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> StrictFrom =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ReviewActionNames.Approve] = new HashSet<string>(StringComparer.Ordinal) { "pending_review" },
            [ReviewActionNames.ApproveAlt] = new HashSet<string>(StringComparer.Ordinal) { "pending_review" },
            [ReviewActionNames.ApproveEdit] = new HashSet<string>(StringComparer.Ordinal) { "pending_review" },
            [ReviewActionNames.Reject] = new HashSet<string>(StringComparer.Ordinal) { "pending_review" },
            [ReviewActionNames.NeedsChange] = new HashSet<string>(StringComparer.Ordinal) { "pending_review" },
            [ReviewActionNames.Demote] = new HashSet<string>(StringComparer.Ordinal) { "approved" }
        };

    // Every action the reviewer UI can send is legal from exactly one status in M4. Anything else is
    // a per-item invalid_transition, never a database failure.
    internal static IReadOnlySet<string> ReviewerAllowedFrom(string action) =>
        StrictFrom.TryGetValue(action, out var allowed) ? allowed : EmptySet;

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);

    internal static async Task<ReviewTransitionOutcome> ApplyAsync(PalabrasDbContext database, Word word,
        ReviewTransitionRequest request, IReadOnlySet<string> allowedFrom, int? actorUserId, long nowMs,
        bool applyCardEffects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedFrom);
        if (word.Version != request.WordVersion)
        {
            return new(ReviewResults.StaleVersion);
        }
        if (!ReviewActionNames.IsKnown(request.Action))
        {
            return new(ReviewResults.Invalid);
        }
        if (!allowedFrom.Contains(word.Status))
        {
            return new(ReviewResults.InvalidTransition);
        }
        var snapshot = await LoadCurrentSnapshotAsync(database, word, cancellationToken);
        return request.Action switch
        {
            ReviewActionNames.Approve => await ApproveAsync(database, word, snapshot, actorUserId, nowMs,
                applyCardEffects, contentChanged: false, cancellationToken),
            ReviewActionNames.ApproveEdit => await ApproveEditAsync(database, word, snapshot, request,
                actorUserId, nowMs, applyCardEffects, cancellationToken),
            ReviewActionNames.ApproveAlt => await ApproveAlternativeAsync(database, word, snapshot, request,
                actorUserId, nowMs, applyCardEffects, cancellationToken),
            ReviewActionNames.Reject => await FlagAsync(database, word, snapshot, "rejected",
                ReviewActionNames.Reject, actorUserId, nowMs, cancellationToken),
            ReviewActionNames.NeedsChange => await FlagAsync(database, word, snapshot, "needs_change",
                ReviewActionNames.NeedsChange, actorUserId, nowMs, cancellationToken),
            _ => await DemoteAsync(database, word, snapshot, actorUserId, nowMs, applyCardEffects,
                cancellationToken)
        };
    }

    // Latest revision of the word's current version is the authoritative snapshot (same rule as
    // ContentCardLifecycle.ApproveAsync and ActiveSenses). A missing snapshot is corpus corruption,
    // not a reviewer error, so it fails loudly instead of silently approving an empty word.
    internal static async Task<JsonObject> LoadCurrentSnapshotAsync(PalabrasDbContext database, Word word,
        CancellationToken cancellationToken)
    {
        var source = await database.WordRevisions.AsNoTracking()
            .Where(revision => revision.WordId == word.Id && revision.Version == word.Version)
            .OrderByDescending(revision => revision.Id).Select(revision => revision.SnapshotJson)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Missing content snapshot for word: " + word.Key + ".");
        return JsonNode.Parse(source)?.AsObject()
            ?? throw new InvalidOperationException("Invalid content snapshot for word: " + word.Key + ".");
    }

    // Same query for a page of words; the queue and the approved list read only current-version
    // snapshots. Words without a snapshot are simply absent from the map.
    internal static async Task<Dictionary<int, JsonObject>> LoadCurrentSnapshotsAsync(PalabrasDbContext database,
        IReadOnlyCollection<int> wordIds, CancellationToken cancellationToken)
    {
        if (wordIds.Count == 0)
        {
            return [];
        }
        var ids = wordIds.Distinct().ToArray();
        var rows = await (from revision in database.WordRevisions.AsNoTracking()
                          join word in database.Words on revision.WordId equals word.Id
                          where ids.Contains(word.Id) && revision.Version == word.Version
                          orderby revision.Id descending
                          select new { revision.WordId, revision.SnapshotJson }).ToListAsync(cancellationToken);
        var snapshots = new Dictionary<int, JsonObject>();
        foreach (var row in rows)
        {
            if (snapshots.ContainsKey(row.WordId))
            {
                continue;
            }
            if (JsonNode.Parse(row.SnapshotJson) is JsonObject snapshot)
            {
                snapshots[row.WordId] = snapshot;
            }
        }
        return snapshots;
    }

    // Legacy word_alternatives carry a v1 `kind`; M3b council rows store the ΔF field name in `kind`
    // (and `field`). Both vocabularies resolve here, and the queue prefers `field` through
    // ChipFieldFor. `regional_note` is deliberately absent, and that is a recorded decision rather
    // than an omission: the note is stored (word_alternatives with field='regional_note', and in
    // the revision snapshot) but not offered as a chip, because the sheet has no regional_note tab
    // and approve_alt has no swap target for a note (it would fall through to ExampleEs and corrupt
    // the example). Revisiting it means a UI decision, not a bug fix.
    internal static string? FieldForAlternativeKind(string kind) => kind switch
    {
        "lemma" or "gloss" or "example" or "regional_variant" or "register_variant" => kind,
        "synonym" or "near-synonym" or "different-sense" or "previous_lemma" => ReviewFieldNames.Lemma,
        "regional" => ReviewFieldNames.RegionalVariant,
        "register" => ReviewFieldNames.RegisterVariant,
        "previous_gloss" => ReviewFieldNames.Gloss,
        "previous_example" => ReviewFieldNames.Example,
        _ => null
    };

    // The M3b `field` column wins over the legacy `kind` vocabulary when it is present; `previous_*`
    // rows still resolve through `kind` because only they are written without a field. The
    // regional_note exclusion survives the preference: council rows carry field='regional_note' and
    // kind='regional_note', and offering either would give approve_alt an example swap target that
    // corrupts the sense (see FieldForAlternativeKind above).
    internal static string? ChipFieldFor(string? field, string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var resolved = field ?? FieldForAlternativeKind(kind);
        return resolved == ReviewFieldNames.RegionalNote ? null : resolved;
    }

    private static async Task<ReviewTransitionOutcome> ApproveAsync(PalabrasDbContext database, Word word,
        JsonObject snapshot, int? actorUserId, long nowMs, bool applyCardEffects, bool contentChanged,
        CancellationToken cancellationToken)
    {
        ApplyStatus(snapshot, "approved", nowMs);
        word.Status = "approved";
        // Carry-forward policy: the columns are NULL for everything approved before M4. Any approval
        // that runs through this core now stamps them, so only legacy rows keep NULLs.
        word.StatusChangedAt = nowMs;
        word.ApprovedVersion = word.Version;
        word.UpdatedAt = nowMs;
        database.WordRevisions.Add(Revision(word, "approve", snapshot, actorUserId, nowMs));
        await database.SaveChangesAsync(cancellationToken);
        if (applyCardEffects)
        {
            // A content edit invalidates answers on the existing cards: epoch++ for this word's
            // senses, exactly like the importer's demote-on-change. A plain approve changes no
            // content, so it only creates missing cards.
            IReadOnlySet<int> changed = contentChanged ? new HashSet<int> { word.Id } : new HashSet<int>();
            await ContentCardLifecycle.ApproveAsync(database, [word.Id], changed, nowMs,
                cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }
        return new(ReviewResults.Applied, word.Version);
    }

    private static async Task<ReviewTransitionOutcome> ApproveEditAsync(PalabrasDbContext database, Word word,
        JsonObject snapshot, ReviewTransitionRequest request, int? actorUserId, long nowMs,
        bool applyCardEffects, CancellationToken cancellationToken)
    {
        if (request.Edits is not { Count: > 0 and <= 10 })
        {
            return new(ReviewResults.Invalid);
        }
        var senses = await LoadSensesAsync(database, word.Id, cancellationToken);
        var snapshotSenses = Senses(snapshot, word.Key);
        // Validate the whole edit list before touching a tracked entity: an item that fails later in
        // the same batch must not leave half an edit for the next SaveChanges to persist.
        var changes = new List<EditChange>(request.Edits.Count);
        var previous = new Dictionary<(string Field, int? SenseOrder), (string Value, string? Gloss)>();
        var diff = new JsonArray();
        foreach (var edit in request.Edits)
        {
            if (edit is null || !ReviewFieldNames.IsEditable(edit.Field) || edit.After is null)
            {
                return new(ReviewResults.Invalid);
            }
            var after = edit.After;
            string before;
            string? previousGloss = null;
            Sense? sense = null;
            JsonObject? source = null;
            IReadOnlyList<string>? accepted = null;
            switch (edit.Field)
            {
                case ReviewFieldNames.Lemma:
                    before = word.Headword;
                    break;
                case ReviewFieldNames.Article:
                    if (after is not ("el" or "la" or "los" or "las"))
                    {
                        return new(ReviewResults.Invalid);
                    }
                    before = word.Article ?? "";
                    break;
                case ReviewFieldNames.Gender:
                    if (after is not ("masculine" or "feminine" or "common" or "not_applicable"))
                    {
                        return new(ReviewResults.Invalid);
                    }
                    before = word.Gender ?? "";
                    break;
                case ReviewFieldNames.RegionalNote:
                    before = word.RegionNotes ?? "";
                    break;
                default:
                {
                    if (edit.SenseOrder is not { } order || order < 0 || order >= snapshotSenses.Count)
                    {
                        return new(ReviewResults.Invalid);
                    }
                    sense = senses.SingleOrDefault(candidate => candidate.SenseOrder == order)
                        ?? throw new InvalidOperationException(
                            "Missing sense " + order.ToString(CultureInfo.InvariantCulture) + " for word: " + word.Key + ".");
                    source = snapshotSenses[order]?.AsObject()
                        ?? throw new InvalidOperationException("Invalid sense snapshot for word: " + word.Key + ".");
                    if (edit.Field == ReviewFieldNames.Gloss)
                    {
                        before = sense.EnGloss;
                    }
                    else if (edit.Field == ReviewFieldNames.Example)
                    {
                        before = sense.ExampleEs;
                        // The sheet has no English field for an example, so the old translation is
                        // kept on the undo row (word_alternatives.gloss) instead of being left to
                        // describe the sentence the edit replaces.
                        previousGloss = sense.ExampleEn;
                    }
                    else
                    {
                        accepted = SplitAccepted(after);
                        if (accepted.Count == 0)
                        {
                            return new(ReviewResults.Invalid);
                        }
                        before = sense.EsAccepted;
                    }
                    break;
                }
            }
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                // A no-op edit would bump the version (and invalidate learner answers) for nothing.
                return new(ReviewResults.Invalid);
            }
            var candidate = accepted is null ? JsonValue.Create(after)! : JsonSerializer.SerializeToNode(accepted)!;
            if (!ReviewContentValidation.IsValid(edit.Field!, candidate, word.Pos, word.Article))
            {
                return new(ReviewResults.Invalid);
            }
            changes.Add(new EditChange(edit.Field!, edit.SenseOrder, before, after, sense, source, accepted));
            previous.TryAdd((edit.Field!, edit.SenseOrder), (before, previousGloss));
            diff.Add(new JsonObject
            {
                ["field"] = edit.Field,
                ["sense"] = edit.SenseOrder,
                ["before"] = before,
                ["after"] = after
            });
        }
        foreach (var change in changes)
        {
            switch (change.Field)
            {
                case ReviewFieldNames.Lemma:
                    word.Headword = change.After;
                    snapshot["headword"] = change.After;
                    break;
                case ReviewFieldNames.Article:
                    word.Article = change.After;
                    snapshot["article"] = change.After;
                    break;
                case ReviewFieldNames.Gender:
                    word.Gender = change.After;
                    snapshot["gender"] = change.After;
                    break;
                case ReviewFieldNames.RegionalNote:
                    word.RegionNotes = change.After;
                    if (snapshot["region"] is JsonObject region)
                    {
                        region["notes"] = change.After;
                    }
                    break;
                case ReviewFieldNames.Gloss:
                    change.Sense!.EnGloss = change.After;
                    change.Source!["gloss"] = change.After;
                    break;
                case ReviewFieldNames.Example:
                    change.Sense!.ExampleEs = change.After;
                    change.Source!["example"] = change.After;
                    // No English input exists on this path, so the previous translation is cleared
                    // rather than kept: a stale example_en would describe the sentence just
                    // replaced. The undo row carries the old English in `gloss`.
                    change.Sense.ExampleEn = "";
                    change.Source!["exampleGloss"] = "";
                    break;
                default:
                    change.Sense!.EsAccepted = JsonSerializer.Serialize(change.Accepted);
                    change.Source!["acceptedEs"] = JsonSerializer.SerializeToNode(change.Accepted);
                    break;
            }
        }
        var field = request.Edits.Count == 1 ? request.Edits[0]!.Field! : ReviewFieldNames.Whole;
        return await CommitEditedApprovalAsync(database, word, snapshot, previous, diff, field, null,
            actorUserId, nowMs, applyCardEffects, cancellationToken);
    }

    private sealed record EditChange(string Field, int? SenseOrder, string Before, string After,
        Sense? Sense, JsonObject? Source, IReadOnlyList<string>? Accepted);

    private static async Task<ReviewTransitionOutcome> ApproveAlternativeAsync(PalabrasDbContext database,
        Word word, JsonObject snapshot, ReviewTransitionRequest request, int? actorUserId, long nowMs,
        bool applyCardEffects, CancellationToken cancellationToken)
    {
        if (request.AlternativeId is not { } alternativeId || alternativeId <= 0)
        {
            return new(ReviewResults.Invalid);
        }
        var alternative = await database.WordAlternatives.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == alternativeId && candidate.WordId == word.Id,
                cancellationToken);
        if (alternative is null)
        {
            return new(ReviewResults.Invalid);
        }
        var field = ChipFieldFor(alternative.Field, alternative.Kind);
        if (field is null || (request.Field is { } requested && !string.Equals(requested, field, StringComparison.Ordinal)))
        {
            return new(ReviewResults.Invalid);
        }
        var previous = new Dictionary<(string Field, int? SenseOrder), (string Value, string? Gloss)>();
        var diff = new JsonArray();
        // Resolve the target and its current value before mutating anything, so an invalid chip
        // leaves the tracked entity untouched for the rest of the batch.
        var swapsHeadword = field is ReviewFieldNames.Lemma or ReviewFieldNames.RegionalVariant
            or ReviewFieldNames.RegisterVariant;
        Sense? sense = null;
        JsonObject? source = null;
        string before;
        // An example swap changes the English sentence too, so the old one is kept on the undo row
        // (word_alternatives.gloss) or undo could only restore half of the pair.
        string? beforeGloss = null;
        if (swapsHeadword)
        {
            before = word.Headword;
        }
        else
        {
            if (request.SenseOrder is not { } order || order < 0)
            {
                return new(ReviewResults.Invalid);
            }
            var senses = await LoadSensesAsync(database, word.Id, cancellationToken);
            sense = senses.SingleOrDefault(candidate => candidate.SenseOrder == order)
                ?? throw new InvalidOperationException(
                    "Missing sense " + order.ToString(CultureInfo.InvariantCulture) + " for word: " + word.Key + ".");
            var snapshotSenses = Senses(snapshot, word.Key);
            if (order >= snapshotSenses.Count)
            {
                return new(ReviewResults.Invalid);
            }
            source = snapshotSenses[order]?.AsObject()
                ?? throw new InvalidOperationException("Invalid sense snapshot for word: " + word.Key + ".");
            before = field == ReviewFieldNames.Gloss ? sense.EnGloss : sense.ExampleEs;
            beforeGloss = field == ReviewFieldNames.Example ? sense.ExampleEn : null;
        }
        if (string.Equals(before, alternative.Value, StringComparison.Ordinal))
        {
            return new(ReviewResults.Invalid);
        }
        if (!ReviewContentValidation.IsValid(swapsHeadword ? ReviewFieldNames.Lemma : field,
                JsonValue.Create(alternative.Value)!, word.Pos, word.Article))
        {
            return new(ReviewResults.Invalid);
        }
        if (swapsHeadword)
        {
            word.Headword = alternative.Value;
            snapshot["headword"] = alternative.Value;
        }
        else if (field == ReviewFieldNames.Gloss)
        {
            sense!.EnGloss = alternative.Value;
            source!["gloss"] = alternative.Value;
        }
        else
        {
            sense!.ExampleEs = alternative.Value;
            source!["example"] = alternative.Value;
            // A council example alternative carries its English translation in `gloss` (the bundle's
            // `en`); without this the swap would leave example_en describing the sentence it just
            // replaced. Legacy undo alternatives (`previous_example`) carry no gloss, so their
            // behaviour is unchanged.
            if (!string.IsNullOrEmpty(alternative.Gloss))
            {
                sense.ExampleEn = alternative.Gloss;
                source!["exampleGloss"] = alternative.Gloss;
            }
        }
        previous[(field, request.SenseOrder)] = (before, beforeGloss);
        diff.Add(new JsonObject
        {
            ["field"] = field,
            ["sense"] = request.SenseOrder,
            ["before"] = before,
            ["after"] = alternative.Value
        });
        return await CommitEditedApprovalAsync(database, word, snapshot, previous, diff, field, alternativeId,
            actorUserId, nowMs, applyCardEffects, cancellationToken);
    }

    // approve_edit / approve_alt: version++, edit revision on the new version, then the shared
    // approval core (status + approve revision at the same new version + card effects).
    private static async Task<ReviewTransitionOutcome> CommitEditedApprovalAsync(PalabrasDbContext database,
        Word word, JsonObject snapshot,
        IReadOnlyDictionary<(string Field, int? SenseOrder), (string Value, string? Gloss)> previous,
        JsonArray diff, string field, int? alternativeId, int? actorUserId, long nowMs, bool applyCardEffects,
        CancellationToken cancellationToken)
    {
        word.Version = checked(word.Version + 1);
        word.UpdatedAt = nowMs;
        snapshot["updatedAt"] = Timestamp(nowMs);
        database.WordRevisions.Add(Revision(word, "edit", snapshot, actorUserId, nowMs));
        // Q-M4-2 default: keep the replaced value as an undo alternative. The legacy alternatives
        // shape has no field column, so the field rides in `kind` (`previous_gloss`, ...). For an
        // example, `gloss` also carries the replaced English sentence: undo is another
        // approve_alt on this row, and without it the round trip would restore the Spanish example
        // while leaving the translation that belongs to the swapped one.
        var rank = await database.WordAlternatives.Where(candidate => candidate.WordId == word.Id)
            .Select(candidate => (int?)candidate.Rank).MaxAsync(cancellationToken) ?? -1;
        foreach (var change in previous)
        {
            database.WordAlternatives.Add(new WordAlternative
            {
                WordId = word.Id,
                Kind = "previous_" + change.Key.Field,
                Value = change.Value.Value,
                Gloss = change.Value.Gloss,
                Reason = "Valor anterior",
                Rank = ++rank
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        var approved = await ApproveAsync(database, word, snapshot, actorUserId, nowMs, applyCardEffects,
            contentChanged: true, cancellationToken);
        return approved.Result == ReviewResults.Applied
            ? new(ReviewResults.Applied, word.Version, field, alternativeId, diff.ToJsonString())
            : approved;
    }

    private static async Task<ReviewTransitionOutcome> FlagAsync(PalabrasDbContext database, Word word,
        JsonObject snapshot, string status, string action, int? actorUserId, long nowMs,
        CancellationToken cancellationToken)
    {
        ApplyStatus(snapshot, status, nowMs);
        word.Status = status;
        word.StatusChangedAt = nowMs;
        word.UpdatedAt = nowMs;
        database.WordRevisions.Add(Revision(word, action, snapshot, actorUserId, nowMs));
        await database.SaveChangesAsync(cancellationToken);
        return new(ReviewResults.Applied, word.Version);
    }

    private static async Task<ReviewTransitionOutcome> DemoteAsync(PalabrasDbContext database, Word word,
        JsonObject snapshot, int? actorUserId, long nowMs, bool applyCardEffects,
        CancellationToken cancellationToken)
    {
        ApplyStatus(snapshot, "pending_review", nowMs);
        word.Status = "pending_review";
        // approved_version keeps the last approved version as an audit fact; the status column says
        // the word is pending again, and the reviewer gate reads only status.
        word.StatusChangedAt = nowMs;
        word.UpdatedAt = nowMs;
        database.WordRevisions.Add(Revision(word, "demote", snapshot, actorUserId, nowMs));
        if (applyCardEffects)
        {
            await ContentCardLifecycle.DemoteAsync(database, word.Id, cancellationToken);
        }
        await database.SaveChangesAsync(cancellationToken);
        return new(ReviewResults.Applied, word.Version);
    }

    private static WordRevision Revision(Word word, string action, JsonObject snapshot, int? actorUserId, long nowMs) => new()
    {
        WordId = word.Id,
        Version = word.Version,
        ActorUserId = actorUserId,
        Action = action,
        SnapshotJson = snapshot.ToJsonString(),
        CreatedAt = nowMs
    };

    private static void ApplyStatus(JsonObject snapshot, string status, long nowMs)
    {
        snapshot["status"] = status;
        snapshot["updatedAt"] = Timestamp(nowMs);
    }

    private static string Timestamp(long nowMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToString("O", CultureInfo.InvariantCulture);

    private static Task<List<Sense>> LoadSensesAsync(PalabrasDbContext database, int wordId,
        CancellationToken cancellationToken) =>
        database.Senses.Where(sense => sense.WordId == wordId).ToListAsync(cancellationToken);

    private static JsonArray Senses(JsonObject snapshot, string key) =>
        snapshot["senses"]?.AsArray()
        ?? throw new InvalidOperationException("Missing senses in content snapshot for word: " + key + ".");

    // The sheet edits accepted answers as a comma-separated list, the same form the importer stores
    // when the file omits `acceptedEs` (gloss split on ", ").
    private static IReadOnlyList<string> SplitAccepted(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
