using Microsoft.Data.Sqlite;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;
using Palabras.Srs;

namespace Palabras.Backup;

internal sealed class BackupService : BackgroundService
{
    internal const int RetainedBackups = 7;
    private readonly DatabaseSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupService> _logger;

    public BackupService(DatabaseSettings settings, TimeProvider time, ILogger<BackupService> logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    internal string BackupDirectory => Path.Combine(Path.GetDirectoryName(_settings.FilePath)!, "backups");

    internal static DateTimeOffset NextRun(DateTimeOffset now, string? zoneId)
    {
        var clock = new LearnerClock(new FixedTimeProvider(now), zoneId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId ?? "Europe/Madrid");
        var date = clock.LocalToday;
        while (true)
        {
            var local = date.ToDateTime(new TimeOnly(3, 30), DateTimeKind.Unspecified);
            // Match LearnerClock's first-valid / first-occurrence rule in zones with unusual transitions.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            var candidate = zone.IsAmbiguousTime(local)
                ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            if (candidate >= now) return candidate;
            date = date.AddDays(1);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? previousRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var database = _settings.CreateContext();
                // Schema permits exactly one owner; authentication gives that owner the learner role.
                var zone = await database.Users.Where(user => user.Role == "owner")
                    .Select(user => user.Timezone).SingleOrDefaultAsync(stoppingToken);
                var now = _time.GetUtcNow();
                var next = NextRun(previousRun is { } previous && now <= previous ? previous.AddTicks(1) : now, zone);
                var delay = next - now;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, stoppingToken);
                previousRun = next;
                await CreateBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Nightly SQLite backup failed; previous backups retained. Retrying schedule in one hour.");
                try { await Task.Delay(TimeSpan.FromHours(1), _time, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task<string> CreateBackupAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(BackupDirectory);
        var stamp = _time.GetUtcNow().ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture);
        var name = $"palabras-{stamp}-{Guid.NewGuid():N}.db";
        var destination = Path.Combine(BackupDirectory, name);
        var temporary = destination + ".partial";
        try
        {
            await using (var database = _settings.CreateContext())
            {
                await database.Database.OpenConnectionAsync(cancellationToken);
                await using var command = database.Database.GetDbConnection().CreateCommand();
                command.CommandText = "VACUUM INTO $destination;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$destination";
                parameter.Value = temporary;
                command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                await using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!Equals(await check.ExecuteScalarAsync(cancellationToken), "ok"))
                    throw new InvalidOperationException("Backup integrity check failed.");
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('users','words','senses','cards','review_log','__EFMigrationsHistory');";
                if (!Equals(await check.ExecuteScalarAsync(cancellationToken), 6L))
                    throw new InvalidOperationException("Backup is missing required application tables.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination); // Same-directory publish: never overwrite a previous snapshot.
            foreach (var old in Directory.EnumerateFiles(BackupDirectory, "palabras-*.db")
                .Where(path => path != destination).OrderDescending(StringComparer.Ordinal).Skip(RetainedBackups - 1))
                File.Delete(old);
            _logger.LogInformation("SQLite backup published: {BackupFile}", destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
