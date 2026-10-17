using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace Palabras.Auth;

internal static class LoginSecurity
{
    private const string UsernameItem = "palabras:login-username";

    internal static string NormalizeUsername(string? username) => username?.Length <= 128 ? username.Trim().ToLowerInvariant() : "";

    internal static string PartitionKey(HttpContext context) =>
        $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}|{context.Items[UsernameItem] ?? ""}";

    internal static async Task ReadPartitionUsernameAsync(HttpContext context, RequestDelegate next)
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/api/auth/login")
        {
            // Bound buffered login JSON before a second read by minimal-API binding. This reads
            // only after trusted forwarding, so the limiter never partitions on the proxy IP.
            context.Request.EnableBuffering(bufferThreshold: 16 * 1024, bufferLimit: 16 * 1024);
            if (context.Request.ContentLength > 16 * 1024)
            {
                await AuthEndpoints.WriteError(context.Response, 413, "request_too_large");
                return;
            }
            if (context.Request.HasJsonContentType())
            {
                try
                {
                    var login = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted);
                    context.Items[UsernameItem] = NormalizeUsername(login?.Username);
                }
                catch (JsonException)
                {
                    // The existing API binding/error middleware owns malformed JSON responses.
                }
                catch (IOException)
                {
                    await AuthEndpoints.WriteError(context.Response, 413, "request_too_large");
                    return;
                }
                finally
                {
                    context.Request.Body.Position = 0;
                }
            }
        }
        await next(context);
    }

    internal static bool IsPublic(HttpContext context, IFileProvider files)
    {
        var path = context.Request.Path;
        if (path == "/healthz" || path == "/login" || path == "/login/" || path == "/api/auth/login")
        {
            return true;
        }
        if (path.StartsWithSegments("/api"))
        {
            return false;
        }
        // Only real static assets are public, never index.html or arbitrary SPA routes with
        // a file-like extension. DefaultFiles runs later and cannot bypass this gate.
        return !string.IsNullOrEmpty(path.Value) && !path.Value.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            && files.GetFileInfo(path.Value).Exists && !files.GetFileInfo(path.Value).IsDirectory;
    }
}
