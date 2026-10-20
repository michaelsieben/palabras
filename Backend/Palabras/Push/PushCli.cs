using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Palabras.Auth;
using Palabras.Persistence;

namespace Palabras.Push;

// Host/operator-only CLI, like `user` and `learn`; never exposed through HTTP.
//
// `push status` is the diagnostic the VAPID plan (§2.1) asks for: whether push is enabled, the key
// fingerprint and creation instant (never the key), subscriptions per learner by state, and the
// resolved reminder window. Read-only: a missing key file is reported, never generated.
//
// `push test-send` is the first real delivery an operator can trigger (plan §4 U4: "the first prod
// probe" for container egress to the push service). It sends the same payload as POST /api/push/test
// and applies the same accounting, so a 410 here removes the dead device exactly as a reminder would.
internal static class PushCli
{
    private const string Usage =
        "Usage: push status | push test-send [--user <username>] [--endpoint <endpoint>]";

    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            // Parse before touching the database: a typo must not create or migrate anything.
            if (args.Length == 2 && args[0] == "push" && args[1] == "status")
            {
                return await StatusAsync(settings, configuration, output, error, cancellationToken);
            }
            if (args.Length >= 2 && args[0] == "push" && args[1] == "test-send")
            {
                return await TestSendAsync(args, settings, configuration, output, error, cancellationToken);
            }
            throw new ArgumentException(Usage);
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

    private static async Task<int> StatusAsync(DatabaseSettings settings, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
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
        // `vapid_key_id` is the fingerprint stored on every row (frozen schema), so the orphan
        // count is exact. With no readable key there is nothing to compare against, and the
        // existing vapid_key=absent|corrupt line says why.
        await output.WriteLineAsync(keys is null
            ? "bound_to_other_key=unknown"
            : "bound_to_other_key=" + await VapidKeyAudit.CountBoundToOtherKeyAsync(database, keys.KeyId,
                cancellationToken));

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

    private static async Task<int> TestSendAsync(string[] args, DatabaseSettings settings, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        string? username = null;
        string? endpoint = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(Usage);
            }
            var value = args[index + 1];
            switch (args[index])
            {
                case "--user":
                    username = value;
                    break;
                case "--endpoint":
                    endpoint = value;
                    break;
                default:
                    throw new ArgumentException(Usage);
            }
        }
        var push = PushSettings.FromConfiguration(configuration, settings);
        if (!push.Enabled)
        {
            // Same rule as every route: nothing is sent while Push:Enabled is not literally true.
            await error.WriteLineAsync("Error: push is disabled; set Push__Enabled=true to send a test.");
            return 1;
        }
        await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
        int? userId = null;
        if (username is not null)
        {
            await using var database = settings.CreateContext();
            var normalized = LoginSecurity.NormalizeUsername(username);
            var user = await database.Users.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Username == normalized, cancellationToken);
            if (user is null)
            {
                await error.WriteLineAsync("Error: unknown user.");
                return 1;
            }
            userId = user.Id;
        }
        // Never generated here: `push status` reports an absent key and the operator decides.
        var keys = await VapidKeyStore.ReadIfPresentAsync(push.VapidKeyPath, cancellationToken)
            ?? throw new InvalidOperationException(
                "the VAPID key file " + push.VapidKeyPath + " is absent; start the app with push enabled once.");
        IReadOnlyList<PushTestTarget> targets;
        await using (var database = settings.CreateContext())
        {
            targets = await PushTestSender.QueryAsync(database, userId, endpoint, cancellationToken);
        }
        await output.WriteLineAsync("targets=" + targets.Count);
        if (targets.Count == 0)
        {
            await error.WriteLineAsync("Error: no_active_subscription");
            return 1;
        }
        using var transport = new WebPushTransport(push, NullLogger<WebPushTransport>.Instance);
        var sender = new PushSender(settings, transport, TimeProvider.System, NullLogger<PushSender>.Instance);
        var failed = 0;
        foreach (var target in targets)
        {
            var result = await sender.SendAsync(target.Id, PushTestSender.Message, keys, reminderLogId: null,
                cancellationToken);
            // The endpoint host only: the full endpoint is a bearer-like secret.
            await output.WriteLineAsync(
                $"delivery subscription={target.Id} host={WebPushTransport.HostOf(target.Endpoint)} " +
                $"outcome={PushOutcomeClassifier.Wire(result.Outcome)} " +
                $"status={(result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none")}" +
                (result.Failure is null ? "" : " error=" + result.Failure));
            if (result.Outcome != PushOutcome.Delivered)
            {
                failed++;
            }
        }
        return failed == 0 ? 0 : 1;
    }

    private static int Sum(IEnumerable<SubscriptionCount> counts, string state) =>
        counts.Where(count => count.State == state).Sum(count => count.Count);

    private sealed record SubscriptionCount(int UserId, string State, int Count);
}
