using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content;

// Host/operator-only CLI, like `user`; never exposed through an HTTP endpoint.
internal static class ContentCli
{
    private const string Usage = "Usage: content approve <key…> | content approve --all-pending | content retire <key…> | content status";
    private static readonly string[] Statuses = ["draft", "pending_review", "approved", "needs_change", "rejected", "retired"];

    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
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
            var (status, allPending, keys) = args switch
            {
                ["content", "status"] => (true, false, Array.Empty<string>()),
                ["content", "approve", "--all-pending"] => (false, true, Array.Empty<string>()),
                ["content", "approve", .. var values] when values.Length > 0
                    && values.All(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal))
                    => (false, false, values.Distinct(StringComparer.Ordinal).ToArray()),
                _ => throw new ArgumentException(Usage)
            };
            await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
            await using var database = settings.CreateContext();
            if (status)
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
            var result = await ContentApproval.ApproveAsync(database, keys, allPending, cancellationToken);
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
}
