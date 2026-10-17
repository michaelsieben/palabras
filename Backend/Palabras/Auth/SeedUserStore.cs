using Microsoft.AspNetCore.Identity;

namespace Palabras.Auth;

internal sealed class SeedUser
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? Password { get; set; }
    public string[] Roles { get; set; } = [];
}

internal sealed class SeedUserStore
{
    private readonly SeedUser[] _users;
    private readonly PasswordHasher<SeedUser> _hasher;
    private readonly SeedUser _dummy;

    internal SeedUserStore(SeedUser[] users, PasswordHasher<SeedUser> hasher)
    {
        _users = users;
        _hasher = hasher;
        _dummy = new SeedUser();
        _dummy.PasswordHash = hasher.HashPassword(_dummy, Guid.NewGuid().ToString());
    }

    internal SeedUser? Authenticate(string? username, string? password)
    {
        var user = _users.SingleOrDefault(candidate => candidate.Username == username);
        var target = user ?? _dummy;
        try
        {
            var result = _hasher.VerifyHashedPassword(target, target.PasswordHash, password ?? "");
            return user != null && password != null && result != PasswordVerificationResult.Failed ? user : null;
        }
        catch (FormatException)
        {
            // Invalid configured hashes fail closed, never become plaintext credentials.
            return null;
        }
    }
}
