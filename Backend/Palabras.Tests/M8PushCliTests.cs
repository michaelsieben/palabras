using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Palabras.Persistence;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// `push status` is the operator's diagnostic (plan §2.1): enabled/disabled, the VAPID key
// fingerprint and creation instant (never the key), subscriptions per learner by state, and the
// resolved reminder window. It must be read-only: a missing key file is reported, not generated.
public sealed class M8PushCliTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T09:00:00Z", CultureInfo.InvariantCulture);
    private readonly string _root = Path.Combine("/tmp", "palabras-push", "cli-" + Guid.NewGuid().ToString("N"));

    public M8PushCliTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ADefaultDeploymentReportsPushOffAndNoKey()
    {
        var result = await Run(Array.Empty<KeyValuePair<string, string?>>());
        Assert.Equal(0, result.Exit);
        Assert.Contains("push_enabled=false", result.Output);
        Assert.Contains("vapid_key=absent", result.Output);
        Assert.Contains("subscriptions_total=0", result.Output);
        Assert.Contains("reminder_window_min=90", result.Output);
        Assert.False(File.Exists(Path.Combine(_root, "vapid.json")));
    }

    [Fact]
    public async Task EnabledWithAMissingKeyReportsAbsentAndDoesNotGenerateOne()
    {
        var path = Path.Combine(_root, "vapid.json");
        var result = await Run(Enabled(path));
        Assert.Equal(0, result.Exit);
        Assert.Contains("push_enabled=true", result.Output);
        Assert.Contains("vapid_key=absent", result.Output);
        // Without a readable key there is no fingerprint to compare rows against.
        Assert.Contains("bound_to_other_key=unknown", result.Output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StatusReportsTheFingerprintCountsAndReminderWindow()
    {
        var path = Path.Combine(_root, "vapid.json");
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), Microsoft.Extensions.Logging.Abstractions.NullLogger<VapidKeyStore>.Instance);
        await store.InitializeAsync(CancellationToken.None);
        var keys = Assert.IsType<VapidKeys>(store.Keys);
        var settings = Settings();
        await using (var database = settings.CreateContext())
        {
            await database.Database.MigrateAsync(CancellationToken.None);
            var stamp = DateTimeOffset.UnixEpoch;
            database.Users.AddRange(
                new User
                {
                    Id = 1, Username = "learner", Role = "owner", PasswordHash = "hash",
                    CreatedAt = stamp, UpdatedAt = stamp
                },
                new User
                {
                    Id = 2, Username = "reviewer", Role = "reviewer", PasswordHash = "hash",
                    CreatedAt = stamp, UpdatedAt = stamp
                });
            database.PushSubscriptions.AddRange(
                new PushSubscription
                {
                    UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/a", P256dh = "p", Auth = "a",
                    VapidKeyId = keys.KeyId, CreatedAtMs = 1, UpdatedAtMs = 1, State = "active"
                },
                new PushSubscription
                {
                    UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/b", P256dh = "p", Auth = "a",
                    VapidKeyId = keys.KeyId, CreatedAtMs = 1, UpdatedAtMs = 1, State = "rejected"
                });
            database.ReminderSettings.Add(new ReminderSettings
            {
                UserId = 1, Enabled = true, ReminderTime = "21:30", SendWindowMin = 120,
                MutedUntil = "2026-10-25", UpdatedAtMs = 1
            });
            await database.SaveChangesAsync(CancellationToken.None);
        }

        var result = await Run(Enabled(path));
        Assert.Equal(0, result.Exit);
        Assert.Contains("push_enabled=true", result.Output);
        Assert.Contains("vapid_key=ready", result.Output);
        Assert.Contains("vapid_key_id=" + keys.KeyId, result.Output);
        Assert.Contains("vapid_key_path=" + path, result.Output);
        Assert.Contains("bound_to_other_key=0", result.Output);
        Assert.Contains("subscription user=learner active=1 stale_key=0 rejected=1 total=2", result.Output);
        Assert.Contains("subscriptions_total=2", result.Output);
        Assert.Contains("reminder_window_min=90", result.Output);
        Assert.Contains("reminder user=learner enabled=true time=21:30 window_min=120 muted_until=2026-10-25",
            result.Output);
        // The private key never leaves the file.
        Assert.DoesNotContain(keys.PrivateKey, result.Output);
        Assert.DoesNotContain(keys.PrivateKey, result.Error);
    }

    [Fact]
    public async Task StatusCountsSubscriptionsBoundToAnEarlierKey()
    {
        var path = Path.Combine(_root, "vapid.json");
        var settings = Settings();
        await using (var database = settings.CreateContext())
        {
            await database.Database.MigrateAsync(CancellationToken.None);
            var stamp = DateTimeOffset.UnixEpoch;
            database.Users.Add(new User
            {
                Id = 1, Username = "learner", Role = "owner", PasswordHash = "hash",
                CreatedAt = stamp, UpdatedAt = stamp
            });
            database.PushSubscriptions.AddRange(
                new PushSubscription
                {
                    UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/a", P256dh = "p", Auth = "a",
                    VapidKeyId = "0123456789abcdef", CreatedAtMs = 1, UpdatedAtMs = 1, State = "active"
                },
                new PushSubscription
                {
                    UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/b", P256dh = "p", Auth = "a",
                    VapidKeyId = "fedcba9876543210", CreatedAtMs = 1, UpdatedAtMs = 1, State = "stale_key"
                });
            await database.SaveChangesAsync(CancellationToken.None);
        }
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VapidKeyStore>.Instance);
        await store.InitializeAsync(CancellationToken.None);
        var keys = Assert.IsType<VapidKeys>(store.Keys);

        var result = await Run(Enabled(path));

        Assert.Equal(0, result.Exit);
        // Neither earlier fingerprint matches the current key, so both rows are orphaned.
        Assert.Contains("bound_to_other_key=2", result.Output);
        Assert.Contains("vapid_key_id=" + keys.KeyId, result.Output);
        Assert.DoesNotContain(keys.PrivateKey, result.Output);
    }

    [Fact]
    public async Task ACorruptKeyFileExitsNonZeroWithAClearMessage()
    {
        var path = Path.Combine(_root, "vapid.json");
        await File.WriteAllTextAsync(path, "{ not a key", CancellationToken.None);
        var result = await Run(Enabled(path));
        Assert.Equal(1, result.Exit);
        Assert.Contains("vapid_key=corrupt", result.Output);
        Assert.StartsWith("Error: ", result.Error);
        Assert.Contains("VAPID key file", result.Error);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("push", "rotate")]
    [InlineData("push", "status", "extra")]
    public async Task InvalidArgumentsFailBeforeDatabaseAccess(params string[] args)
    {
        var result = await Run(Array.Empty<KeyValuePair<string, string?>>(), args);
        Assert.Equal(1, result.Exit);
        Assert.StartsWith("Error: Usage: push status", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "palabras.db")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DatabaseSettings Settings() => new(Path.Combine(_root, "palabras.db"));

    private static KeyValuePair<string, string?>[] Enabled(string path) =>
    [
        KeyValuePair.Create<string, string?>(PushSettings.EnabledKey, "true"),
        KeyValuePair.Create<string, string?>(PushSettings.VapidKeyPathKey, path)
    ];

    private async Task<(int Exit, string Output, string Error)> Run(
        KeyValuePair<string, string?>[] values, params string[] args)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PushCli.RunAsync(args.Length == 0 ? ["push", "status"] : args, Settings(), configuration,
            output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }
}
