namespace Palabras.Auth;

internal static partial class AuthLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Resolved request RemoteIpAddress={RemoteIpAddress}")]
    internal static partial void ResolvedClientAddress(ILogger logger, string remoteIpAddress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an unreadable API request body with status {StatusCode}; the caller received the contract's JSON error instead.")]
    internal static partial void RequestBodyRejected(ILogger logger, int statusCode, Exception exception);
}
