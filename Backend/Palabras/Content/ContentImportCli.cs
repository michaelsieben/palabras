using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content;

internal static class ContentImportCli
{
    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var (directory, dryRun) = args switch
            {
                ["import", var path] when !path.StartsWith("--", StringComparison.Ordinal) => (path, false),
                ["import", var path, "--dry-run"] when !path.StartsWith("--", StringComparison.Ordinal) => (path, true),
                _ => throw new ArgumentException("Usage: import <dir> [--dry-run]")
            };
            var result = await new ContentImporter(settings).ImportAsync(directory, dryRun, cancellationToken);
            await output.WriteLineAsync($"{(dryRun ? "Dry run: " : "")}created={result.Created} updated={result.Updated} unchanged={result.Unchanged} rejected={result.Rejected.Count}");
            foreach (var rejection in result.Rejected)
            {
                await error.WriteLineAsync(rejection);
            }
            return result.Rejected.Count == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or DbUpdateException or SqliteException)
        {
            await error.WriteLineAsync(exception is DbUpdateException or SqliteException
                ? "Error: Database operation failed; import transaction rolled back."
                : "Error: " + exception.Message);
            return 1;
        }
    }
}
