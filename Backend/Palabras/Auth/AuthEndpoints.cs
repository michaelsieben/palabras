using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Palabras.Auth;

internal static class AuthEndpoints
{
    internal static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, SeedUserStore users, HttpContext context) =>
        {
            var user = users.Authenticate(request.Username, request.Password);
            if (user == null)
            {
                return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);
            }
            var claims = new List<Claim> { new(ClaimTypes.Name, user.Username) };
            claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Ok(new { username = user.Username, roles = user.Roles });
        });
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        app.MapGet("/api/auth/me", (HttpContext context) => Results.Ok(new
        {
            username = context.User.Identity!.Name,
            roles = context.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray()
        })).RequireAuthorization();
    }

    internal static Task WriteError(HttpResponse response, int status, string error)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { error }, response.HttpContext.RequestAborted);
    }
}

internal sealed record LoginRequest(string? Username, string? Password);
