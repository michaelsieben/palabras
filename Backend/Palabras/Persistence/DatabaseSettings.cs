using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Palabras.Persistence;

internal sealed record DatabaseSettings(string FilePath)
{
    internal static DatabaseSettings FromConfiguration(IConfiguration configuration)
    {
        var directory = configuration["DATA_DIR"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = "/data";
        }
        var configured = configuration.GetConnectionString("Default");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(directory, "palabras.db")
            : new SqliteConnectionStringBuilder(configured).DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
        {
            throw new InvalidOperationException("Configure a persistent SQLite database file, not an in-memory database.");
        }
        return new DatabaseSettings(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path)));
    }

    // The default keeps every existing caller on ReadWriteCreate. The health probe passes ReadWrite so a
    // missing file fails the probe instead of being silently recreated empty.
    internal PalabrasDbContext CreateContext(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = mode,
            ForeignKeys = true,
            // Separate short-lived CLI processes must never keep pooled handles across reset.
            Pooling = false,
            DefaultTimeout = 30
        };
        return new PalabrasDbContext(new DbContextOptionsBuilder<PalabrasDbContext>().UseSqlite(connection.ToString()).Options);
    }
}

internal sealed class PalabrasDbContextFactory : IDesignTimeDbContextFactory<PalabrasDbContext>
{
    public PalabrasDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        return DatabaseSettings.FromConfiguration(configuration).CreateContext();
    }
}
