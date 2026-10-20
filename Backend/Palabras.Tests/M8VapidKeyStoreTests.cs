using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Palabras.Persistence;
using Palabras.Push;
using Xunit;

namespace Palabras.Tests;

// M8 VAPID key lifecycle (plan §2.2, U2). The file is the deployment's identity: it must be
// created once with owner-only permissions, reused verbatim afterwards, and never silently
// regenerated when it is unreadable — that would orphan every live subscription. Nothing that
// leaves the process, logs included, may contain the private key.
public sealed class M8VapidKeyStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T09:00:00Z", CultureInfo.InvariantCulture);
    private readonly string _root = Path.Combine("/tmp", "palabras-push", "vapid-" + Guid.NewGuid().ToString("N"));

    public M8VapidKeyStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("garbage", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    public void PushStaysOffUnlessTheSettingIsLiterallyTrue(string? value, bool expected)
    {
        KeyValuePair<string, string?>[] source = value is null
            ? Array.Empty<KeyValuePair<string, string?>>()
            : [KeyValuePair.Create<string, string?>(PushSettings.EnabledKey, value)];
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(source).Build();
        var settings = PushSettings.FromConfiguration(configuration, Settings());
        Assert.Equal(expected, settings.Enabled);
    }

    [Fact]
    public void VapidKeyPathDefaultsBesideTheDatabaseAndSubjectHasADefault()
    {
        var configuration = new ConfigurationBuilder().Build();
        var settings = PushSettings.FromConfiguration(configuration, Settings());
        Assert.Equal(Path.Combine(_root, "vapid.json"), settings.VapidKeyPath);
        Assert.Equal(PushSettings.DefaultSubject, settings.Subject);

        var overridden = PushSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PushSettings.VapidKeyPathKey] = Path.Combine(_root, "elsewhere", "key.json"),
                [PushSettings.SubjectKey] = "mailto:owner@example.invalid"
            }).Build(), Settings());
        Assert.Equal(Path.Combine(_root, "elsewhere", "key.json"), overridden.VapidKeyPath);
        Assert.Equal("mailto:owner@example.invalid", overridden.Subject);
    }

    [Fact]
    public async Task FirstStartCreatesA0600FileAndEveryLaterStartReusesIt()
    {
        var path = Path.Combine(_root, "vapid.json");
        var settings = new PushSettings(true, path, PushSettings.DefaultSubject);
        var first = new VapidKeyStore(settings, new FakeTimeProvider(Now), NullLogger());
        await first.InitializeAsync(CancellationToken.None);
        Assert.Null(first.Failure);
        var keys = Assert.IsType<VapidKeys>(first.Keys);
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        // The fingerprint is derived from the public key only and has the documented shape.
        Assert.Equal(16, keys.KeyId.Length);
        Assert.Matches("^[0-9a-f]{16}$", keys.KeyId);
        Assert.True(VapidKeyStore.TryDecodeBase64Url(keys.PublicKey, out var publicKey));
        Assert.True(VapidKeyStore.TryDecodeBase64Url(keys.PrivateKey, out var privateKey));
        Assert.Equal(65, publicKey.Length);
        Assert.Equal(0x04, publicKey[0]);
        Assert.Equal(32, privateKey.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(publicKey))[..16].ToLowerInvariant(), keys.KeyId);
        Assert.Equal(Now, keys.CreatedAt);

        var second = new VapidKeyStore(settings, new FakeTimeProvider(Now.AddDays(1)), NullLogger());
        await second.InitializeAsync(CancellationToken.None);
        var again = Assert.IsType<VapidKeys>(second.Keys);
        Assert.Equal(keys.PublicKey, again.PublicKey);
        Assert.Equal(keys.PrivateKey, again.PrivateKey);
        Assert.Equal(keys.KeyId, again.KeyId);
        Assert.Equal(keys.CreatedAt, again.CreatedAt);
    }

    [Fact]
    public async Task ACorruptFileIsReportedAndNeverRegenerated()
    {
        var path = Path.Combine(_root, "vapid.json");
        await File.WriteAllTextAsync(path, "{ this is not the key file", CancellationToken.None);
        var logger = new CapturingLogger<VapidKeyStore>();
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), logger);

        await store.InitializeAsync(CancellationToken.None);

        Assert.Null(store.Keys);
        Assert.NotNull(store.Failure);
        Assert.Equal("{ this is not the key file", await File.ReadAllTextAsync(path, CancellationToken.None));
        Assert.Contains(logger.Messages, message => message.Contains("Push features are disabled", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(path, StringComparison.Ordinal));
        // A failed file is not retried behind the operator's back.
        await store.InitializeAsync(CancellationToken.None);
        Assert.Null(store.Keys);
        Assert.Equal("{ this is not the key file", await File.ReadAllTextAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task AKeyFileWithMismatchedHalvesIsRejected()
    {
        var path = Path.Combine(_root, "vapid.json");
        var first = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        var other = VapidKeyStore.Generate(new FakeTimeProvider(Now));
        await File.WriteAllTextAsync(path,
            "{\"v\":1,\"publicKey\":\"" + first.PublicKey + "\",\"privateKey\":\"" + other.PrivateKey
            + "\",\"createdAt\":\"2026-10-20T09:00:00.0000000+00:00\"}", CancellationToken.None);
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), NullLogger());

        await store.InitializeAsync(CancellationToken.None);

        Assert.Null(store.Keys);
        Assert.Contains("do not belong together", store.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePrivateKeyNeverReachesTheLogs()
    {
        var path = Path.Combine(_root, "vapid.json");
        var logger = new CapturingLogger<VapidKeyStore>();
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), logger);

        await store.InitializeAsync(CancellationToken.None);

        var keys = Assert.IsType<VapidKeys>(store.Keys);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(keys.PrivateKey, message, StringComparison.Ordinal);
            Assert.DoesNotContain("privateKey", message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(logger.Messages, message => message.Contains(keys.KeyId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledPushNeverCreatesOrReadsTheFile()
    {
        var path = Path.Combine(_root, "vapid.json");
        var store = new VapidKeyStore(new PushSettings(false, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), NullLogger());

        await store.InitializeAsync(CancellationToken.None);
        Assert.Null(store.Keys);
        Assert.Null(store.Failure);
        Assert.False(File.Exists(path));

        // Even a corrupt file is irrelevant while push is off: nothing reads it.
        await File.WriteAllTextAsync(path, "garbage", CancellationToken.None);
        var second = new VapidKeyStore(new PushSettings(false, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), NullLogger());
        await second.InitializeAsync(CancellationToken.None);
        Assert.Null(second.Keys);
        Assert.Null(second.Failure);
    }

    [Fact]
    public async Task AFreshInstallWithAnEmptyDatabaseWarnsAboutNoOrphans()
    {
        var path = Path.Combine(_root, "vapid.json");
        var settings = Settings();
        await using (var database = settings.CreateContext())
        {
            await database.Database.MigrateAsync(CancellationToken.None);
        }
        var logger = new CapturingLogger<VapidKeyAudit>();
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), NullLogger());

        await store.InitializeAsync(CancellationToken.None);
        var orphaned = await VapidKeyAudit.WarnIfOrphanedAsync(store, settings, logger, CancellationToken.None);

        Assert.True(store.GeneratedOnLoad);
        Assert.Equal(0, orphaned);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ARegeneratedKeyWarnsThatExistingSubscriptionsAreOrphaned()
    {
        var path = Path.Combine(_root, "vapid.json");
        var settings = Settings();
        const string earlierKeyId = "0123456789abcdef";
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
                    VapidKeyId = earlierKeyId, CreatedAtMs = 1, UpdatedAtMs = 1, State = "active"
                },
                new PushSubscription
                {
                    UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/b", P256dh = "p", Auth = "a",
                    VapidKeyId = earlierKeyId, CreatedAtMs = 1, UpdatedAtMs = 1, State = "stale_key"
                });
            await database.SaveChangesAsync(CancellationToken.None);
        }
        var logger = new CapturingLogger<VapidKeyAudit>();
        var store = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), NullLogger());

        await store.InitializeAsync(CancellationToken.None);
        var keys = Assert.IsType<VapidKeys>(store.Keys);
        var orphaned = await VapidKeyAudit.WarnIfOrphanedAsync(store, settings, logger, CancellationToken.None);

        Assert.True(store.GeneratedOnLoad);
        Assert.Equal(2, orphaned);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("2 existing subscriptions are bound to another key", warning.Message,
            StringComparison.Ordinal);
        Assert.Contains(keys.KeyId, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(keys.PrivateKey, warning.Message, StringComparison.Ordinal);

        // A later start reads the same file and must not repeat the warning: nothing new was minted.
        var reloaded = new VapidKeyStore(new PushSettings(true, path, PushSettings.DefaultSubject),
            new FakeTimeProvider(Now), NullLogger());
        await reloaded.InitializeAsync(CancellationToken.None);
        var secondLogger = new CapturingLogger<VapidKeyAudit>();
        Assert.Equal(0, await VapidKeyAudit.WarnIfOrphanedAsync(reloaded, settings, secondLogger,
            CancellationToken.None));
        Assert.False(reloaded.GeneratedOnLoad);
        Assert.DoesNotContain(secondLogger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void Base64UrlDecodingAcceptsTheUrlAlphabetAndRejectsStandardBase64()
    {
        Assert.True(VapidKeyStore.TryDecodeBase64Url("AA-_", out var urlSafe));
        Assert.Equal(new byte[] { 0x00, 0x0F, 0xBF }, urlSafe);
        Assert.False(VapidKeyStore.TryDecodeBase64Url("AA+/", out _));
        Assert.False(VapidKeyStore.TryDecodeBase64Url("A", out _));
        Assert.False(VapidKeyStore.TryDecodeBase64Url("", out _));
        Assert.True(VapidKeyStore.TryDecodeBase64Url("AA-_==", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DatabaseSettings Settings() => new(Path.Combine(_root, "palabras.db"));

    private static ILogger<VapidKeyStore> NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<VapidKeyStore>.Instance;

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];

        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            Entries.Add((logLevel, message));
        }
    }
}
