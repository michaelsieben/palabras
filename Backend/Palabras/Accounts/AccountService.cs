using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Accounts;

internal sealed partial class AccountService
{
    private readonly PalabrasDbContext _database;
    private readonly AccountPasswordHasher _hasher;

    internal AccountService(PalabrasDbContext database, AccountPasswordHasher hasher)
    {
        _database = database;
        _hasher = hasher;
    }

    internal static string NormalizeUsername(string username)
    {
        var normalized = username.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(normalized))
        {
            throw new ArgumentException("Username must be 3-32 characters: a-z, 0-9, dot, underscore or hyphen.");
        }
        return normalized;
    }

    internal async Task CreateAsync(string role, string username, string password, CancellationToken cancellationToken)
    {
        if (role is not ("owner" or "reviewer"))
        {
            throw new ArgumentException("Role must be owner or reviewer.");
        }
        username = NormalizeUsername(username);
        ValidatePassword(password);
        if (await _database.Users.AnyAsync(user => user.Role == role, cancellationToken))
        {
            throw new ArgumentException($"An account for role '{role}' already exists.");
        }
        if (await _database.Users.AnyAsync(user => user.Username == username, cancellationToken))
        {
            throw new ArgumentException("Username is already taken (case-insensitive).");
        }
        var now = DateTimeOffset.UtcNow;
        _database.Users.Add(new User
        {
            Username = username, Role = role, PasswordHash = _hasher.Hash(password), CreatedAt = now, UpdatedAt = now
        });
        await _database.SaveChangesAsync(cancellationToken);
    }

    internal async Task SetUsernameAsync(string oldUsername, string newUsername, CancellationToken cancellationToken)
    {
        oldUsername = NormalizeUsername(oldUsername);
        newUsername = NormalizeUsername(newUsername);
        var now = DateTimeOffset.UtcNow;
        // Single UPDATE; preserve id, created_at, password_hash, role and ALL future FK rows.
        var updated = await _database.Users.Where(user => user.Username == oldUsername)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.Username, newUsername).SetProperty(user => user.UpdatedAt, now), cancellationToken);
        RequireAccount(updated);
    }

    internal async Task SetPasswordAsync(string username, string password, CancellationToken cancellationToken)
    {
        username = NormalizeUsername(username);
        ValidatePassword(password);
        var hash = _hasher.Hash(password);
        var now = DateTimeOffset.UtcNow;
        var updated = await _database.Users.Where(user => user.Username == username)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, hash).SetProperty(user => user.UpdatedAt, now), cancellationToken);
        RequireAccount(updated);
    }

    internal Task<List<User>> ListAsync(CancellationToken cancellationToken) =>
        _database.Users.AsNoTracking().OrderBy(user => user.Role).ToListAsync(cancellationToken);

    private static void RequireAccount(int updated)
    {
        if (updated == 0)
        {
            throw new ArgumentException("Account not found. Use 'user create <owner|reviewer> --username <name>' first.");
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 12 or > 1024)
        {
            throw new ArgumentException("Password must contain 12-1024 characters.");
        }
    }

    [GeneratedRegex("\\A[a-z0-9._-]{3,32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
