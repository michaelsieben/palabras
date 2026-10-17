using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

app.UseDefaultFiles();
// The PWA shell files must never be served stale, and the proxy must not own this rule:
// an add_header inside an nginx location drops every server-level add_header.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name is "sw.js" or "manifest.webmanifest")
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version }));
app.MapGet("/api/ping", () => Results.Ok(new { status = "ok", version }));
// simplification: unknown API paths must not return the SPA document.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }
