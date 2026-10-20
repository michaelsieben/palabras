using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Push;

// Host/operator-only CLI, like `user` and `learn`; never exposed through HTTP. `push status` is
// the diagnostic the VAPID plan (§2.1) asks for: whether push is enabled, the key fingerprint and
// creation instant (never the key), subscriptions per learner by state, and the resolved reminder
// window. Read-only: a missing key file is reported, never generated.
internal static class PushCli
{
    private const string Usage = "Usage: push status";

    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            // Parse before touching the database: a typo must not create or migrate anything.
            if (args.Length != 2 || args[0] != "push" || args[1] != "status")
            {
                throw new ArgumentException(Usage);
            }
            var push = PushSettings.FromConfiguration(configuration, settings);
            await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
            await using var database = settings.CreateContext();

            VapidKeys? keys = null;
            var keyState = "absent";
            var corrupt = false;
            try
            {
                keys = await VapidKeyStore.ReadIfPresentAsync(push.VapidKeyPath, cancellationToken);
                keyState = keys is null ? "absent" : "ready";
            }
            catch (InvalidDataException exception)
            {
                keyState = "corrupt";
                corrupt = true;
                await error.WriteLineAsync("Error: " + exception.Message);
            }

            await output.WriteLineAsync("push_enabled=" + (push.Enabled ? "true" : "false"));
            await output.WriteLineAsync("vapid_key=" + keyState);
            if (keys is not null)
            {
                await output.WriteLineAsync("vapid_key_id=" + keys.KeyId);
                await output.WriteLineAsync("vapid_key_created_at=" +
                    keys.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            }
            await output.WriteLineAsync("vapid_key_path=" + push.VapidKeyPath);
            await output.WriteLineAsync("subject=" + push.Subject);

            var counts = await database.PushSubscriptions.AsNoTracking()
                .GroupBy(row => new { row.UserId, row.State })
                .Select(group => new SubscriptionCount(group.Key.UserId, group.Key.State, group.Count()))
                .ToListAsync(cancellationToken);
            var users = await database.Users.AsNoTracking()
                .Select(user => new { user.Id, user.Username })
                .ToListAsync(cancellationToken);
            var usernames = users.ToDictionary(user => user.Id, user => user.Username);
            var total = 0;
            foreach (var group in counts.GroupBy(count => usernames.GetValueOrDefault(count.UserId, "#" + count.UserId))
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var userCounts = group.ToArray();
                var active = Sum(userCounts, PushSubscriptionLifecycle.Active);
                var staleKey = Sum(userCounts, PushSubscriptionLifecycle.StaleKey);
                var rejected = Sum(userCounts, PushSubscriptionLifecycle.Rejected);
                total += userCounts.Sum(count => count.Count);
                await output.WriteLineAsync(
                    $"subscription user={group.Key} active={active} stale_key={staleKey} rejected={rejected} " +
                    $"total={active + staleKey + rejected}");
            }
            await output.WriteLineAsync("subscriptions_total=" + total);

            // "No reminder_settings row" is the documented default (22:00, 90-minute window), so the
            // resolved window is printed once and then for every learner who changed it.
            var defaultWindow = new ReminderSettings().SendWindowMin;
            await output.WriteLineAsync("reminder_window_min=" + defaultWindow);
            var reminders = await database.ReminderSettings.AsNoTracking().ToListAsync(cancellationToken);
            foreach (var reminder in reminders.OrderBy(row => usernames.GetValueOrDefault(row.UserId, ""),
                         StringComparer.Ordinal))
            {
                await output.WriteLineAsync(
                    $"reminder user={usernames.GetValueOrDefault(reminder.UserId, "#" + reminder.UserId)} " +
                    $"enabled={(reminder.Enabled ? "true" : "false")} time={reminder.ReminderTime} " +
                    $"window_min={reminder.SendWindowMin} " +
                    $"muted_until={reminder.MutedUntil ?? "none"}");
            }
            return corrupt ? 1 : 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or DbUpdateException or SqliteException)
        {
            // Never echo raw EF/SQLite errors: they can carry parameter values.
            await error.WriteLineAsync(exception is DbUpdateException or SqliteException
                ? "Error: Database operation failed; no changes were committed."
                : "Error: " + exception.Message);
            return 1;
        }
    }

    private static int Sum(IEnumerable<SubscriptionCount> counts, string state) =>
        counts.Where(count => count.State == state).Sum(count => count.Count);

    private sealed record SubscriptionCount(int UserId, string State, int Count);
}
