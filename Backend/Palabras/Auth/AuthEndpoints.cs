using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Palabras.Auth;

internal static class AuthEndpoints
{
    internal static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, AccountAuthentication users, HttpContext context) =>
        {
            var user = await users.AuthenticateAsync(request.Username, request.Password, context.RequestAborted);
            if (user == null)
            {
                return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);
            }
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                AccountAuthentication.Principal(user),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Ok(new { username = user.Username, role = user.Role, roles = AccountAuthentication.Roles(user) });
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();
        app.MapGet("/api/auth/me", (HttpContext context) => Results.Ok(new
        {
            username = context.User.Identity!.Name,
            role = context.User.IsInRole("owner") ? "owner" : "reviewer",
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
