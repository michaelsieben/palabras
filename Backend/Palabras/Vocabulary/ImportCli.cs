using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Vocabulary;

internal static class ImportCli
{
    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var entries = new List<(string Path, JsonElement Entry)>();
        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (args.Length != 2 || args[0] != "import") { throw new InvalidDataException("Usage: import <path-or-dir>"); }
            var path = Path.GetFullPath(args[1]);
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal).ToArray() : [path];
            if (files.Length == 0) { throw new InvalidDataException("No JSON files found."); }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) { throw new InvalidDataException("File exceeds 1 MiB limit."); }
                    await using var stream = File.OpenRead(file);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    WordSchema.Validate(document.RootElement);
                    var id = document.RootElement.GetProperty("id").GetString()!;
                    if (Path.GetFileName(file) != id + ".json") { throw new InvalidDataException("Filename must match content id."); }
                    if (!ids.Add(id)) { throw new InvalidDataException("Duplicate content id in batch."); }
                    entries.Add((file, document.RootElement.Clone()));
                    outcomes[file] = "skipped";
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                {
                    outcomes[file] = "failed";
                    await error.WriteLineAsync($"FAIL {Path.GetFileName(file)}: {exception.Message}");
                }
            }
            if (outcomes.Values.Contains("failed")) { return await Finish(output, outcomes, false); }

            // Validate the ENTIRE batch before opening/migrating the DB. One transaction for every upsert.
            await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
            await using var database = settings.CreateContext();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            foreach (var (file, entry) in entries)
            {
                var id = entry.GetProperty("id").GetString()!;
                var word = await database.Words.Include(word => word.Senses).Include(word => word.Alternatives).Include(word => word.Topics)
                    .SingleOrDefaultAsync(word => word.ExternalId == id, cancellationToken);
                // An importer cannot promote previously rejected/draft content by changing its file status.
                if (word != null && word.Status is "draft" or "rejected" && entry.GetProperty("status").GetString() == "approved")
                {
                    outcomes[file] = "failed";
                    await error.WriteLineAsync($"FAIL {Path.GetFileName(file)}: cannot import {word.Status} word as approved; reviewer transition required.");
                    continue;
                }
                if (word != null && word.ContentJson == WordContent.CanonicalJson(entry)) { continue; }
                outcomes[file] = word == null ? "created" : "updated";
                if (word == null) { word = new Word(); database.Words.Add(word); }
                WordContent.Apply(word, entry);
            }
            if (outcomes.Values.Contains("failed"))
            {
                await transaction.RollbackAsync(cancellationToken);
                foreach (var file in outcomes.Keys.ToArray()) { if (outcomes[file] != "failed") { outcomes[file] = "skipped"; } }
                return await Finish(output, outcomes, false);
            }
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await Finish(output, outcomes, true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            await error.WriteLineAsync("Import failed (no content committed): " + exception.Message);
            foreach (var file in outcomes.Keys.ToArray()) { outcomes[file] = "failed"; }
            await Finish(output, outcomes, false);
            return 1;
        }
    }

    private static async Task<int> Finish(TextWriter output, Dictionary<string, string> outcomes, bool success)
    {
        foreach (var (path, result) in outcomes) { await output.WriteLineAsync($"{result.ToUpperInvariant()} {Path.GetFileName(path)}"); }
        await output.WriteLineAsync($"Import: created={outcomes.Values.Count(value => value == "created")} updated={outcomes.Values.Count(value => value == "updated")} skipped={outcomes.Values.Count(value => value == "skipped")} failed={outcomes.Values.Count(value => value == "failed")} files={outcomes.Count}");
        return success ? 0 : 1;
    }
}
