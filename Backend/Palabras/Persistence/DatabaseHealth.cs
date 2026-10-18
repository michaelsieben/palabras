using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Palabras.Persistence;

internal static class DatabaseHealth
{
    // Cheapest reachability read: open the existing file and run one constant SELECT. No writes, no
    // migrations and no schema access, so a healthcheck cannot mutate the database it is checking.
    // ReadWrite (not the default ReadWriteCreate) fails when the file is absent, so a deleted database
    // reports unhealthy instead of being recreated empty; ReadOnly would break on a WAL database.
    internal static async Task<bool> CanReachAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.FilePath))
        {
            return false;
        }

        try
        {
            await using var database = settings.CreateContext(SqliteOpenMode.ReadWrite);
            await database.Database.OpenConnectionAsync(cancellationToken);
            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
