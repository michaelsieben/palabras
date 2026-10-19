using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Learn;
using Palabras.Persistence;

namespace Palabras.Content;

// Host/operator-only CLI, like `user`; never exposed through an HTTP endpoint.
internal static class ContentCli
{
    private const string Usage = "Usage: content approve <key…> | content approve --all-pending | content approve --top N [--dry-run] | content reject <key…> | content retire <key…> | content status";
    private static readonly string[] Statuses = ["draft", "pending_review", "approved", "needs_change", "rejected", "retired"];

    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            if (args is ["content", "reject", .. var rejectKeys] && rejectKeys.Length > 0
                && rejectKeys.All(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal)))
            {
                await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
                await using var rejectionDatabase = settings.CreateContext();
                var rejected = await ContentRejection.RejectAsync(rejectionDatabase,
                    rejectKeys.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
                await output.WriteLineAsync($"rejected={rejected.Rejected} unchanged={rejected.Unchanged}");
                return 0;
            }
            if (args is ["content", "retire", .. var retireKeys] && retireKeys.Length > 0
                && retireKeys.All(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal)))
            {
                await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
                await using var retirementDatabase = settings.CreateContext();
                var retired = await ContentRetirement.RetireAsync(retirementDatabase,
                    retireKeys.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
                await output.WriteLineAsync($"retired={retired}");
                return 0;
            }
            (bool Status, bool AllPending, string[] Keys, int? Top, bool DryRun) command = args switch
            {
                ["content", "status"] => (true, false, Array.Empty<string>(), null, false),
                ["content", "approve", "--all-pending"] => (false, true, Array.Empty<string>(), null, false),
                ["content", "approve", "--top", var rawTop]
                    => (false, false, Array.Empty<string>(), ParseTop(rawTop), false),
                ["content", "approve", "--top", var rawTop, "--dry-run"]
                    => (false, false, Array.Empty<string>(), ParseTop(rawTop), true),
                ["content", "approve", .. var values] when values.Length > 0
                    && values.All(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal))
                    => (false, false, values.Distinct(StringComparer.Ordinal).ToArray(), null, false),
                _ => throw new ArgumentException(Usage)
            };
            await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
            await using var database = settings.CreateContext();
            if (command.Top is { } top)
            {
                // Plan §5 approval policy: the first N words in rank order, pending only. Re-running
                // after a success therefore moves to the next N pending words instead of duplicating
                // work, and every approved word still flows through the shipped ContentApproval
                // transaction (revision + card pass), never a direct status write.
                var ranked = await database.Words.AsNoTracking()
                    .Where(word => word.Status == "pending_review")
                    .OrderBy(word => word.Cefr)
                    .ThenBy(word => word.Difficulty ?? LearnQueries.UnrankedDifficulty)
                    .ThenBy(word => word.Id)
                    .Take(top)
                    .Select(word => word.Key)
                    .ToListAsync(cancellationToken);
                if (command.DryRun)
                {
                    foreach (var key in ranked)
                    {
                        await output.WriteLineAsync(key);
                    }
                    await output.WriteLineAsync($"dry-run={ranked.Count}");
                    return 0;
                }
                var topResult = await ContentApproval.ApproveAsync(database, ranked, false, cancellationToken);
                await output.WriteLineAsync($"approved={topResult.Approved} unchanged={topResult.Unchanged}");
                return 0;
            }
            if (command.Status)
            {
                var counts = await database.Words.AsNoTracking().GroupBy(word => word.Status)
                    .Select(group => new { Status = group.Key, Count = group.Count() })
                    .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);
                foreach (var value in Statuses)
                {
                    await output.WriteLineAsync($"{value}={counts.GetValueOrDefault(value)}");
                }
                await output.WriteLineAsync($"total={counts.Values.Sum()}");
                return 0;
            }
            var result = await ContentApproval.ApproveAsync(database, command.Keys, command.AllPending, cancellationToken);
            await output.WriteLineAsync($"approved={result.Approved} unchanged={result.Unchanged}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or DbUpdateException or SqliteException)
        {
            await error.WriteLineAsync(exception is DbUpdateException or SqliteException
                ? args is ["content", "retire", ..]
                    ? "Error: Database operation failed; retirement transaction rolled back."
                    : "Error: Database operation failed; approval transaction rolled back."
                : "Error: " + exception.Message);
            return 1;
        }
    }

    private static int ParseTop(string raw)
    {
        // --top n is "the first n ranked words"; zero is a typo, not "approve nothing".
        if (!int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new ArgumentException($"--top must be a whole number of at least 1 (given '{raw}'). {Usage}");
        }
        return value;
    }
}
