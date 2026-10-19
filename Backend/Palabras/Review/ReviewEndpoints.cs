using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Learn;
using Palabras.Persistence;

namespace Palabras.Review;

// M4 reviewer gate over word_reviews (plan §2.4). Reviewer-only: the owner account deliberately has
// no reviewer role, so her verdicts stay hers (Q-M4-6). Every route answers the API error contract.
internal static class ReviewEndpoints
{
    private const string Policy = "reviewer";
    private const int DefaultQueueLimit = 30;
    private const int MaximumQueueLimit = 30;
    private const int DefaultApprovedLimit = 20;
    private const int MaximumApprovedLimit = 50;
    private const int MaximumActionsPerBatch = 50;
    private const int MaximumBatchApprovals = 25;
    private const int MaximumDecisionMs = 600_000;

    internal static void MapReviewEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/review").RequireAuthorization(Policy);
        group.MapGet("/queue", QueueAsync);
        group.MapPost("/actions", ActionsAsync);
        group.MapGet("/approved", ApprovedAsync);
    }

    // E.4 ordering: topic with most pending first; inside a topic council_agreement DESC NULLS LAST,
    // then key. The corpus is hundreds of words, so the ordering is computed from loaded rows rather
    // than pushed into SQLite (no proposal_support/frequency_rank column exists in M4).
    //
    // Trust-by-default (T-20261019-2100): `pending` is the default scope, so a caller that has not
    // adopted the new field gets exactly the pre-change page. The objection UI opts into approved
    // rows explicitly with `status=all` (or `status=approved`); rendering an approved row as a
    // pending card would offer "approve" for a word that is already approved.
    //
    // Objection discoverability (T-20261019-2100 step 36): `rejected` is a scope of its own and
    // `all` is the union of all three statuses, so a withdrawn word never becomes unreachable. A
    // rejected row is a full ReviewItem (wordId, version, status), which is exactly what `approve`
    // needs to lift the objection. The three scopes stay mutually exclusive.
    //
    // Decided rows (approved + rejected) share one ordering: newest decision first, keyset-paged
    // by word id through `after`/`nextAfter`. The cursor addresses the decided list of the
    // selected scope, so `status=approved`, `status=rejected` and the decided part of `all` page
    // the same way.
    private static async Task<IResult> QueueAsync(DatabaseSettings settings, string? limit, string? status,
        string? after, CancellationToken cancellationToken)
    {
        if (!TryLimit(limit, DefaultQueueLimit, MaximumQueueLimit, out var take))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        if (!TryQueueScope(status, out var includePending, out var includeApproved, out var includeRejected))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        int? cursor = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            }
            cursor = parsed;
        }
        await using var database = settings.CreateContext();
        var pending = includePending
            ? await LoadPendingQueueAsync(database, take, cancellationToken)
            : new PendingQueue([], 0, []);
        var decided = includeApproved || includeRejected
            ? await LoadDecidedQueueAsync(database, includeApproved, includeRejected,
                take - pending.Words.Count, cursor, cancellationToken)
            : new DecidedQueue([], 0, 0, null, false);
        if (decided.InvalidCursor)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        var pageWords = pending.Words.Concat(decided.Words).ToArray();
        var items = pageWords.Length == 0
            ? []
            : await BuildQueueItemsAsync(database, pageWords, cancellationToken);
        return Results.Ok(new ReviewQueueResponse(items, pending.Total, pending.Topics,
            decided.ApprovedTotal, decided.NextAfter, decided.RejectedTotal));
    }

    private sealed record PendingQueue(IReadOnlyList<Word> Words, int Total,
        IReadOnlyList<ReviewTopicCount> Topics);

    private sealed record DecidedQueue(IReadOnlyList<Word> Words, int ApprovedTotal, int RejectedTotal,
        int? NextAfter, bool InvalidCursor);

    private static async Task<PendingQueue> LoadPendingQueueAsync(PalabrasDbContext database, int take,
        CancellationToken cancellationToken)
    {
        var pending = await database.Words.AsNoTracking()
            .Where(word => word.Status == "pending_review").ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return new PendingQueue([], 0, []);
        }
        var wordIds = pending.Select(word => word.Id).ToArray();
        var snapshots = await ReviewTransitions.LoadCurrentSnapshotsAsync(database, wordIds, cancellationToken);
        var fallbackTopics = await LoadTopicsAsync(database, wordIds, cancellationToken);
        var topicByWord = pending.ToDictionary(word => word.Id,
            word => FirstTopic(snapshots.GetValueOrDefault(word.Id), fallbackTopics.GetValueOrDefault(word.Id)));
        var topicCounts = topicByWord.Values.Where(topic => topic is not null)
            .GroupBy(topic => topic!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ordered = pending
            .OrderByDescending(word => topicCounts.GetValueOrDefault(topicByWord[word.Id] ?? string.Empty))
            .ThenByDescending(word => word.CouncilAgreement ?? double.NegativeInfinity)
            .ThenBy(word => word.Key, StringComparer.Ordinal)
            .Take(take)
            .ToArray();
        var topics = topicCounts.OrderByDescending(count => count.Value)
            .ThenBy(count => count.Key, StringComparer.Ordinal)
            .Select(count => new ReviewTopicCount(count.Key, count.Value)).ToArray();
        return new PendingQueue(ordered, pending.Count, topics);
    }

    // Decided rows (approved and rejected) are ordered by the same "decision time" rule
    // /api/review/approved uses, then keyset paged by word id. status_changed_at is authoritative;
    // pre-M4 approvals fall back to the newest approve revision, then updated_at/created_at. The
    // cursor is resolved against the full ordered list, so a non-existent id is a bad_request
    // rather than a silently empty page.
    private static async Task<DecidedQueue> LoadDecidedQueueAsync(PalabrasDbContext database,
        bool includeApproved, bool includeRejected, int take, int? cursor, CancellationToken cancellationToken)
    {
        var statuses = new List<string>(2);
        if (includeApproved)
        {
            statuses.Add("approved");
        }
        if (includeRejected)
        {
            statuses.Add("rejected");
        }
        var words = await database.Words.AsNoTracking()
            .Where(word => statuses.Contains(word.Status)).ToListAsync(cancellationToken);
        var approvedTotal = words.Count(word => word.Status == "approved");
        var rejectedTotal = words.Count(word => word.Status == "rejected");
        if (words.Count == 0)
        {
            return new DecidedQueue([], 0, 0, null, false);
        }
        var wordIds = words.Select(word => word.Id).ToArray();
        var decisions = await database.WordRevisions.AsNoTracking()
            .Where(revision => wordIds.Contains(revision.WordId)
                && (revision.Action == "approve" || revision.Action == "reject"))
            .OrderByDescending(revision => revision.Id)
            .Select(revision => new { revision.WordId, revision.CreatedAt })
            .ToListAsync(cancellationToken);
        var lastDecision = new Dictionary<int, long>();
        foreach (var decision in decisions)
        {
            lastDecision.TryAdd(decision.WordId, decision.CreatedAt);
        }
        var ordered = words
            .Select(word => new
            {
                Word = word,
                DecidedAt = word.StatusChangedAt
                    ?? (lastDecision.TryGetValue(word.Id, out var decisionMs) ? decisionMs : (long?)null)
                    ?? (word.UpdatedAt != 0 ? word.UpdatedAt : word.CreatedAt)
            })
            .OrderByDescending(row => row.DecidedAt)
            .ThenByDescending(row => row.Word.Id)
            .ToList();
        var start = 0;
        if (cursor is { } afterId)
        {
            var index = ordered.FindIndex(row => row.Word.Id == afterId);
            if (index < 0)
            {
                return new DecidedQueue([], approvedTotal, rejectedTotal, null, true);
            }
            start = index + 1;
        }
        var remaining = ordered.Skip(start).ToList();
        var page = remaining.Take(Math.Max(take, 0)).Select(row => row.Word).ToArray();
        var nextAfter = take > 0 && remaining.Count > take ? page[^1].Id : (int?)null;
        return new DecidedQueue(page, approvedTotal, rejectedTotal, nextAfter, false);
    }

    private static async Task<ReviewItem[]> BuildQueueItemsAsync(PalabrasDbContext database,
        IReadOnlyList<Word> pageWords, CancellationToken cancellationToken)
    {
        var pageIds = pageWords.Select(word => word.Id).ToArray();
        var snapshots = await ReviewTransitions.LoadCurrentSnapshotsAsync(database, pageIds, cancellationToken);
        var fallbackTopics = await LoadTopicsAsync(database, pageIds, cancellationToken);
        var senses = await database.Senses.AsNoTracking().Where(sense => pageIds.Contains(sense.WordId))
            .OrderBy(sense => sense.WordId).ThenBy(sense => sense.SenseOrder)
            .Select(sense => new { sense.WordId, sense.SenseOrder, sense.EnGloss, sense.EnAccepted,
                sense.EsAccepted, sense.ExampleEs, sense.ExampleEn, sense.Notes })
            .ToListAsync(cancellationToken);
        var alternatives = await database.WordAlternatives.AsNoTracking()
            .Where(alternative => pageIds.Contains(alternative.WordId))
            .OrderBy(alternative => alternative.Rank).ThenBy(alternative => alternative.Id)
            .ToListAsync(cancellationToken);
        // The chip support denominator is the run's requested roster (plan §2.5, T-12): one query
        // for the runs behind this page, not one per word. A word without a run (legacy corpus row)
        // keeps support=null, exactly as before M3b.
        var runIds = pageWords.Where(word => word.CouncilRunId is not null)
            .Select(word => word.CouncilRunId!.Value).Distinct().ToArray();
        var modelsRequested = runIds.Length == 0
            ? new Dictionary<int, int>()
            : await database.CouncilRuns.AsNoTracking().Where(run => runIds.Contains(run.Id))
                .ToDictionaryAsync(run => run.Id, run => run.ModelsRequested, cancellationToken);
        // "Cambiar ⚑" leaves a note so a reworked entry shows the reviewer's last flag again.
        var flags = await database.WordReviews.AsNoTracking()
            .Where(review => pageIds.Contains(review.WordId) && review.Action == "needs_change")
            .OrderByDescending(review => review.Id)
            .Select(review => new { review.WordId, review.Note, review.CreatedAt })
            .ToListAsync(cancellationToken);
        var flagByWord = new Dictionary<int, ReviewLastFlag>();
        foreach (var flag in flags)
        {
            flagByWord.TryAdd(flag.WordId,
                new ReviewLastFlag("needs_change", flag.Note, DateTimeOffset.FromUnixTimeMilliseconds(flag.CreatedAt)));
        }
        return pageWords.Select(word =>
        {
            var snapshot = snapshots.GetValueOrDefault(word.Id);
            var snapshotSenseCount = snapshot?["senses"]?.AsArray().Count;
            var wordSenses = senses
                .Where(sense => sense.WordId == word.Id
                    && (snapshotSenseCount is null || sense.SenseOrder < snapshotSenseCount))
                .OrderBy(sense => sense.SenseOrder)
                .Select(sense => new ReviewSenseItem(sense.SenseOrder, sense.EnGloss,
                    ParseStringArray(sense.EnAccepted), ParseStringArray(sense.EsAccepted),
                    sense.ExampleEs, sense.ExampleEn, sense.Notes))
                .ToArray();
            var denominator = word.CouncilRunId is { } runId ? modelsRequested.GetValueOrDefault(runId) : 0;
            var chips = alternatives.Where(alternative => alternative.WordId == word.Id
                    && !alternative.Kind.StartsWith("previous_", StringComparison.Ordinal))
                // `field` (M3b) wins over the legacy `kind` vocabulary; the resolver also keeps
                // regional_note out of the chip list on both routes.
                .Select(alternative => ReviewTransitions.ChipFieldFor(alternative.Field, alternative.Kind) is { } field
                    ? new ReviewAlternativeChip(alternative.Id, field, alternative.Value, alternative.Gloss,
                        alternative.Reason,
                        denominator > 0
                            ? alternative.Support.ToString(CultureInfo.InvariantCulture) + "/"
                                + denominator.ToString(CultureInfo.InvariantCulture)
                            : null)
                    : null)
                .Where(chip => chip is not null)
                .Select(chip => chip!)
                .ToArray();
            return new ReviewItem(word.Id, word.Key, word.Version, word.Headword, word.Article, word.Gender,
                word.Pos, word.Cefr, FirstTopic(snapshot, fallbackTopics.GetValueOrDefault(word.Id)), wordSenses,
                word.Notes, word.RegionNotes, word.RegisterNotes, word.CouncilAgreement, word.CouncilBand,
                word.ReducedConfidence != 0, ParseLintWarnings(word.LintWarnings), chips,
                flagByWord.GetValueOrDefault(word.Id), word.Status);
        }).ToArray();
    }

    // One request = one transaction under SQLite's write lock (same pattern as POST /api/learn/attempts):
    // the duplicate check and the verdict insert cannot interleave with a concurrent flush. Per-item
    // outcomes keep a partially usable offline queue, so the batch itself always answers 200.
    private static async Task<IResult> ActionsAsync(ReviewActionsRequest? request, HttpContext context,
        DatabaseSettings settings, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (LearnEndpoints.CurrentUserId(context) is not { } reviewerId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var actions = request?.Actions;
        if (actions is null || actions.Count is < 1 or > MaximumActionsPerBatch
            || actions.Count(action => action?.ViaBatch == true) > MaximumBatchApprovals)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        await using var database = settings.CreateContext();
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)database.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await database.Database.UseTransactionAsync(transaction, cancellationToken);

        var wordIds = actions.Where(action => action is not null && action.WordId > 0)
            .Select(action => action!.WordId).Distinct().ToArray();
        var words = await database.Words.Where(word => wordIds.Contains(word.Id))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
        var clientIds = actions.Where(action => action is not null && Guid.TryParse(action.ClientReviewId, out _))
            .Select(action => Guid.Parse(action!.ClientReviewId!).ToString("D")).Distinct().ToArray();
        var seen = (await database.WordReviews.AsNoTracking()
                .Where(review => clientIds.Contains(review.ClientReviewId))
                .Select(review => review.ClientReviewId).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var now = timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var results = new List<ReviewActionResult>(actions.Count);
        foreach (var raw in actions)
        {
            var echoed = raw?.ClientReviewId ?? "";
            if (!TryNormalize(raw, out var action))
            {
                results.Add(new(echoed, ReviewResults.Invalid, null));
                continue;
            }
            if (seen.Contains(action.ClientReviewId))
            {
                results.Add(new(action.EchoedClientReviewId, ReviewResults.Duplicate, null));
                continue;
            }
            if (!words.TryGetValue(raw!.WordId, out var word))
            {
                results.Add(new(action.EchoedClientReviewId, ReviewResults.Invalid, null));
                continue;
            }
            if (word.Version != action.Request.WordVersion)
            {
                results.Add(new(action.EchoedClientReviewId, ReviewResults.StaleVersion, null));
                continue;
            }
            // Batch approve is only offered for council `alta` rows with no lint warning (plan §2.4).
            if (action.ViaBatch && !BatchEligible(word))
            {
                results.Add(new(action.EchoedClientReviewId, ReviewResults.Invalid, null));
                continue;
            }
            var outcome = await ReviewTransitions.ApplyAsync(database, word, action.Request,
                ReviewTransitions.ReviewerAllowedFrom(action.Request.Action), reviewerId, nowMs,
                applyCardEffects: true, cancellationToken);
            if (outcome.Result != ReviewResults.Applied)
            {
                results.Add(new(action.EchoedClientReviewId, outcome.Result, null));
                continue;
            }
            database.WordReviews.Add(new WordReview
            {
                WordId = word.Id,
                WordVersion = action.Request.WordVersion,
                SenseOrder = action.Request.SenseOrder,
                ReviewerId = reviewerId,
                Action = action.Request.Action,
                Field = outcome.Field
                    ?? (action.Request.Action == ReviewActionNames.NeedsChange ? action.Request.Field : null),
                ChosenAlternativeId = outcome.AlternativeId,
                EditDiff = outcome.EditDiff,
                Note = action.Note,
                ClientReviewId = action.ClientReviewId,
                // via_batch has no 0/1 CHECK in the schema (plan §2.3 omits it); this endpoint is the
                // only writer and always stores exactly 0 or 1.
                ViaBatch = action.ViaBatch ? 1 : 0,
                AgreementAtReview = word.CouncilAgreement,
                DecisionMs = action.DecisionMs,
                CreatedAt = nowMs
            });
            await database.SaveChangesAsync(cancellationToken);
            seen.Add(action.ClientReviewId);
            results.Add(new(action.EchoedClientReviewId, ReviewResults.Applied,
                new ReviewActionWordResult(word.Status, word.Version)));
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new ReviewActionsResponse(results));
    }

    // Aprobadas: searchable on headword/gloss and keyset paged by word id (the cursor in `after`).
    // approvedAt falls back for pre-M4 rows: status_changed_at is NULL there, so the latest approve
    // revision (written by the CLI) and finally words.updated_at/created_at are used. The list never
    // reports a missing value as "approved at the epoch".
    private static async Task<IResult> ApprovedAsync(DatabaseSettings settings, string? q,
        string? after, string? limit, CancellationToken cancellationToken)
    {
        if (!TryLimit(limit, DefaultApprovedLimit, MaximumApprovedLimit, out var take))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        int? cursor = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            }
            cursor = parsed;
        }
        await using var database = settings.CreateContext();
        var words = await database.Words.AsNoTracking()
            .Where(word => word.Status == "approved").ToListAsync(cancellationToken);
        var wordIds = words.Select(word => word.Id).ToArray();
        var snapshots = await ReviewTransitions.LoadCurrentSnapshotsAsync(database, wordIds, cancellationToken);
        var fallbackTopics = await LoadTopicsAsync(database, wordIds, cancellationToken);
        var senses = await database.Senses.AsNoTracking()
            .Where(sense => wordIds.Contains(sense.WordId))
            .OrderBy(sense => sense.WordId).ThenBy(sense => sense.SenseOrder)
            .Select(sense => new { sense.WordId, sense.SenseOrder, sense.EnGloss })
            .ToListAsync(cancellationToken);
        var approvals = await database.WordRevisions.AsNoTracking()
            .Where(revision => wordIds.Contains(revision.WordId) && revision.Action == "approve")
            .OrderByDescending(revision => revision.Id)
            .Select(revision => new { revision.WordId, revision.CreatedAt })
            .ToListAsync(cancellationToken);
        var lastApproval = new Dictionary<int, long>();
        foreach (var approval in approvals)
        {
            lastApproval.TryAdd(approval.WordId, approval.CreatedAt);
        }
        var rows = words.Select(word =>
        {
            var snapshot = snapshots.GetValueOrDefault(word.Id);
            var snapshotSenseCount = snapshot?["senses"]?.AsArray().Count;
            var gloss = senses.FirstOrDefault(sense => sense.WordId == word.Id
                && (snapshotSenseCount is null || sense.SenseOrder < snapshotSenseCount))?.EnGloss ?? "";
            var approvedMs = word.StatusChangedAt
                ?? (lastApproval.TryGetValue(word.Id, out var approvalMs) ? approvalMs : (long?)null)
                ?? (word.UpdatedAt != 0 ? word.UpdatedAt : word.CreatedAt);
            return new ApprovedWordItem(word.Id, word.Key, word.Version, word.Headword, word.Article, gloss,
                FirstTopic(snapshot, fallbackTopics.GetValueOrDefault(word.Id)),
                DateTimeOffset.FromUnixTimeMilliseconds(approvedMs));
        });
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            rows = rows.Where(row => row.Headword.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || row.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || row.Gloss.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        var ordered = rows.OrderByDescending(row => row.ApprovedAt).ThenByDescending(row => row.WordId).ToList();
        if (cursor is { } afterId)
        {
            var index = ordered.FindIndex(row => row.WordId == afterId);
            if (index < 0)
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            }
            ordered = ordered.Skip(index + 1).ToList();
        }
        return Results.Ok(new ApprovedWordsResponse(ordered.Take(take).ToArray()));
    }

    private sealed record NormalizedAction(
        string EchoedClientReviewId,
        string ClientReviewId,
        ReviewTransitionRequest Request,
        bool ViaBatch,
        string? Note,
        int? DecisionMs);

    // Wire-shape validation only. Content/payload rules (chip ownership, sense orders, editable
    // fields) are enforced by ReviewTransitions so the CLI and the HTTP route agree.
    private static bool TryNormalize(ReviewActionRequest? raw, out NormalizedAction action)
    {
        action = null!;
        if (raw is null || !Guid.TryParse(raw.ClientReviewId, out var clientReviewId))
        {
            return false;
        }
        if (raw.WordId <= 0 || raw.WordVersion <= 0 || !ReviewActionNames.IsKnown(raw.Action))
        {
            return false;
        }
        if (raw.SenseOrder is < 0 || (raw.Field is { } field && !ReviewFieldNames.IsKnown(field)))
        {
            return false;
        }
        if ((raw.Action == ReviewActionNames.ApproveAlt) != (raw.AlternativeId is not null))
        {
            return false;
        }
        if (raw.AlternativeId is { } alternativeId && alternativeId <= 0)
        {
            return false;
        }
        switch (raw.Action)
        {
            case ReviewActionNames.ApproveAlt:
                if (raw.Edits is { Count: > 0 })
                {
                    return false;
                }
                break;
            case ReviewActionNames.ApproveEdit:
                if (raw.AlternativeId is not null || raw.Field is not null
                    || raw.Edits is not { Count: > 0 and <= 10 })
                {
                    return false;
                }
                foreach (var edit in raw.Edits)
                {
                    if (edit is null || !ReviewFieldNames.IsEditable(edit.Field) || edit.After is null
                        || edit.SenseOrder is < 0)
                    {
                        return false;
                    }
                }
                break;
            default:
                if (raw.AlternativeId is not null || raw.Edits is { Count: > 0 })
                {
                    return false;
                }
                // Only "Cambiar ⚑" may name the field it is flagging.
                if (raw.Action != ReviewActionNames.NeedsChange && raw.Field is not null)
                {
                    return false;
                }
                break;
        }
        if (raw.ViaBatch == true && raw.Action != ReviewActionNames.Approve)
        {
            return false;
        }
        action = new NormalizedAction(raw.ClientReviewId ?? "", clientReviewId.ToString("D"),
            new ReviewTransitionRequest(raw.WordVersion, raw.Action!, raw.Field, raw.SenseOrder,
                raw.AlternativeId, raw.Edits),
            raw.ViaBatch == true, raw.Note,
            raw.DecisionMs is { } decision ? Math.Clamp(decision, 0, MaximumDecisionMs) : null);
        return true;
    }

    private static bool BatchEligible(Word word) =>
        string.Equals(word.CouncilBand, "alta", StringComparison.Ordinal) && word.LintWarnings is null;

    private static async Task<Dictionary<int, IReadOnlyList<string>>> LoadTopicsAsync(PalabrasDbContext database,
        IReadOnlyList<int> wordIds, CancellationToken cancellationToken)
    {
        if (wordIds.Count == 0)
        {
            return [];
        }
        var rows = await database.WordTopics.AsNoTracking()
            .Where(topic => wordIds.Contains(topic.WordId))
            .OrderBy(topic => topic.Topic)
            .Select(topic => new { topic.WordId, topic.Topic })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.WordId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.Topic).ToArray());
    }

    // Q-M4-7: a word can carry up to three topics; the sheet and the queue group by the first one in
    // the current snapshot (file order), falling back to the alphabetically first word_topics row.
    private static string? FirstTopic(JsonObject? snapshot, IReadOnlyList<string>? fallback)
    {
        if (snapshot?["topics"] is JsonArray topics && topics.Count > 0
            && topics[0] is JsonValue value && value.TryGetValue<string>(out var first))
        {
            return first;
        }
        return fallback is { Count: > 0 } ? fallback[0] : null;
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // lint_warnings is a JSON array in the ΔF shape. If M3b ever stores plain text, the raw value is
    // surfaced as one warning rather than silently dropping the reviewer's red chip.
    private static IReadOnlyList<string> ParseLintWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        var values = ParseStringArray(json);
        return values.Count > 0 ? values : [json];
    }

    private static bool TryQueueScope(string? raw, out bool includePending, out bool includeApproved,
        out bool includeRejected)
    {
        // Default stays `pending` (commit 37e1371): a caller that never adopted `status` keeps the
        // exact pre-change page.
        includePending = true;
        includeApproved = false;
        includeRejected = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }
        switch (raw.Trim())
        {
            case "pending":
                return true;
            case "approved":
                includePending = false;
                includeApproved = true;
                return true;
            case "rejected":
                includePending = false;
                includeRejected = true;
                return true;
            case "all":
                includeApproved = true;
                includeRejected = true;
                return true;
            default:
                return false;
        }
    }

    private static bool TryLimit(string? raw, int defaultValue, int maximum, out int value)
    {
        value = defaultValue;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1 || parsed > maximum)
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static IResult Error(int status, string error) =>
        Results.Json(new { error }, statusCode: status);
}
