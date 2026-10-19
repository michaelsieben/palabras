using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Accounts;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Learn;

// Host/operator-only CLI, like `user` and `content`; never exposed through an HTTP endpoint.
// M5 ships no HTTP writer for user_settings, so this is the only way to move the new-words-per-day
// cap that PlanComposer clamps every request against (`?maxNew=` can only lower it), and the
// only way to run the plan §3 fallback that marks the probable-known prefix as already known.
internal static class LearnCli
{
    private const string Usage =
        "Usage: learn settings --username <name> [--new-per-day N] [--max-reviews-per-day N] [--backlog-threshold N] | " +
        "learn seed-known --username <name> --top N [--due-days N] [--dry-run] [--reset]";

    // Ranges are deliberately generous but bounded: a typo must fail here, not silently queue
    // thousands of introductions or suppress every review for a month.
    private const int MaximumNewPerDay = 1000;
    private const int MaximumReviewsPerDay = 10000;
    private const int MaximumBacklogThreshold = 100000;
    private const int MaximumDueDays = 3650;
    private const int SeedDueDays = 30;

    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            // Parse before opening the DB: a malformed verb has no side effects, so a throwaway
            // database stays absent and a typo cannot create a half-written settings row.
            var command = Parse(args);
            await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
            await using var database = settings.CreateContext();
            var user = await RequireUserAsync(database, command.Username, cancellationToken);
            return command.Verb == "settings"
                ? await SettingsAsync(database, user, command, output, cancellationToken)
                : await SeedKnownAsync(database, user, command, output, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or DbUpdateException or SqliteException)
        {
            // Never echo raw EF/SQLite errors: they can carry parameter values.
            await error.WriteLineAsync(exception is DbUpdateException or SqliteException
                ? "Error: Database operation failed; no changes were committed."
                : "Error: " + exception.Message);
            return 1;
        }
    }

    // Upsert, not insert: no code path creates the row today (LearnEndpoints.LoadSettingsAsync
    // falls back to the in-memory defaults), so the first write must create it. The printed
    // `old -> new` values are the effective ones, i.e. the defaults when the row is absent.
    private static async Task<int> SettingsAsync(PalabrasDbContext database, User user, Command command,
        TextWriter output, CancellationToken cancellationToken)
    {
        var stored = await database.UserSettings.SingleOrDefaultAsync(row => row.UserId == user.Id, cancellationToken);
        var defaults = new UserSettings { UserId = user.Id };
        var oldNewPerDay = stored?.NewPerDay ?? defaults.NewPerDay;
        var oldMaxReviewsPerDay = stored?.MaxReviewsPerDay ?? defaults.MaxReviewsPerDay;
        var oldBacklogThreshold = stored?.BacklogThreshold ?? defaults.BacklogThreshold;
        var changed = command.NewPerDay is not null
            || command.MaxReviewsPerDay is not null
            || command.BacklogThreshold is not null;
        if (changed)
        {
            var row = stored ?? defaults;
            if (stored is null)
            {
                database.UserSettings.Add(row);
            }
            row.NewPerDay = command.NewPerDay ?? row.NewPerDay;
            row.MaxReviewsPerDay = command.MaxReviewsPerDay ?? row.MaxReviewsPerDay;
            row.BacklogThreshold = command.BacklogThreshold ?? row.BacklogThreshold;
            await database.SaveChangesAsync(cancellationToken);
        }
        var newPerDay = command.NewPerDay ?? oldNewPerDay;
        var maxReviewsPerDay = command.MaxReviewsPerDay ?? oldMaxReviewsPerDay;
        var backlogThreshold = command.BacklogThreshold ?? oldBacklogThreshold;
        await output.WriteLineAsync("user=" + user.Username);
        await output.WriteLineAsync(changed
            ? $"new_per_day={oldNewPerDay} -> {newPerDay}"
            : $"new_per_day={newPerDay}");
        await output.WriteLineAsync(changed
            ? $"max_reviews_per_day={oldMaxReviewsPerDay} -> {maxReviewsPerDay}"
            : $"max_reviews_per_day={maxReviewsPerDay}");
        await output.WriteLineAsync(changed
            ? $"backlog_threshold={oldBacklogThreshold} -> {backlogThreshold}"
            : $"backlog_threshold={backlogThreshold}");
        return 0;
    }

    // Plan §3 fallback: the ranked prefix is probably already known, so give those words existing
    // Review state instead of teaching them. Only approved words can have cards, and only the
    // user's cards are touched, so this can never create eligibility. `introduced_local_date`
    // stays NULL: a seeded card is not an introduction and must not spend today's new budget.
    private static async Task<int> SeedKnownAsync(PalabrasDbContext database, User user, Command command,
        TextWriter output, CancellationToken cancellationToken)
    {
        var words = await database.Words.AsNoTracking()
            .Where(word => word.Status == "approved")
            .OrderBy(word => word.Cefr)
            .ThenBy(word => word.Difficulty ?? LearnQueries.UnrankedDifficulty)
            .ThenBy(word => word.Id)
            .Take(command.Top!.Value)
            .Select(word => new { word.Id, word.Key })
            .ToListAsync(cancellationToken);
        if (command.DryRun)
        {
            foreach (var word in words)
            {
                await output.WriteLineAsync(word.Key);
            }
            await output.WriteLineAsync($"dry-run={words.Count}");
            return 0;
        }
        var wordIds = words.Select(word => word.Id).ToArray();
        List<Card> cards = [];
        if (wordIds.Length > 0)
        {
            cards = await database.Cards.Where(card => card.UserId == user.Id
                    && database.Senses.Any(sense => sense.Id == card.SenseId && wordIds.Contains(sense.WordId)))
                .ToListAsync(cancellationToken);
        }
        var now = TimeProvider.System.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var dueMs = now.AddDays(command.DueDays).ToUnixTimeMilliseconds();
        foreach (var card in cards)
        {
            // A suspended card keeps its reason: seeding must not resurrect content the learner
            // or the content pipeline paused. It only changes the SRS state of live cards.
            card.Step = null;
            card.IntroducedLocalDate = null;
            if (command.Reset)
            {
                card.State = CardState.New;
                card.Stability = null;
                card.Reps = 0;
                card.LastReviewMs = null;
                card.DueMs = nowMs;
            }
            else
            {
                card.State = CardState.Review;
                card.Stability = SeedDueDays;
                card.Reps = 1;
                card.LastReviewMs = nowMs;
                card.DueMs = dueMs;
            }
        }
        if (cards.Count > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        await output.WriteLineAsync(command.Reset
            ? $"reset_words={words.Count} reset_cards={cards.Count}"
            : $"seeded_words={words.Count} seeded_cards={cards.Count}");
        return 0;
    }

    private static async Task<User> RequireUserAsync(PalabrasDbContext database, string username,
        CancellationToken cancellationToken)
    {
        var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Username == username,
            cancellationToken);
        return user ?? throw new ArgumentException(
            $"Account not found: {username}. Use 'user create <owner|reviewer> --username {username}' first.");
    }

    private static Command Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "learn")
        {
            throw new ArgumentException(Usage);
        }
        return args[1] switch
        {
            "settings" => ParseSettings(args),
            "seed-known" => ParseSeedKnown(args),
            _ => throw new ArgumentException(Usage)
        };
    }

    private static Command ParseSettings(string[] args)
    {
        string? username = null;
        int? newPerDay = null;
        int? maxReviewsPerDay = null;
        int? backlogThreshold = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var flag = args[index];
            RequireFirstOccurrence(seen, flag);
            switch (flag)
            {
                case "--username":
                    username = Value(args, ref index, flag);
                    break;
                case "--new-per-day":
                    newPerDay = Count(args, ref index, flag, 0, MaximumNewPerDay);
                    break;
                case "--max-reviews-per-day":
                    maxReviewsPerDay = Count(args, ref index, flag, 0, MaximumReviewsPerDay);
                    break;
                case "--backlog-threshold":
                    backlogThreshold = Count(args, ref index, flag, 0, MaximumBacklogThreshold);
                    break;
                default:
                    throw new ArgumentException("Unknown option " + flag + ". " + Usage);
            }
        }
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("--username is required. " + Usage);
        }
        return new Command("settings", AccountService.NormalizeUsername(username), newPerDay, maxReviewsPerDay,
            backlogThreshold);
    }

    private static Command ParseSeedKnown(string[] args)
    {
        string? username = null;
        int? top = null;
        var dueDays = SeedDueDays;
        var dryRun = false;
        var reset = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var flag = args[index];
            RequireFirstOccurrence(seen, flag);
            switch (flag)
            {
                case "--username":
                    username = Value(args, ref index, flag);
                    break;
                case "--top":
                    // Zero words is not a request; --top 0 is a typo, not "seed nothing".
                    top = Count(args, ref index, flag, 1, MaximumBacklogThreshold);
                    break;
                case "--due-days":
                    dueDays = Count(args, ref index, flag, 1, MaximumDueDays);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--reset":
                    reset = true;
                    break;
                default:
                    throw new ArgumentException("Unknown option " + flag + ". " + Usage);
            }
        }
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("--username is required. " + Usage);
        }
        if (top is null)
        {
            throw new ArgumentException("--top is required. " + Usage);
        }
        return new Command("seed-known", AccountService.NormalizeUsername(username), Top: top, DueDays: dueDays,
            DryRun: dryRun, Reset: reset);
    }

    private static void RequireFirstOccurrence(HashSet<string> seen, string flag)
    {
        if (!seen.Add(flag))
        {
            throw new ArgumentException("Option " + flag + " is given more than once. " + Usage);
        }
    }

    private static string Value(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException("Option " + flag + " needs a value. " + Usage);
        }
        index++;
        return args[index];
    }

    private static int Count(string[] args, ref int index, string flag, int minimum, int maximum)
    {
        var raw = Value(args, ref index, flag);
        // NumberStyles.None rejects signs and separators, so "-1", "+5" and "1,000" all fail.
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
        {
            throw new ArgumentException(
                $"{flag} must be a whole number from {minimum} to {maximum} (given '{raw}').");
        }
        return value;
    }

    private sealed record Command(string Verb, string Username, int? NewPerDay = null,
        int? MaxReviewsPerDay = null, int? BacklogThreshold = null, int? Top = null,
        int DueDays = SeedDueDays, bool DryRun = false, bool Reset = false);
}
