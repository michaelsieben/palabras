using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Palabras.Accounts;
using Palabras.Persistence;
using Xunit;

namespace Palabras.Tests;

public sealed class UserCliTests : IDisposable
{
    private const string Password = "original test password";
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "palabras-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseSettings _settings;

    public UserCliTests()
    {
        _settings = new DatabaseSettings(Path.Combine(_directory, "palabras.db"));
    }

    [Fact]
    public async Task CreateThenListShowsNormalizedUsernameAndRoleButNeverHash()
    {
        Assert.Equal(0, (await Run(["user", "create", "owner", "--username", "  My.Owner  ", "--password-stdin"], Password)).Exit);
        var result = await Run(["user", "list"]);
        Assert.Equal(0, result.Exit);
        Assert.Contains("owner\tmy.owner\t", result.Output);
        Assert.Contains("never", result.Output);
        Assert.DoesNotContain(Password, result.Output);
        Assert.DoesNotContain("pbkdf2", result.Output);
    }

    [Fact]
    public async Task CreatingSecondAccountForSameRoleFails()
    {
        await CreateOwner();
        var result = await Run(["user", "create", "owner", "--username", "another", "--password-stdin"], Password);
        Assert.Equal(1, result.Exit);
        Assert.Contains("role 'owner' already exists", result.Error);
        Assert.Single(await ReadUsers());
    }

    [Fact]
    public async Task DuplicateUsernameDifferingOnlyInCaseFails()
    {
        await CreateOwner();
        var result = await Run(["user", "create", "reviewer", "--username", " OwNeR ", "--password-stdin"], Password);
        Assert.Equal(1, result.Exit);
        Assert.Contains("case-insensitive", result.Error);
        Assert.Single(await ReadUsers());
    }

