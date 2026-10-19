using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Content.Council;

// `import-council <dir> [--dry-run] [--force] [--hold]` (plan §3.2). `<dir>` is one bundle or a
// parent directory of bundles; the same scan rule the startup sync uses. Exit codes stay on the
// repo's single 0/1 convention: 0 = everything imported, already imported or unchanged; 1 = any
// refusal, validation error, hash mismatch or database failure.
internal static class CouncilImportCli
{
    private const string Usage = "Usage: import-council <dir> [--dry-run] [--force] [--hold]";

    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var (directory, options) = ParseArguments(args);
            var bundles = FindBundles(directory);
            var importer = new CouncilImporter(settings);
            var imported = 0;
            var created = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var bundle in bundles)
            {
                var result = await importer.ImportAsync(bundle, options, cancellationToken);
                var label = string.IsNullOrEmpty(result.RunId) ? Path.GetFileName(bundle) : result.RunId;
                if (result.Refused)
                {
                    failed++;
                    foreach (var refusal in result.Refusals)
                    {
                        await error.WriteLineAsync("Error: " + label + ": " + refusal);
                    }
                    continue;
                }
                imported += result.Imported ? 1 : 0;
                created += result.Created;
                skipped += result.Unchanged + result.SkippedExisting + result.SkippedReviewerEditNewer;
                await output.WriteLineAsync(Prefix(options.DryRun) + label + " mode=" + result.Mode
                    + " created=" + Count(result.Created)
                    + " updated=" + Count(result.Updated)
                    + " unchanged=" + Count(result.Unchanged)
                    + " skipped_existing=" + Count(result.SkippedExisting)
                    + " skipped_reviewer_edit_newer=" + Count(result.SkippedReviewerEditNewer)
                    + (result.AlreadyImported ? " already imported" : ""));
            }
            await output.WriteLineAsync(
                Prefix(options.DryRun)
                + "bundles=" + Count(bundles.Count)
                + " imported=" + Count(imported)
                + " created=" + Count(created)
                + " skipped=" + Count(skipped)
                + " failed=" + Count(failed));
            return failed == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or DbUpdateException or SqliteException)
        {
            // SQLite/EF detail stays out of the operator-facing line, exactly like `import`.
            await error.WriteLineAsync(exception is DbUpdateException or SqliteException
                ? "Error: Database operation failed; council import transaction rolled back."
                : "Error: " + exception.Message);
            return 1;
        }
    }

    private static (string Directory, CouncilImportOptions Options) ParseArguments(string[] args)
    {
        string? directory = null;
        var dryRun = false;
        var force = false;
        var hold = false;
        foreach (var argument in args.Skip(1))
        {
            switch (argument)
            {
                case "--dry-run":
                    if (dryRun)
                    {
                        throw new ArgumentException(Usage);
                    }
                    dryRun = true;
                    break;
                case "--force":
                    if (force)
                    {
                        throw new ArgumentException(Usage);
                    }
                    force = true;
                    break;
                case "--hold":
                    if (hold)
                    {
                        throw new ArgumentException(Usage);
                    }
                    hold = true;
                    break;
                default:
                    // A second positional argument, or anything that looks like a flag, is a typo:
                    // importing the wrong bundle is not recoverable by re-running with a fix.
                    if (directory is not null || argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException(Usage);
                    }
                    directory = argument;
                    break;
            }
        }
        if (directory is null)
        {
            throw new ArgumentException(Usage);
        }
        if (!System.IO.Directory.Exists(directory))
        {
            throw new ArgumentException("Bundle directory does not exist: " + directory);
        }
        return (directory, new CouncilImportOptions(DryRun: dryRun, Force: force, Hold: hold));
    }

    // Same discovery rule as ContentSyncService: a bundle is a directory with manifest.json, so a
    // parent holding one broken child still imports its siblings (each is refused on its own line).
    private static IReadOnlyList<string> FindBundles(string directory)
    {
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            return [directory];
        }
        var bundles = System.IO.Directory.GetDirectories(directory)
            .Where(child => File.Exists(Path.Combine(child, "manifest.json")))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (bundles.Length == 0)
        {
            throw new ArgumentException(
                "No council bundles found in " + directory + ": expected manifest.json or a parent with */manifest.json");
        }
        return bundles;
    }

    private static string Prefix(bool dryRun) => dryRun ? "Dry run: " : "";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
