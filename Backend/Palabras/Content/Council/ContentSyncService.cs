using Palabras.Persistence;

namespace Palabras.Content.Council;

// Startup import of committed council bundles (plan §3.4, Q-M3-7). One scan at startup, no watcher,
// no timer: a manual re-run is the `import-council` verb.
//
// The sync imports PROPOSE bundles only. A rework changes the status and version of content that may
// already be approved, and that must never be a side effect of a container restart, so the importer
// refuses a rework on this path (CouncilImportOptions.ProposeOnly) and the refusal is only logged.
// The app must start and /healthz must stay ok whatever the bundles say: every bundle is isolated
// in its own transaction and every failure is logged and skipped.
internal sealed class ContentSyncService : BackgroundService
{
    internal const string DirectoryKey = "Content:CouncilDirectory";
    internal const string EnabledKey = "Content:CouncilSyncEnabled";

    private readonly DatabaseSettings _database;
    private readonly ContentSyncSettings _settings;
    private readonly ILogger<ContentSyncService> _logger;

    public ContentSyncService(DatabaseSettings database, ContentSyncSettings settings,
        ILogger<ContentSyncService> logger)
    {
        _database = database;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("council sync: disabled by {Setting}.", EnabledKey);
            return;
        }
        if (!Directory.Exists(_settings.Directory))
        {
            // Today the image ships no bundles, so this is the normal startup line, not a warning.
            _logger.LogInformation("council sync: no bundle directory at {Directory}; nothing to import.",
                _settings.Directory);
            return;
        }
        var importer = new CouncilImporter(_database);
        foreach (var bundle in Directory.GetDirectories(_settings.Directory)
                     .Where(child => File.Exists(Path.Combine(child, "manifest.json")))
                     .Order(StringComparer.Ordinal))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            try
            {
                var result = await importer.ImportAsync(bundle,
                    new CouncilImportOptions(ProposeOnly: true), stoppingToken);
                if (result.Refused)
                {
                    // Retried on the next start: the run id was never recorded.
                    _logger.LogError("council sync: run={RunId} mode={Mode} refused: {Refusals}",
                        string.IsNullOrEmpty(result.RunId) ? Path.GetFileName(bundle) : result.RunId,
                        result.Mode, string.Join(" | ", result.Refusals));
                    continue;
                }
                if (result.AlreadyImported)
                {
                    _logger.LogInformation("council sync: run={RunId} already imported.", result.RunId);
                    continue;
                }
                _logger.LogInformation(
                    "council sync: run={RunId} mode={Mode} created={Created} skipped={Skipped} failed=0",
                    result.RunId, result.Mode, result.Created,
                    result.Unchanged + result.SkippedExisting + result.SkippedReviewerEditNewer);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One bad bundle must not stop the rest, and must not stop the app: log the type and
                // message only, never the manifest body.
                _logger.LogError(exception,
                    "council sync: bundle {Bundle} was not imported; it will be retried on the next start.",
                    Path.GetFileName(bundle));
            }
        }
    }
}

// Read once at startup, next to BackupService/DayRolloverService. `ContentSyncEnabled` defaults to
// true so a committed bundle imports on the first start; an explicit false (or a non-boolean) is the
// operator's kill switch and wins over the directory.
internal sealed record ContentSyncSettings(bool Enabled, string Directory)
{
    internal static ContentSyncSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[ContentSyncService.DirectoryKey];
        return new(
            !bool.TryParse(configuration[ContentSyncService.EnabledKey]?.Trim(), out var enabled) || enabled,
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "content", "council")
                : configured);
    }
}
