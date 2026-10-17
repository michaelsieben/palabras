using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Palabras.Accounts;
using Palabras.Persistence;

namespace Palabras.Auth;

internal sealed class AccountAuthentication
{
    private const string StampClaim = "palabras:password-stamp";
    private readonly DatabaseSettings _settings;
    private readonly AccountPasswordHasher _hasher;
    private readonly string _dummyHash;

    public AccountAuthentication(DatabaseSettings settings, AccountPasswordHasher hasher)
    {
        _settings = settings;
        _hasher = hasher;
        _dummyHash = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    internal async Task<User?> AuthenticateAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        await using var database = _settings.CreateContext();
        var normalized = LoginSecurity.NormalizeUsername(username);
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Username == normalized, cancellationToken);
        // Same KDF cost for unknown accounts and bad passwords; never return lookup details.
        var valid = _hasher.Verify(user?.PasswordHash ?? _dummyHash, password?.Length <= 1024 ? password : "");
        if (user == null || !valid || password == null || password.Length > 1024)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        // Password rotation/deletion racing login must not issue a usable stale session.
        var updated = await database.Users.Where(candidate => candidate.Id == user.Id && candidate.PasswordHash == user.PasswordHash)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.LastLoginAt, now), cancellationToken);
        return updated == 1 ? user : null;
    }

    internal static string[] Roles(User user) => user.Role == "owner" ? ["owner", "learner"] : [user.Role];

    internal static ClaimsPrincipal Principal(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.Username), new(StampClaim, Stamp(user.PasswordHash))
        };
        claims.AddRange(Roles(user).Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    internal async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (!int.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            context.RejectPrincipal();
            return;
        }
        await using var database = _settings.CreateContext();
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, context.HttpContext.RequestAborted);
        // Hash-derived stamp changes on password rotation and on id reuse after reset. It is
        // encrypted inside the cookie; never serialize the stored password hash in an API response.
        if (user == null || context.Principal?.FindFirstValue(StampClaim) != Stamp(user.PasswordHash))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var renamed = context.Principal?.Identity?.Name != user.Username;
        context.ReplacePrincipal(Principal(user));
        if (renamed)
        {
            context.ShouldRenew = true;
        }
    }

    private static string Stamp(string hash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash)));
}
