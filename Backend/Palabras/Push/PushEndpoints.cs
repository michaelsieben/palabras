using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Mvc;
using Palabras.Learn;
using Palabras.Persistence;

namespace Palabras.Push;

// M8 Web Push registration routes (plan §3.1-3.3). Learner-only, exactly like /api/learn/*: the
// cookie's user id selects the owner and no body field ever can. Push is inert until
// Push:Enabled is literally true; while it is off (or the VAPID key is unusable) every route
// answers 503 and no subscription row can appear.
internal static class PushEndpoints
{
    internal const long MaximumBodyBytes = 8 * 1024;
    private const string Policy = "learner";
    private const int MaximumEndpointLength = 1024;
    private const int MaximumDeviceLabelLength = 60;
    private const int P256dhLength = 65;
    private const int AuthLength = 16;
    private const byte UncompressedPoint = 0x04;

    internal static void MapPushEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/push").RequireAuthorization(Policy);
        group.MapGet("/config", ConfigAsync);
        group.MapPut("/subscription", SubscribeAsync);
        group.MapDelete("/subscription", UnsubscribeAsync);
    }

    private static IResult? Unavailable(VapidKeyStore keys)
    {
        if (!keys.Enabled)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "push_disabled");
        }
        return keys.Keys is null ? Error(StatusCodes.Status503ServiceUnavailable, "push_unavailable") : null;
    }

    // The SPA and the service worker's pushsubscriptionchange handler read this. Only the public
    // half and the fingerprint leave the process; the private key never appears in a response.
    private static IResult ConfigAsync(VapidKeyStore keys)
    {
        if (Unavailable(keys) is { } unavailable)
        {
            return unavailable;
        }
        var current = keys.Keys!;
        return Results.Ok(new PushConfigResponse(current.PublicKey, current.KeyId));
    }

    private static async Task<IResult> SubscribeAsync(PushSubscriptionRequest? request, HttpContext context,
        DatabaseSettings settings, VapidKeyStore keys, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (Unavailable(keys) is { } unavailable)
        {
            return unavailable;
        }
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var current = keys.Keys!;
        var validation = Validate(request, context.Request.Headers.UserAgent.ToString());
        if (validation.Error is { } error)
        {
            return Error(StatusCodes.Status400BadRequest, error);
        }
        if (!string.Equals(validation.KeyId, current.KeyId, StringComparison.Ordinal))
        {
            // A rotated key: the client must subscribe again before the server can encrypt to it.
            return Results.Json(new { error = "stale_key", keyId = current.KeyId },
                statusCode: StatusCodes.Status409Conflict);
        }
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var database = settings.CreateContext();
        await using var transaction = await PushWriteTransaction.BeginAsync(database, cancellationToken);
        var row = await database.PushSubscriptions.SingleOrDefaultAsync(
            candidate => candidate.Endpoint == validation.Endpoint, cancellationToken);
        if (row is not null && row.UserId == userId && PushSubscriptionLifecycle.IsRevoked(row)
            && request!.Reactivate != true)
        {
            // Revoked rows stay revoked until the learner explicitly turns reminders back on.
            return Error(StatusCodes.Status409Conflict, "revoked");
        }
        if (row is null)
        {
            row = new PushSubscription
            {
                UserId = userId,
                Endpoint = validation.Endpoint!,
                CreatedAtMs = nowMs,
                UpdatedAtMs = nowMs
            };
            database.PushSubscriptions.Add(row);
        }
        else
        {
            // The same endpoint under another account is the same device after a switch: reassign
            // the row instead of failing on the unique index.
            row.UserId = userId;
            row.UpdatedAtMs = nowMs;
            row.State = PushSubscriptionLifecycle.Active;
            row.LastStatus = null;
        }
        row.P256dh = validation.P256dh!;
        row.Auth = validation.Auth!;
        row.VapidKeyId = current.KeyId;
        if (validation.DeviceLabel is not null)
        {
            row.DeviceLabel = validation.DeviceLabel;
        }
        else if (row.DeviceLabel is null)
        {
            row.DeviceLabel = validation.FallbackLabel;
        }
        await database.SaveChangesAsync(cancellationToken);
        if (validation.ReplacesEndpoint is { } replaces
            && !string.Equals(replaces, validation.Endpoint, StringComparison.Ordinal))
        {
            // The browser says this device's old subscription is gone. Revoke rather than delete so
            // the endpoint stays auditable, and never touch another learner's row.
            var replaced = await database.PushSubscriptions.SingleOrDefaultAsync(
                candidate => candidate.Endpoint == replaces && candidate.UserId == userId,
                cancellationToken);
            if (replaced is not null)
            {
                PushSubscriptionLifecycle.Revoke(replaced, nowMs);
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new PushSubscriptionResponse(row.Id, row.State));
    }

    private static async Task<IResult> UnsubscribeAsync([FromBody] PushUnsubscribeRequest? request,
        HttpContext context, DatabaseSettings settings, VapidKeyStore keys, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (Unavailable(keys) is { } unavailable)
        {
            return unavailable;
        }
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        var endpoint = request?.Endpoint?.Trim();
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaximumEndpointLength)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_endpoint");
        }
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var database = settings.CreateContext();
        await using var transaction = await PushWriteTransaction.BeginAsync(database, cancellationToken);
        var row = await database.PushSubscriptions.SingleOrDefaultAsync(
            candidate => candidate.Endpoint == endpoint, cancellationToken);
        // Idempotent and owner-scoped: absent, or another learner's endpoint, is a silent no-op and
        // never reveals whether that endpoint exists. Revoking also clears a push-service refusal
        // marker (last_status), so a later automatic resync cannot resurrect a device the learner
        // explicitly removed.
        if (row is not null && row.UserId == userId)
        {
            PushSubscriptionLifecycle.Revoke(row, nowMs);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    // Answers the first failure only (with a stable code, never the supplied value) so a client
    // can correct one field at a time without any secret being echoed back.
    private static Validation Validate(PushSubscriptionRequest? request, string? userAgent)
    {
        if (request is null)
        {
            return new Validation("missing_body");
        }
        var endpoint = request.Endpoint?.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            return new Validation("missing_endpoint");
        }
        if (endpoint.Length > MaximumEndpointLength)
        {
            return new Validation("invalid_endpoint");
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return new Validation("invalid_endpoint");
        }
        if (!IsPublicHost(uri.Host))
        {
            return new Validation("invalid_endpoint_host");
        }
        if (request.Keys is null)
        {
            return new Validation("missing_keys");
        }
        if (!VapidKeyStore.TryDecodeBase64Url(request.Keys.P256dh, out var p256dh)
            || p256dh.Length != P256dhLength || p256dh[0] != UncompressedPoint)
        {
            return new Validation("invalid_p256dh");
        }
        if (!VapidKeyStore.TryDecodeBase64Url(request.Keys.Auth, out var auth) || auth.Length != AuthLength)
        {
            return new Validation("invalid_auth");
        }
        if (string.IsNullOrWhiteSpace(request.KeyId))
        {
            return new Validation("invalid_key_id");
        }
        var explicitLabel = request.DeviceLabel?.Trim();
        if (explicitLabel is { Length: 0 })
        {
            explicitLabel = null;
        }
        if (explicitLabel is { Length: > MaximumDeviceLabelLength }
            || (explicitLabel is not null && explicitLabel.Any(char.IsControl)))
        {
            return new Validation("invalid_device_label");
        }
        string? replaces = null;
        if (request.ReplacesEndpoint is not null)
        {
            replaces = request.ReplacesEndpoint.Trim();
            if (replaces.Length is 0 or > MaximumEndpointLength)
            {
                return new Validation("invalid_replaces_endpoint");
            }
        }
        return new Validation(null)
        {
            Endpoint = endpoint,
            KeyId = request.KeyId.Trim(),
            P256dh = request.Keys.P256dh!.Trim(),
            Auth = request.Keys.Auth!.Trim(),
            DeviceLabel = explicitLabel,
            ReplacesEndpoint = replaces,
            FallbackLabel = DeviceLabelFromUserAgent(userAgent)
        };
    }

    // SSRF guard (plan §3.2, R8): the server will POST to this URL, so the host must not be the
    // machine itself or a private network. IP literals are checked directly; a public hostname that
    // resolves to a private address (DNS rebinding) is out of scope for the default posture — the
    // deferred allow-list (D5) is the stronger mitigation.
    private static bool IsPublicHost(string host)
    {
        var candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (IPAddress.TryParse(candidate, out var address))
        {
            return !IsPrivateAddress(address);
        }
        if (candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // A single-label name is an intranet host by definition; real push services are FQDNs.
        return candidate.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPrivateAddress(address.MapToIPv4());
            }
            var v6 = address.GetAddressBytes();
            return (v6[0] & 0xFE) == 0xFC; // unique-local fc00::/7
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 0
            || bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            || address.Equals(IPAddress.Broadcast);
    }

    // The frozen schema has no user-agent column, so the browser's UA is kept as the fallback
    // device label (bounded, control characters removed). A client-supplied label always wins.
    private static string? DeviceLabelFromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }
        var builder = new StringBuilder(Math.Min(userAgent.Length, MaximumDeviceLabelLength));
        var previousSpace = false;
        foreach (var character in userAgent)
        {
            var sanitized = char.IsControl(character) ? ' ' : character;
            var isSpace = char.IsWhiteSpace(sanitized);
            if (isSpace && previousSpace)
            {
                continue;
            }
            builder.Append(isSpace ? ' ' : sanitized);
            previousSpace = isSpace;
            if (builder.Length == MaximumDeviceLabelLength)
            {
                break;
            }
        }
        var label = builder.ToString().Trim();
        return label.Length == 0 ? null : label;
    }

    private static IResult Error(int status, string error) =>
        Results.Json(new { error }, statusCode: status);

    private sealed record Validation(string? Error)
    {
        internal string? Endpoint { get; init; }
        internal string? KeyId { get; init; }
        internal string? P256dh { get; init; }
        internal string? Auth { get; init; }
        internal string? DeviceLabel { get; init; }
        internal string? ReplacesEndpoint { get; init; }
        internal string? FallbackLabel { get; init; }
    }

    // Immediate SQLite transaction, matching DayEndpoints.WriteTransaction: two devices (or a
    // resync racing a user click) serialize on the database instead of both inserting the same
    // endpoint and one failing the unique index.
    private sealed class PushWriteTransaction : IAsyncDisposable
    {
        private readonly DbTransaction _transaction;
        private readonly IDbContextTransaction _scope;

        private PushWriteTransaction(DbTransaction transaction, IDbContextTransaction scope)
        {
            _transaction = transaction;
            _scope = scope;
        }

        internal static async Task<PushWriteTransaction> BeginAsync(PalabrasDbContext database,
            CancellationToken cancellationToken)
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)database.Database.GetDbConnection();
            var transaction = connection.BeginTransaction(deferred: false);
            var scope = database.Database.UseTransaction(transaction)
                ?? throw new InvalidOperationException("EF Core did not accept the SQLite write transaction.");
            return new PushWriteTransaction(transaction, scope);
        }

        internal Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _transaction.DisposeAsync();
        }
    }
}

internal sealed record PushConfigResponse(string PublicKey, string KeyId);

internal sealed record PushKeysRequest(string? P256dh, string? Auth);

internal sealed record PushSubscriptionRequest(
    string? Endpoint, PushKeysRequest? Keys, string? KeyId, string? DeviceLabel,
    string? ReplacesEndpoint, bool? Reactivate);

internal sealed record PushUnsubscribeRequest(string? Endpoint);

internal sealed record PushSubscriptionResponse(int Id, string State);
