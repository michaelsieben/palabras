using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Tests.Srs;

internal sealed class SrsTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public PalabrasDbContext Context { get; }

    public SrsTestDatabase()
    {
        _connection.Open();
        Context = new PalabrasDbContext(new DbContextOptionsBuilder<PalabrasDbContext>().UseSqlite(_connection).Options);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
