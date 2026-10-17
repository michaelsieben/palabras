using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version }));
app.MapGet("/api/ping", () => Results.Ok(new { status = "ok", version }));
// simplification: unknown API paths must not return the SPA document.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
