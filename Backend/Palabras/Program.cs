using System.Reflection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Palabras.Auth;
using Palabras.Accounts;
using Palabras.Persistence;

if (args.Length > 0 && args[0] == "user")
{
    var cliConfiguration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
    using var cliCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cliCancellation.Cancel(); };
    try
    {
        Environment.ExitCode = await UserCli.RunAsync(args, DatabaseSettings.FromConfiguration(cliConfiguration), Console.In,
            Console.Out, Console.Error, cliCancellation.Token, Console.IsInputRedirected ? null : UserCli.ReadHiddenPassword);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Command cancelled.");
        Environment.ExitCode = 1;
    }
    return;
}

// Hash generation never starts the server or exposes the password in command-line arguments.
if (args is ["--hash-password"])
{
    var password = Console.ReadLine() ?? throw new InvalidOperationException("Supply a password on stdin.");
    Console.WriteLine(new PasswordHasher<SeedUser>().HashPassword(new SeedUser(), password));
    return;
}
var builder = WebApplication.CreateBuilder(args);
var databaseSettings = DatabaseSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(databaseSettings);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.18.0.0/16"));
});
// Move the default host filter behind forwarded headers to validate the effective host. The framework
// filter is an internal type, so it is resolved once from the public assembly anchor and then matched by
// Type identity instead of by type name. A rename, a move or a missing registration fails fast, so the
// default filter can never silently stay ahead of forwarded headers after a framework upgrade.
var hostFilteringStartupFilter = typeof(WebApplication).Assembly.GetType("Microsoft.AspNetCore.HostFilteringStartupFilter", throwOnError: false)
    ?? throw new InvalidOperationException("The framework host-filtering startup filter could not be resolved. Host filtering order cannot be guaranteed.");
var removedHostFilters = 0;
foreach (var descriptor in builder.Services.Where(service => service.ImplementationType == hostFilteringStartupFilter).ToArray())
{
    if (builder.Services.Remove(descriptor))
    {
        removedHostFilters++;
    }
}
if (removedHostFilters == 0)
{
    throw new InvalidOperationException("The framework host-filtering startup filter is not registered. Host filtering would run before forwarded headers.");
}
var users = builder.Configuration.GetSection("Auth:Users").Get<SeedUser[]>() ?? [];
var hasher = new PasswordHasher<SeedUser>();
foreach (var user in users)
{
    if (string.IsNullOrWhiteSpace(user.Username) || user.Roles.Length == 0 || user.Roles.Any(role => role is not ("learner" or "reviewer")))
    {
        throw new InvalidOperationException("Auth users need a username and learner/reviewer roles.");
    }
    if (user.Password != null)
    {
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException("Plaintext Auth passwords are allowed only in Development.");
        }
        user.PasswordHash = hasher.HashPassword(user, user.Password);
        user.Password = null;
    }
    if (string.IsNullOrWhiteSpace(user.PasswordHash))
    {
        throw new InvalidOperationException("Auth users need PasswordHash values.");
    }
}
if (users.Select(user => user.Username).Distinct(StringComparer.Ordinal).Count() != users.Length)
{
    throw new InvalidOperationException("Auth usernames must be unique.");
}
builder.Services.AddSingleton(new SeedUserStore(users, hasher));
var keysPath = builder.Configuration["DataProtection:KeysPath"];
builder.Services.AddDataProtection().SetApplicationName("Palabras");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<ILoggerFactory>((options, loggerFactory) => options.XmlRepository = new ResilientKeyRepository(keysPath, loggerFactory));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "__Host-palabras";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Path = "/";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => AuthEndpoints.WriteError(context.Response, 401, "unauthorized");
    options.Events.OnRedirectToAccessDenied = context => AuthEndpoints.WriteError(context.Response, 403, "forbidden");
});
builder.Services.AddAuthorization(options => options.AddPolicy("reviewer", policy => policy.RequireRole("reviewer")));
var app = builder.Build();
await DatabaseLifecycle.InitializeAsync(databaseSettings, app.Lifetime.ApplicationStopping);
if (users.Length == 0)
{
    AuthLog.NoAuthUsersConfigured(app.Logger);
}
app.UseForwardedHeaders();
app.UseHostFiltering();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method)
        && context.Request.Headers["X-Palabras"] != "1")
    {
        await AuthEndpoints.WriteError(context.Response, 400, "missing_x_palabras");
        return;
    }
    await next(context);
});
// Framework request-binding failures answer with a bare status code, or raise BadHttpRequestException
// where RouteHandlerOptions.ThrowOnBadRequest is set. API callers always get the contract's JSON error
// shape instead: no HTML page, no stack trace and no redirect.
app.UseStatusCodePages(statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        return Task.CompletedTask;
    }
    var error = context.Response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        _ => "request_failed"
    };
    return AuthEndpoints.WriteError(context.Response, context.Response.StatusCode, error);
});
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        // Keep the status code, drop the binding detail; the status code page above writes the contract body.
        AuthLog.RequestBodyRejected(app.Logger, exception.StatusCode, exception);
        context.Response.StatusCode = exception.StatusCode;
    }
});
// Explicit routing prevents minimal hosting from inserting routing ahead of forwarded headers.
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
var staticOptions = new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context =>
    {
        // The proxy must not own the PWA shell cache rule.
        if (context.File.Name is "index.html" or "sw.js" or "manifest.webmanifest")
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
};
app.UseDefaultFiles();
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), branch => branch.UseStaticFiles(staticOptions));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version }));
app.MapGet("/api/ping", () => Results.Ok(new { status = "ok", version }));
app.MapAuthEndpoints();
// simplification: M4 will replace this reviewer queue placeholder with real content.
app.MapGet("/api/review/queue", () => Results.Ok(new { items = Array.Empty<object>(), note = "M4" })).RequireAuthorization("reviewer");
app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "not_found" }));
app.MapFallbackToFile("index.html", staticOptions);
app.Run();

public partial class Program { }
