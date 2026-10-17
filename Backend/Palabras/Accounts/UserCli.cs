using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palabras.Persistence;

namespace Palabras.Accounts;

internal static class UserCli
{
    internal static async Task<int> RunAsync(string[] args, DatabaseSettings settings, TextReader input, TextWriter output,
        TextWriter error, CancellationToken cancellationToken, Func<string>? readHiddenPassword = null)
    {
        try
        {
            // Parse BEFORE opening the DB: malformed commands, especially reset without --yes, have no side effects.
            var command = Parse(args);
            if (command.Verb == "reset")
            {
                await output.WriteLineAsync("WARNING: Destroying ALL database data: accounts, review history, SRS state, feedback and sessions. This cannot be undone.");
                await DatabaseLifecycle.ResetAsync(settings, cancellationToken);
                await output.WriteLineAsync("Database reset complete. All database data destroyed; no accounts remain.");
                return 0;
            }
            await DatabaseLifecycle.InitializeAsync(settings, cancellationToken);
            await using var database = settings.CreateContext();
            var accounts = new AccountService(database, new AccountPasswordHasher());
            switch (command.Verb)
            {
                case "create":
                    await accounts.CreateAsync(command.Role!, command.Username!, await ReadPasswordAsync(command.PasswordStdin, input, output, readHiddenPassword, cancellationToken), cancellationToken);
                    await output.WriteLineAsync($"Created {command.Role} account '{AccountService.NormalizeUsername(command.Username!)}'.");
                    break;
                case "set-username":
                    await accounts.SetUsernameAsync(command.OldUsername!, command.Username!, cancellationToken);
                    await output.WriteLineAsync($"Renamed account to '{AccountService.NormalizeUsername(command.Username!)}' (same id; history preserved).");
                    break;
                case "set-password":
                    await accounts.SetPasswordAsync(command.Username!, await ReadPasswordAsync(command.PasswordStdin, input, output, readHiddenPassword, cancellationToken), cancellationToken);
                    await output.WriteLineAsync("Password updated. No restart required.");
                    break;
                case "list":
                    await output.WriteLineAsync("ROLE\tUSERNAME\tCREATED (UTC)\tLAST LOGIN (UTC)");
                    foreach (var user in await accounts.ListAsync(cancellationToken))
                    {
                        await output.WriteLineAsync($"{user.Role}\t{user.Username}\t{user.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}\t{user.LastLoginAt?.ToString("O", CultureInfo.InvariantCulture) ?? "never"}");
                    }
                    break;
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or DbUpdateException or SqliteException)
        {
            // Never print raw EF errors: they may include parameter values or account hashes.
            var message = exception is SqliteException or DbUpdateException
                ? "Database operation failed. Check username/role uniqueness, permissions and concurrent access."
                : exception.Message;
            await error.WriteLineAsync("Error: " + message);
            return 1;
        }
    }

    private static Command Parse(string[] args)
    {
        if (args.Any(value => value == "--password" || value.StartsWith("--password=", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Never pass passwords in argv. Use the hidden prompt or --password-stdin.");
        }
        return args switch
        {
            ["user", "create", var role, "--username", var username] => new("create", Username: username, Role: role),
            ["user", "create", var role, "--username", var username, "--password-stdin"] => new("create", Username: username, Role: role, PasswordStdin: true),
            ["user", "set-username", var old, "--username", var username] => new("set-username", Username: username, OldUsername: old),
            ["user", "set-username", var old, var username] when !username.StartsWith("--", StringComparison.Ordinal) => new("set-username", Username: username, OldUsername: old),
            ["user", "set-password", var username] => new("set-password", Username: username),
            ["user", "set-password", var username, "--password-stdin"] => new("set-password", Username: username, PasswordStdin: true),
            ["user", "list"] => new("list"),
            ["user", "reset", "--yes"] => new("reset"),
            ["user", "reset", ..] => throw new ArgumentException("Reset destroys ALL database data. To confirm, run 'user reset --yes'."),
            _ => throw new ArgumentException("Usage: user create <owner|reviewer> --username <name> [--password-stdin] | set-username <old> --username <new> | set-password <username> [--password-stdin] | list | reset --yes")
        };
    }

    private static async Task<string> ReadPasswordAsync(bool passwordStdin, TextReader input, TextWriter output,
        Func<string>? readHiddenPassword, CancellationToken cancellationToken)
    {
        if (passwordStdin)
        {
            return await input.ReadLineAsync(cancellationToken) ?? throw new ArgumentException("Supply one password line on stdin.");
        }
        if (readHiddenPassword == null)
        {
            throw new ArgumentException("Interactive password entry requires a TTY (docker exec -it), or use --password-stdin with redirected input.");
        }
        await output.WriteAsync("Password (12-1024 characters, hidden): ");
        await output.FlushAsync(cancellationToken);
        var password = readHiddenPassword();
        await output.WriteAsync("Confirm password (hidden): ");
        await output.FlushAsync(cancellationToken);
        if (password != readHiddenPassword())
        {
            throw new ArgumentException("Passwords do not match. Account unchanged.");
        }
        return password;
    }

    internal static string ReadHiddenPassword()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            {
                value.Length--;
            }
            else if (!char.IsControl(key.KeyChar) && value.Length < 1025)
            {
                value.Append(key.KeyChar);
            }
        }
    }

    private sealed record Command(string Verb, string? Username = null, string? OldUsername = null, string? Role = null, bool PasswordStdin = false);
}