    [Fact]
    public async Task RenamePreservesIdCreationPasswordRoleAndHistory()
    {
        await CreateOwner();
        var before = Assert.Single(await ReadUsers());
        await using var database = _settings.CreateContext();
        await database.Database.ExecuteSqlRawAsync("CREATE TABLE regression_history (user_id INTEGER NOT NULL REFERENCES users(id), note TEXT NOT NULL);", CancellationToken.None);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO regression_history(user_id, note) VALUES ({before.Id}, 'keep me');", CancellationToken.None);
        Assert.Equal(0, (await Run(["user", "set-username", "OWNER", "--username", "renamed"])).Exit);
        var after = Assert.Single(await ReadUsers());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.LastLoginAt, after.LastLoginAt);
        Assert.Equal("renamed", after.Username);
        Assert.True(after.UpdatedAt >= before.UpdatedAt);
        var history = await database.Database.SqlQueryRaw<int>("SELECT user_id AS Value FROM regression_history").SingleAsync(CancellationToken.None);
        Assert.Equal(after.Id, history);
    }

    [Fact]
    public async Task SetPasswordChangesHashAndOldPasswordNoLongerVerifies()
    {
        await CreateOwner();
        var before = Assert.Single(await ReadUsers());
        const string replacement = "replacement test password";
        Assert.Equal(0, (await Run(["user", "set-password", "OWNER", "--password-stdin"], replacement)).Exit);
        var after = Assert.Single(await ReadUsers());
        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        var hasher = new AccountPasswordHasher();
        Assert.False(hasher.Verify(after.PasswordHash, Password));
        Assert.True(hasher.Verify(after.PasswordHash, replacement));
    }

    [Fact]
    public async Task ResetYesDestroysAllTablesThenRecreatesEmptySchemaWithoutUnlinkingLiveFile()
    {
        await CreateOwner();
        await using var database = _settings.CreateContext();
        await database.Database.OpenConnectionAsync(CancellationToken.None);
        await database.Database.ExecuteSqlRawAsync("CREATE TABLE future_feedback (user_id INTEGER REFERENCES users(id));", CancellationToken.None);
        Assert.Equal(0, (await Run(["user", "reset", "--yes"])).Exit);
        Assert.Empty(await database.Users.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Empty(await ReadUsers());
        Assert.Equal(0, await database.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name='future_feedback'").SingleAsync(CancellationToken.None));
        Assert.Single(await database.Database.GetAppliedMigrationsAsync(CancellationToken.None));
        await CreateOwner();
        Assert.Single(await ReadUsers());
    }

    [Fact]
    public async Task ResetWithoutYesFailsWithoutCreatingOrChangingDatabase()
    {
        var absent = await Run(["user", "reset"]);
        Assert.Equal(1, absent.Exit);
        Assert.Contains("ALL database data", absent.Error);
        Assert.False(Directory.Exists(_directory));
        await CreateOwner();
        Assert.Equal(1, (await Run(["user", "reset"])).Exit);
        Assert.Single(await ReadUsers());
    }

    [Fact]
    public void HashIsVersionedSaltedAndNeverContainsPlaintext()
    {
        var hasher = new AccountPasswordHasher();
        var first = hasher.Hash(Password);
        var second = hasher.Hash(Password);
        Assert.StartsWith("pbkdf2-sha256$v1$i=600000$", first);
        Assert.DoesNotContain(Password, first);
        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify(first, Password));
        Assert.False(hasher.Verify(first, "wrong password"));
        Assert.False(hasher.Verify(first.Replace("$v1$", "$v2$", StringComparison.Ordinal), Password));
        Assert.False(hasher.Verify("pbkdf2-sha256$v1$i=600000$bad$bad", Password));
    }

    [Fact]
    public async Task InteractivePasswordIsRequestedTwiceAndMismatchDoesNotCreateAccount()
    {
        var prompts = new Queue<string>([Password, "different password"]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await UserCli.RunAsync(["user", "create", "owner", "--username", "owner"], _settings,
            TextReader.Null, output, error, CancellationToken.None, () => prompts.Dequeue()));
        Assert.Empty(prompts);
        Assert.Contains("Passwords do not match", error.ToString());
        Assert.Empty(await ReadUsers());
        prompts = new Queue<string>([Password, Password]);
        Assert.Equal(0, await UserCli.RunAsync(["user", "create", "owner", "--username", "owner"], _settings,
            TextReader.Null, output, error, CancellationToken.None, () => prompts.Dequeue()));
        Assert.Empty(prompts);
        Assert.Single(await ReadUsers());
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("invalid name")]
    [InlineData("ñusername")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidUsernamesAreRejected(string username)
    {
        Assert.Equal(1, (await Run(["user", "create", "owner", "--username", username, "--password-stdin"], Password)).Exit);
        Assert.Empty(await ReadUsers());
    }

    [Fact]
    public async Task RoleAndUsernameChecksAreEnforcedInDatabaseNotOnlyCli()
    {
        await CreateOwner();
        await using var database = _settings.CreateContext();
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync("UPDATE users SET role='admin';", CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync("UPDATE users SET username='MixedCase';", CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync("UPDATE users SET username='has space';", CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(() => database.Database.ExecuteSqlRawAsync("INSERT INTO users(username, role, password_hash, created_at, updated_at) SELECT 'different', role, password_hash, created_at, updated_at FROM users;", CancellationToken.None));
        Assert.Single(await ReadUsers());
    }

    [Fact]
    public async Task RepeatedAndConcurrentInitializationIsIdempotent()
    {
        await Task.WhenAll(DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None), DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None));
        await CreateOwner();
        await DatabaseLifecycle.InitializeAsync(_settings, CancellationToken.None);
        await using var database = _settings.CreateContext();
        Assert.Single(await database.Database.GetAppliedMigrationsAsync(CancellationToken.None));
        Assert.Single(await ReadUsers());
        Assert.Equal("wal", Assert.Single(await database.Database.SqlQueryRaw<string>("PRAGMA journal_mode;").ToListAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task MissingAccountsAndPasswordArgvAreRejected()
    {
        Assert.Equal(1, (await Run(["user", "set-password", "unknown", "--password-stdin"], Password)).Exit);
        Assert.Equal(1, (await Run(["user", "set-username", "unknown", "new-name"])).Exit);
        var result = await Run(["user", "create", "owner", "--username", "owner", "--password", Password]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Never pass passwords in argv", result.Error);
        Assert.DoesNotContain(Password, result.Error);
        Assert.Empty(await ReadUsers());
    }

    [Fact]
    public void DataDirectoryDefaultAndConnectionStringOverrideResolveCorrectly()
    {
        Assert.Equal(Path.GetFullPath("/data/palabras.db"), DatabaseSettings.FromConfiguration(new ConfigurationBuilder().Build()).FilePath);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIR"] = _directory }).Build();
        Assert.Equal(_settings.FilePath, DatabaseSettings.FromConfiguration(config).FilePath);
        config["ConnectionStrings:Default"] = "Data Source=other.db";
        Assert.Equal(Path.Combine(_directory, "other.db"), DatabaseSettings.FromConfiguration(config).FilePath);
        config["ConnectionStrings:Default"] = "Data Source=" + Path.Combine(_directory, "absolute.db");
        Assert.Equal(Path.Combine(_directory, "absolute.db"), DatabaseSettings.FromConfiguration(config).FilePath);
    }

    private async Task CreateOwner() => Assert.Equal(0, (await Run(["user", "create", "owner", "--username", "owner", "--password-stdin"], Password)).Exit);

    private async Task<List<User>> ReadUsers()
    {
        await using var database = _settings.CreateContext();
        return await database.Users.AsNoTracking().ToListAsync(CancellationToken.None);
    }

    private async Task<(int Exit, string Output, string Error)> Run(string[] args, string password = "")
    {
        using var input = new StringReader(password + "\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await UserCli.RunAsync(args, _settings, input, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
