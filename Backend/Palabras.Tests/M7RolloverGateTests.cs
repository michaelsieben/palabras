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
// cutover (the backfill guard). Step 11 made that cutover durable: it is fixed in app_meta by
// the first enabled observation of the database and read back unchanged by every restart,
// redeploy or re-enable, so a restart can no longer move it and strand the day under way.
// Tests cover: config binding, a disabled host across startup, repeated ticks and restart
// (writing neither history nor a cutover marker); enabled ticks being idempotent across repeats
// and a restart; the restart not losing a day; cutover sharing across service instances over one
// database; kill-switch re-enablement keeping the original cutover; an unreadable marker
// re-anchoring without inventing history; and the pre-enable day refusal through direct
// DayRollover runs, like M7DayStatusTests.
public sealed class M7RolloverGateTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static DateTimeOffset Moment(string iso) => DateTimeOffset.Parse(iso, Inv);

    // A service instance without a web host: ExecuteAsync never runs, ticks are driven directly.
    private static DayRolloverService NewService(GateRig rig, FakeTimeProvider time, bool enabled) =>
        new(rig.Settings, new DayRolloverSettings(enabled), time, NullLogger<DayRolloverService>.Instance);

    private static async Task<long> ReadCutoverMarkerMs(GateRig rig)
    {
        await using var database = rig.OpenDatabase();
        var markers = await database.AppMetas.AsNoTracking().ToListAsync(CancellationToken.None);
        var marker = Assert.Single(markers);
        Assert.Equal(DayRollover.CutoverKey, marker.Key);
        return long.Parse(marker.Value, Inv);
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
            // A disabled host never observes the gate enabled, so it also never fixes a cutover.
            Assert.Empty(await database.AppMetas.ToListAsync(CancellationToken.None));
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
        // The enable instant is durable: the first enabled observation fixes the cutover at the
        // host-start instant and writes it to app_meta (T-20261018-2105 step 11 replaced the
        // old per-process TrackingStart capture that moved on every restart).
        Assert.Equal(now, await service.CutoverAsync(CancellationToken.None));
        Assert.Equal(now.ToUnixTimeMilliseconds(), await ReadCutoverMarkerMs(rig));
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

        // Restart mid-day: a fresh process on the same database reads back the SAME persisted
        // cutover instead of re-deriving one from its own host start; committed rows stay
        // exactly one per (account, day) and the refused pre-cutover days are never
        // retroactively written.
        using (var restarted = new GateHost(rig, now, "true"))
        {
            var second = restarted.RolloverService();
            Assert.Equal(now, await second.CutoverAsync(CancellationToken.None));
            Assert.Equal(now.ToUnixTimeMilliseconds(), await ReadCutoverMarkerMs(rig));
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

    [Fact]
    public async Task ARestartDoesNotMoveTheCutoverAndStillMarksTheRestartDay()
    {
        // Regression for the step-11 defect: while TrackingStart was captured per host start,
        // every restart or poll redeploy moved the cutover forward and the restart day was
        // refused forever, silently leaving it without a history row.
        using var rig = new GateRig();
        var firstStart = Moment("2026-10-18T09:00:00Z");
        using (var host = new GateHost(rig, firstStart, "true"))
        {
            var service = host.RolloverService();
            Assert.Equal(firstStart, await service.CutoverAsync(CancellationToken.None));
        }

        // Redeploy the next morning: 2026-10-19 (Madrid) began at 2026-10-18T22:00Z — before the
        // new host start but after the persisted cutover — so it is still the first missable day.
        var restart = Moment("2026-10-19T09:00:00Z");
        using (var host = new GateHost(rig, restart, "true"))
        {
            var service = host.RolloverService();
            Assert.Equal(firstStart, await service.CutoverAsync(CancellationToken.None));
            host.Time.Advance(TimeSpan.FromDays(1)); // 2026-10-20: candidate 2026-10-19.
            Assert.Equal(2, await service.RunTickAsync(CancellationToken.None));
            Assert.Equal(firstStart, await service.CutoverAsync(CancellationToken.None));
        }
        await using (var database = rig.OpenDatabase())
        {
            var rows = await database.DayStatuses.ToListAsync(CancellationToken.None);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("2026-10-19", row.LocalDate));
            // The marker was written once, by the first enabled host, and never moved.
            Assert.Equal(firstStart.ToUnixTimeMilliseconds(), await ReadCutoverMarkerMs(rig));
        }
    }

    [Fact]
    public async Task ASecondServiceInstanceOnTheSameDatabaseReusesThePersistedCutover()
    {
        using var rig = new GateRig();
        var first = Moment("2026-10-18T09:00:00Z");
        var firstTime = new FakeTimeProvider(first);
        var firstService = NewService(rig, firstTime, enabled: true);
        Assert.Equal(first, await firstService.CutoverAsync(CancellationToken.None));
        firstTime.Advance(TimeSpan.FromDays(2)); // 2026-10-20: candidate 19 is the first full day.
        Assert.Equal(2, await firstService.RunTickAsync(CancellationToken.None));

        // Restart two days later: the cutover is the stored instant, not this instance's start.
        var secondTime = new FakeTimeProvider(Moment("2026-10-20T15:00:00Z"));
        var secondService = NewService(rig, secondTime, enabled: true);
        Assert.Equal(first, await secondService.CutoverAsync(CancellationToken.None));
        secondTime.Advance(TimeSpan.FromDays(1)); // 2026-10-21: candidate 2026-10-20.
        Assert.Equal(2, await secondService.RunTickAsync(CancellationToken.None));
        await using (var database = rig.OpenDatabase())
        {
            var dates = (await database.DayStatuses.ToListAsync(CancellationToken.None))
                .Select(row => row.LocalDate).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(["2026-10-19", "2026-10-20"], dates);
            Assert.Equal(first.ToUnixTimeMilliseconds(), await ReadCutoverMarkerMs(rig));
        }
    }

    [Fact]
    public async Task ReEnablingTheGateAfterAKillSwitchKeepsTheOriginalCutover()
    {
        using var rig = new GateRig();
        var start = Moment("2026-10-18T09:00:00Z");
        var first = NewService(rig, new FakeTimeProvider(start), enabled: true);
        Assert.Equal(start, await first.CutoverAsync(CancellationToken.None));

        // Kill switch: a disabled host never reads or writes the marker (C1 contract kept).
        var off = NewService(rig, new FakeTimeProvider(Moment("2026-10-20T09:00:00Z")), enabled: false);
        Assert.Equal(0, await off.RunTickAsync(CancellationToken.None));
        await using (var during = rig.OpenDatabase())
        {
            Assert.Empty(await during.DayStatuses.ToListAsync(CancellationToken.None));
        }

        // Re-enable: the original cutover is read back, not re-anchored to the new host start.
        var back = NewService(rig, new FakeTimeProvider(Moment("2026-10-25T09:00:00Z")), enabled: true);
        Assert.Equal(start, await back.CutoverAsync(CancellationToken.None));
        Assert.Equal(start.ToUnixTimeMilliseconds(), await ReadCutoverMarkerMs(rig));
    }

    [Fact]
    public async Task AnUnreadableCutoverMarkerIsReanchoredWithoutInventingHistory()
    {
        using var rig = new GateRig();
        await using (var seed = rig.OpenDatabase())
        {
            seed.AppMetas.Add(new AppMeta { Key = DayRollover.CutoverKey, Value = "not-an-instant" });
            await seed.SaveChangesAsync(CancellationToken.None);
        }
        var now = Moment("2026-10-18T09:00:00Z");
        var time = new FakeTimeProvider(now);
        var service = NewService(rig, time, enabled: true);
        // Re-anchoring is the safe direction: it only tightens the refusal window.
        Assert.Equal(now, await service.CutoverAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromDays(1)); // 2026-10-19: candidate 18 began before the marker did.
        Assert.Equal(0, await service.RunTickAsync(CancellationToken.None));
        Assert.Equal(now.ToUnixTimeMilliseconds(), await ReadCutoverMarkerMs(rig));
        await using (var database = rig.OpenDatabase())
        {
            Assert.Empty(await database.DayStatuses.ToListAsync(CancellationToken.None));
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
