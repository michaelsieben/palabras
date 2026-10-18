using System.Security.Claims;
using System.Text.Json;
using Palabras.Persistence;

namespace Palabras.Backup;

internal static class ExportEndpoints
{
    internal static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/learn/export", async (HttpContext context, DatabaseSettings settings,
            TimeProvider time, CancellationToken cancellationToken) =>
        {
            var format = context.Request.Query["format"].ToString();
            if (format is not ("json" or "csv")) return Results.BadRequest(new { error = "bad_format" });
            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Unauthorized();
            var export = await ProgressFiles.ExportAsync(settings, userId, time, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(format == "json" ? JsonSerializer.SerializeToUtf8Bytes(export, ProgressFiles.Json) : ProgressFiles.Csv(export),
                format == "json" ? "application/json" : "text/csv; charset=utf-8", $"palabras-progress.{format}");
        }).RequireAuthorization("learner");
        return endpoints;
    }
}
