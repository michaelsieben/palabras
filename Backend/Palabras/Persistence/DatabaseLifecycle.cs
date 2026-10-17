using Microsoft.EntityFrameworkCore;

namespace Palabras.Persistence;

internal static class DatabaseLifecycle
{
    internal static async Task InitializeAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var schemaLock = await AcquireSchemaLockAsync(settings, cancellationToken);
        await using var database = settings.CreateContext();
        await database.Database.MigrateAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    internal static async Task ResetAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var schemaLock = await AcquireSchemaLockAsync(settings, cancellationToken);
        await using var database = settings.CreateContext();
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        // Drop/recreate in the SAME file: never unlink a live SQLite file or its WAL/SHM files.
        command.CommandText = "PRAGMA foreign_keys=OFF;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            var tables = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    tables.Add(reader.GetString(0));
                }
            }
            foreach (var table in tables)
            {
                command.CommandText = "DROP TABLE \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            command.Transaction = null;
        }
        command.CommandText = "PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await database.Database.MigrateAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    private static async Task<FileStream> AcquireSchemaLockAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.FilePath)!);
        // One server container, but docker exec starts additional processes. Serialize ALL schema work.
        // OS releases this lock on crash; the persistent lock file contains no user data.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(settings.FilePath + ".schema.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
