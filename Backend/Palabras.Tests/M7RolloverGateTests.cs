using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Palabras.Learn;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

// M7 rollover rollout gate (T-20261018-2105 C1). A fresh deploy must migrate the schema but
// never write `missed` history unless M7:DayRolloverEnabled is literally true, and even when
// enabled the first post-enable ticks may not invent missed days for the period before the
// cutover (the backfill guard). Tests cover: config binding, a disabled host across startup,
// repeated ticks and restart; enabled ticks being idempotent across repeats and a restart;
// and the pre-enable day refusal through direct DayRollover runs, like M7DayStatusTests.
public sealed class M7RolloverGateTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static DateTimeOffset Moment(string iso) => DateTimeOffset.Parse(iso, Inv);

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
    public void TheGateStaysOffUnlessTheSettingIsLiterallyTrue(string? value, bool expected)
    {
        KeyValuePair<string, string?>[] source = value is null
            ? Array.Empty<KeyValuePair<string, string?>>()
            : [KeyValuePair.Create<string, string?>(DayRolloverSettings.EnabledKey, value)];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(source).Build();
        Assert.Equal(expected, DayRolloverSettings.FromConfiguration(configuration).Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task ADisabledHostWritesNoMissedHistoryAcrossStartupTicksAndRestart(string? setting)
    {
        using var rig = new GateRig();
        // 2026-10-18T09:00Z is 11:00 local in Madrid: the 00:05 window is open for every
        // eligible account, so an ungated job would already have written the 17th twice.
        var now = Moment("2026-10-18T09:00:00Z");
        using (var host = new GateHost(rig, now, setting))
        {
            Assert.False(host.Services.GetRequiredService<DayRolloverSettings>().Enabled);
            var service = host.RolloverService();
            Assert.False(service.Enabled);
            host.Time.Advance(TimeSpan.FromDays(5));
            Assert.Equal(0, await service.RunTickAsync(CancellationToken.None));
        }
        using (var host = new GateHost(rig, now, setting))
        {
            var service = host.RolloverService();
            host.Time.Advance(TimeSpan.FromDays(5));
            Assert.Equal(0, await service.RunTickAsync(CancellationToken.None));
        }
        await using (var database = rig.OpenDatabase())
        {
            Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
            Assert.Empty(await database.DaySkips.ToListAsync(CancellationToken.None));
            Assert.Empty(await database.PracticeSessions.ToListAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task AnEnabledHostTicksWriteEachDayOnceAndSurviveRepeatAndRestart()
    {
        using var rig = new GateRig();
        var now = Moment("2026-10-18T09:00:00Z");
        using var host = new GateHost(rig, now, "true");
        Assert.True(host.Services.GetRequiredService<DayRolloverSettings>().Enabled);
        var service = host.RolloverService();
        Assert.True(service.Enabled);
        // The enable instant is fixed at host start (the container's clock at first run).
        Assert.Equal(now, service.TrackingStart);
        await using (var database = rig.OpenDatabase())
        {
            Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
        }

        // The first tick waits one interval, and its candidate (2026-10-17) began before the
        // cutover: refused, nothing invented for the pre-enable day.
        host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await service.RunTickAsync(CancellationToken.None));
        // The next day the candidate is the enable day itself — a partial tracked day, still
        // refused (the first honest candidate is the first full local day after enablement).
        host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await service.RunTickAsync(CancellationToken.None));
        await using (var check = rig.OpenDatabase())
        {
            Assert.Empty(await check.DayStatuses.ToListAsync(CancellationToken.None));
        }

        // On 2026-10-20 the candidate 2026-10-19 began fully under the enabled tracker: two
        // accounts (owner + reviewer), one row each.
        host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, await service.RunTickAsync(CancellationToken.None));
        // Repeated ticks of the same day write nothing (idempotent existence check + PK).
        for (var tick = 0; tick < 3; tick++)
        {
            Assert.Equal(0, await service.RunTickAsync(CancellationToken.None));
        }
        // The next full day is written on its tick, one row per account.
        host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, await service.RunTickAsync(CancellationToken.None));

        // Restart mid-day: a fresh process on the same database re-derives its own tracking
        // start; committed rows stay exactly one per (account, day) and the refused pre-cutover
        // days are never retroactively written.
        using (var restarted = new GateHost(rig, now, "true"))
        {
            var second = restarted.RolloverService();
            restarted.Time.Advance(TimeSpan.FromDays(2)); // 2026-10-20: candidate 19 is committed.
            Assert.Equal(0, await second.RunTickAsync(CancellationToken.None));
            restarted.Time.Advance(TimeSpan.FromDays(1)); // candidate 20 is committed.
            Assert.Equal(0, await second.RunTickAsync(CancellationToken.None));
        }
        await using (var database = rig.OpenDatabase())
        {
            var rows = await database.DayStatuses.ToListAsync(CancellationToken.None);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, row => Assert.Equal("missed", row.Status));
            var dates = rows.Select(row => row.LocalDate).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(["2026-10-19", "2026-10-20"], dates);
        }
    }

    [Fact]
    public async Task BackfillRefusesEveryDayThatBeganBeforeTheTrackerWasEnabled()
    {
        using var rig = new GateRig();
        var trackingStart = Moment("2026-10-18T07:00:00Z"); // 09:00 local Madrid on the 18th.
        // Tick on the 19th (06:00 local): candidate 2026-10-18 began five hours before the
        // cutover — refused.
        Assert.Equal(0, await DayRollover.RunAsync(rig.Settings, new FakeTimeProvider(Moment("2026-10-19T04:00:00Z")),
            NullLogger.Instance, trackingStart, CancellationToken.None));
        // Tick on the 20th: candidate 2026-10-19 is the first full day under the enabled
        // tracker — written for both accounts.
        Assert.Equal(2, await DayRollover.RunAsync(rig.Settings, new FakeTimeProvider(Moment("2026-10-20T04:00:00Z")),
            NullLogger.Instance, trackingStart, CancellationToken.None));
        // Tick on the 21st: candidate 2026-10-20; the refused days are never backfilled.
        Assert.Equal(2, await DayRollover.RunAsync(rig.Settings, new FakeTimeProvider(Moment("2026-10-21T04:00:00Z")),
            NullLogger.Instance, trackingStart, CancellationToken.None));
        await using (var database = rig.OpenDatabase())
        {
            var dates = (await database.DayStatuses.ToListAsync(CancellationToken.None))
                .Select(row => row.LocalDate).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(["2026-10-19", "2026-10-20"], dates);
        }
    }

    [Fact]
    public async Task ATrackerEnabledExactlyAtMidnightOfADayAllowsThatDay()
    {
        using var rig = new GateRig();
        // 2026-10-18 00:00 Europe/Madrid == 2026-10-17T22:00Z: the boundary is inclusive, so a
        // cutover at the exact first instant of a day makes that day missable on its tick.
        var trackingStart = Moment("2026-10-17T22:00:00Z");
        var time = new FakeTimeProvider(Moment("2026-10-18T22:05:00Z")); // 00:05 local on the 19th.
        Assert.Equal(2, await DayRollover.RunAsync(rig.Settings, time, NullLogger.Instance,
            trackingStart, CancellationToken.None));
        await using (var database = rig.OpenDatabase())
        {
            var rows = await database.DayStatuses.ToListAsync(CancellationToken.None);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("2026-10-18", row.LocalDate));
        }
    }

    // Bare migrated database with both schema-allowed accounts (owner + reviewer), no web host.
    private sealed class GateRig : IDisposable
    {
        private readonly string _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode",
            "palabras-gate-" + Guid.NewGuid().ToString("N"));

        internal GateRig()
        {
            Directory.CreateDirectory(Path.Combine(Root, "wwwroot"));
            File.WriteAllText(Path.Combine(Root, "wwwroot", "index.html"), "<!doctype html><html>test spa</html>");
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using var database = Settings.CreateContext();
            database.Database.Migrate();
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
            database.SaveChanges();
        }

        internal string Root => _root;

        internal DatabaseSettings Settings => new(DatabasePath);

        internal string DatabasePath => Path.Combine(_root, "data", "palabras.db");

        internal PalabrasDbContext OpenDatabase() => Settings.CreateContext();

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    // Real web host over a shared rig database. The hosted DayRolloverService starts with it
    // (its 1-minute PeriodicTimer runs on the real clock and never fires inside a test); the
    // tick body is driven through DayRolloverService.RunTickAsync so virtual days can pass.
    private sealed class GateHost : WebApplicationFactory<Program>
    {
        private readonly GateRig _rig;

        internal GateHost(GateRig rig, DateTimeOffset now, string? rolloverSetting)
        {
            _rig = rig;
            Time = new FakeTimeProvider(now);
            RolloverSetting = rolloverSetting;
        }

        internal FakeTimeProvider Time { get; }

        internal string? RolloverSetting { get; }

        // AddHostedService registers the singleton only through the enumerable IHostedService
        // descriptors, so the running instance is resolved that way. Accessing Services runs
        // EnsureServer, which starts the host and its hosted services first.
        internal DayRolloverService RolloverService() =>
            Services.GetServices<IHostedService>().OfType<DayRolloverService>().Single();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(Path.Combine(_rig.Root, "wwwroot"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_rig.Root, "keys"));
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_rig.DatabasePath}");
            if (RolloverSetting is not null)
            {
                builder.UseSetting(DayRolloverSettings.EnabledKey, RolloverSetting);
            }
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
            });
        }

        // The rig owns the files: a restart creates a second host over the same database and
        // only the rig disposes the directory tree.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
