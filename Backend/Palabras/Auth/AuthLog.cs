namespace Palabras.Auth;

internal static partial class AuthLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "No auth users are configured. Login is impossible until Auth:Users supplies at least one user with a username, a learner/reviewer role and a password hash.")]
    internal static partial void NoAuthUsersConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an unreadable API request body with status {StatusCode}; the caller received the contract's JSON error instead.")]
    internal static partial void RequestBodyRejected(ILogger logger, int statusCode, Exception exception);
}
