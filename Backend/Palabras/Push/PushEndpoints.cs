using System.Data.Common;
using System.Globalization;
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
// Push:Enabled is literally true; while it is off (or the VAPID key is unusable) every mutating
// route answers 503 and no subscription row can appear. GET /api/push/subscriptions is the one
// exception: it describes stored configuration, so it answers 200 with whatever rows exist.
internal static class PushEndpoints
{
    internal const long MaximumBodyBytes = 8 * 1024;
    // A learner's device list is a handful of browsers. The cap keeps a long-lived account (every
    // endpoint replacement leaves a rejected row behind) from answering an unbounded body, and the
    // response says `truncated` when it applies instead of silently hiding rows.
    internal const int MaximumListedSubscriptions = 50;
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
        group.MapGet("/subscriptions", ListAsync);
        group.MapDelete("/subscriptions/{id:int}", RevokeAsync);
        group.MapPut("/subscription", SubscribeAsync);
        group.MapDelete("/subscription", UnsubscribeAsync);
        group.MapPost("/test", TestAsync);
    }

    // Shared with ReminderEndpoints: the reminder surfaces are inert while push is off, so they
    // answer the same 503 codes instead of keeping a half-configured feature alive.
    internal static IResult? Unavailable(VapidKeyStore keys)
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

    // Read-only device list for the U8 settings screen (plan §3.9 / U8): one row per subscription the
    // calling account owns, with an opaque id, the stored label, the stored state, the last push-service
    // status and both instants. Nothing in the payload is a secret: the endpoint and the two encryption
    // keys never leave the database, and this route logs nothing at all.
    //
    // `isCurrent` is decided from `?currentId=<id>`, the id PUT /subscription already returns to the
    // caller, so "this device" is an equality on an integer the client was handed. It is deliberately
    // not derived from the endpoint (a bearer-like secret that would land in URL and access logs) and
    // not from the user agent (two phones of the same model share one string — that would be
    // fingerprinting, not identification). An absent, unknown or another account's id leaves every row
    // `false`, which is the honest answer: the server cannot know which browser is asking.
    //
    // No 503 gate on purpose (review item: pairing with GET /config must not turn "push is off" into an
    // error): with push off or an unusable VAPID key the answer is still 200 with the rows that are
    // stored, because the list describes configuration, not delivery. That is also why the state is
    // reported verbatim — `stale_key` means "this row needs a resubscribe", which the UI can say
    // without guessing.
    private static async Task<IResult> ListAsync(HttpContext context, string? currentId,
        DatabaseSettings settings, CancellationToken cancellationToken)
    {
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        int? current = null;
        if (!string.IsNullOrEmpty(currentId))
        {
            if (!int.TryParse(currentId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_current_id");
            }
            current = parsed;
        }
        await using var database = settings.CreateContext();
        // Active devices first, then rows that need a resubscribe, then the learner's own opt-outs; each
        // group newest-first with the id as a total-order tiebreaker, so two identical requests always
        // answer the same sequence. The projection never selects the endpoint, p256dh or auth columns.
        var rows = await database.PushSubscriptions.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.State == PushSubscriptionLifecycle.Active
                ? 0
                : row.State == PushSubscriptionLifecycle.Rejected ? 2 : 1)
            .ThenByDescending(row => row.UpdatedAtMs)
            .ThenByDescending(row => row.Id)
            .Take(MaximumListedSubscriptions + 1)
            .Select(row => new
            {
                row.Id,
                row.DeviceLabel,
                row.State,
                row.LastStatus,
                row.CreatedAtMs,
                row.UpdatedAtMs
            })
            .ToListAsync(cancellationToken);
        var truncated = rows.Count > MaximumListedSubscriptions;
        var items = rows.Take(MaximumListedSubscriptions)
            .Select(row => new PushDeviceResponse(
                row.Id,
                row.DeviceLabel,
                row.State,
                row.LastStatus,
                DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtMs),
                DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtMs),
                row.Id == current))
            .ToArray();
        return Results.Ok(new PushDevicesResponse(items, truncated));
    }

    // The mutation that pairs with the list (plan §3.9 / U8): switch one stored device off by the
    // opaque id the list hands out. The id is not a credential — it only ever selects among the
    // calling account's rows, and another learner's id answers the very same 404 as an id that
    // never existed, so this route cannot be used to probe for rows (a 403 would be an oracle).
    // Idempotent: a row the learner already switched off is a no-op success, not a conflict.
    //
    // 204, matching DELETE /api/push/subscription, rather than the updated list item: an empty
    // body is what makes "just revoked" and "already revoked" byte-identical (a returned row would
    // differ in `updatedAt` and turn the retry into an observable second event), and no response
    // member can leak the endpoint or the two keys because nothing is written back at all. The
    // settings screen refetches GET /subscriptions, whose shape it already renders.
    private static async Task<IResult> RevokeAsync(int id, HttpContext context, DatabaseSettings settings,
        VapidKeyStore keys, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (Unavailable(keys) is { } unavailable)
        {
            return unavailable;
        }
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        // No row can carry a non-positive id, and answering 404 without a query keeps every
        // unusable id on the same path as an id this account does not own.
        if (id <= 0)
        {
            return Error(StatusCodes.Status404NotFound, "subscription_not_found");
        }
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var database = settings.CreateContext();
        await using var transaction = await PushWriteTransaction.BeginAsync(database, cancellationToken);
        var row = await database.PushSubscriptions.SingleOrDefaultAsync(
            candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (row is null)
        {
            return Error(StatusCodes.Status404NotFound, "subscription_not_found");
        }
        // Already an explicit opt-out: leave `updatedAt` alone so the second click changes nothing.
        // A push-service refusal (`rejected` plus a status) becomes an opt-out here — the learner's
        // decision overrides the sender's, and Revoke clears `last_status`, the same rule U4 keeps.
        // In both cases the row stays for the audit trail and is re-activatable only by a
        // `reactivate: true` PUT (U3's 409 `revoked`), never by an ordinary resync.
        if (!PushSubscriptionLifecycle.IsRevoked(row))
        {
            PushSubscriptionLifecycle.Revoke(row, nowMs);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
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

    // The "Enviar prueba" route (plan §3.4): one test notification to one named device, or to every
    // device the caller has. It is a real delivery, so it goes through the same sender and the same
    // 404/410/403/5xx accounting as a reminder; it deliberately records no reminder decision, and
    // the per-learner cooldown keeps a stuck client from ringing every device on every retry.
    private static async Task<IResult> TestAsync([FromBody] PushTestRequest? request, HttpContext context,
        VapidKeyStore keys, PushTestSender tests, PushTestCooldown cooldown, CancellationToken cancellationToken)
    {
        if (Unavailable(keys) is { } unavailable)
        {
            return unavailable;
        }
        if (LearnEndpoints.CurrentUserId(context) is not { } userId)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }
        string? endpoint = null;
        if (request?.Endpoint is not null)
        {
            // Only ever used as an equality key against a row this learner registered, so the
            // https/SSRF validation of PUT /subscription is not repeated here — nothing is ever
            // fetched from a value the client sends to this route.
            endpoint = request.Endpoint.Trim();
            if (endpoint.Length is 0 or > MaximumEndpointLength)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_endpoint");
            }
        }
        var targets = await tests.ResolveTargetsAsync(userId, endpoint, cancellationToken);
        if (targets.Count == 0)
        {
            // An unnamed "all my devices" with nothing to send to is a valid empty answer; a named
            // endpoint that is not active is the caller asking about a device we no longer have.
            return endpoint is null
                ? Results.Ok(new PushTestResponse([]))
                : Error(StatusCodes.Status404NotFound, "no_active_subscription");
        }
        if (!cooldown.TryAcquire(userId, out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited");
        }
        var results = await tests.SendAsync(targets, cancellationToken);
        return Results.Ok(new PushTestResponse(results
            .Select(result => new PushTestResultResponse(result.SubscriptionId,
                PushOutcomeClassifier.Wire(result.Outcome), result.StatusCode))
            .ToArray()));
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
    // deferred allow-list (D5) is the stronger mitigation, and the deferred sender (U4) must use
    // AllowAutoRedirect=false so a public host cannot bounce the POST to a private one.
    private static bool IsPublicHost(string host)
    {
        var candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        // "localhost." and "localhost" are the same host: without normalising, the trailing dot
        // would slip past every literal check below and a dotted IP literal past IPAddress.TryParse.
        candidate = candidate.TrimEnd('.').ToLowerInvariant();
        if (candidate.Length == 0)
        {
            return false;
        }
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
    // endpoint and one failing the unique index. ReminderEndpoints reuses it for the settings
    // upsert so two open panels cannot race the user_id primary key.
    internal sealed class PushWriteTransaction : IAsyncDisposable
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

// One device in GET /api/push/subscriptions. Secret-free by construction: there is no member for the
// endpoint or for either encryption key, so a serialization mistake cannot leak one. `state` is the
// stored value verbatim (`active` / `stale_key` / `rejected`); `lastStatus` is the HTTP status of the
// push service's last refusal, and null means "the learner switched this device off" (or nothing has
// been attempted yet). `deviceLabel` is null only for a row registered without a user agent.
internal sealed record PushDeviceResponse(
    int Id,
    string? DeviceLabel,
    string State,
    int? LastStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrent);

internal sealed record PushDevicesResponse(IReadOnlyList<PushDeviceResponse> Items, bool Truncated);

internal sealed record PushKeysRequest(string? P256dh, string? Auth);

internal sealed record PushSubscriptionRequest(
    string? Endpoint, PushKeysRequest? Keys, string? KeyId, string? DeviceLabel,
    string? ReplacesEndpoint, bool? Reactivate);

internal sealed record PushUnsubscribeRequest(string? Endpoint);

internal sealed record PushSubscriptionResponse(int Id, string State);

// Body of POST /api/push/test: absent endpoint means "every active device of mine".
internal sealed record PushTestRequest(string? Endpoint);

// One line per attempted device. `status` is the push service's answer, or 0 when no HTTP response
// arrived (timeout, DNS, TLS), matching what the database records.
internal sealed record PushTestResultResponse(int Id, string Outcome, int? Status);

internal sealed record PushTestResponse(IReadOnlyList<PushTestResultResponse> Results);
