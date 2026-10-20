using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Palabras.Push;

// Loaded VAPID key material. PublicKey is an uncompressed P-256 point (65 bytes) and PrivateKey
// the matching 32-byte scalar, both base64url without padding (the format Web Push expects).
// KeyId is the first 16 lowercase hex characters of SHA-256 over the uncompressed public key; it
// is the only key-derived value an operator or a log line ever sees.
internal sealed record VapidKeys(string PublicKey, string PrivateKey, string KeyId, DateTimeOffset CreatedAt);

// M8 VAPID key lifecycle (plan §2.2, D1). One JSON file per deployment, generated on first
// start with push enabled and then never rewritten. A file that exists but cannot be read is a
// hard configuration failure for push FEATURES only: the app keeps serving, `GET
// /api/push/config` answers 503, and the operator gets one clear log line. It is never silently
// regenerated — that would orphan every subscription bound to the old key.
internal sealed class VapidKeyStore
{
    private const int PublicKeyLength = 65;
    private const int PrivateKeyLength = 32;
    private const byte UncompressedPoint = 0x04;
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web);

    private readonly PushSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<VapidKeyStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VapidKeys? _keys;
    private string? _failure;
    private bool _initialized;
    private bool _generated;

    public VapidKeyStore(PushSettings settings, TimeProvider time, ILogger<VapidKeyStore> logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    internal bool Enabled => _settings.Enabled;

    internal string KeyPath => _settings.VapidKeyPath;

    // Null until a successful load. Endpoints must treat null as "push unavailable".
    internal VapidKeys? Keys => _keys;

    // Operator-facing reason when an enabled key file could not be used; never key material.
    internal string? Failure => _failure;

    // True only when this start published a new key file. Startup uses it to warn that rows which
    // carry another fingerprint are now orphaned; a race that loses to another process reads the
    // winner's key and reports false.
    internal bool GeneratedOnLoad => _generated;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            if (!_settings.Enabled)
            {
                return;
            }
            try
            {
                var (keys, created) =
                    await LoadOrCreateAsync(_settings.VapidKeyPath, _time, cancellationToken);
                _keys = keys;
                _generated = created;
                _failure = null;
                if (created)
                {
                    _logger.LogInformation(
                        "VAPID key generated at {Path} (key_id {KeyId}). Back this file up with the database; it is never regenerated silently.",
                        _settings.VapidKeyPath, _keys.KeyId);
                }
                else
                {
                    _logger.LogInformation("VAPID key loaded from {Path} (key_id {KeyId}).",
                        _settings.VapidKeyPath, _keys.KeyId);
                }
            }
            catch (Exception exception) when (IsConfigurationFailure(exception))
            {
                _keys = null;
                _generated = false;
                _failure = exception.Message;
                _logger.LogError(
                    "Push features are disabled: the VAPID key file {Path} could not be used ({Reason}). " +
                    "No key was regenerated. Restore the file from backup, or delete it deliberately to rotate keys, then restart.",
                    _settings.VapidKeyPath, exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Used by `push status` and by the lifetime above. Read-only: a missing file returns null and
    // is never created here.
    internal static async Task<VapidKeys?> ReadIfPresentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return await ReadAsync(path, cancellationToken);
    }

    // Created reports whether this call published the file: a lost write race reads the winner's
    // key back and therefore reports false, so startup never warns about a key it did not mint.
    internal static async Task<(VapidKeys Keys, bool Created)> LoadOrCreateAsync(string path, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return (await ReadAsync(path, cancellationToken), false);
        }
        var generated = Generate(time);
        await WriteAtomicallyAsync(path, generated, cancellationToken);
        // Re-read the published file: when two processes raced, the winner's key is the one on
        // disk and both must report the same fingerprint.
        var published = await ReadAsync(path, cancellationToken);
        return (published, string.Equals(published.KeyId, generated.KeyId, StringComparison.Ordinal));
    }

    internal static VapidKeys Generate(TimeProvider time)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[PublicKeyLength];
        publicKey[0] = UncompressedPoint;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return new VapidKeys(EncodeBase64Url(publicKey), EncodeBase64Url(parameters.D!),
            KeyIdFor(publicKey), time.GetUtcNow());
    }

    internal static string KeyIdFor(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return Convert.ToHexString(SHA256.HashData(publicKey))[..16].ToLowerInvariant();
    }

    internal static async Task<VapidKeys> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The VAPID key file could not be read: " + exception.Message, exception);
        }
        VapidKeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VapidKeyFile>(bytes, FileJson);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The VAPID key file is not valid JSON.");
        }
        if (file is null || file.V != 1)
        {
            throw new InvalidDataException("The VAPID key file is missing its version marker.");
        }
        if (string.IsNullOrWhiteSpace(file.PublicKey) || string.IsNullOrWhiteSpace(file.PrivateKey))
        {
            throw new InvalidDataException("The VAPID key file is missing key material.");
        }
        byte[] publicKey;
        byte[] privateKey;
        try
        {
            publicKey = DecodeBase64Url(file.PublicKey, "public key");
            privateKey = DecodeBase64Url(file.PrivateKey, "private key");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The VAPID key file contains malformed base64url key material.");
        }
        if (publicKey.Length != PublicKeyLength || publicKey[0] != UncompressedPoint)
        {
            throw new InvalidDataException("The VAPID public key is not an uncompressed P-256 point.");
        }
        if (privateKey.Length != PrivateKeyLength)
        {
            throw new InvalidDataException("The VAPID private key is not a 32-byte P-256 scalar.");
        }
        ValidatePair(publicKey, privateKey);
        if (!DateTimeOffset.TryParse(file.CreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var createdAt))
        {
            throw new InvalidDataException("The VAPID key file has no valid creation instant.");
        }
        return new VapidKeys(file.PublicKey, file.PrivateKey, KeyIdFor(publicKey), createdAt);
    }

    // Best-effort base64url decoder for client payloads: true only for the URL-safe alphabet
    // (optional trailing padding), so a standard-base64 or whitespace-bearing key is rejected.
    internal static bool TryDecodeBase64Url(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        var trimmed = value.TrimEnd('=');
        if (trimmed.Length % 4 == 1)
        {
            return false;
        }
        var buffer = new char[trimmed.Length];
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            buffer[index] = character switch
            {
                '-' => '+',
                '_' => '/',
                >= 'A' and <= 'Z' => character,
                >= 'a' and <= 'z' => character,
                >= '0' and <= '9' => character,
                _ => '\0'
            };
            if (buffer[index] == '\0')
            {
                return false;
            }
        }
        var padded = new string(buffer) + new string('=', (4 - trimmed.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string EncodeBase64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value, string what)
    {
        if (!TryDecodeBase64Url(value, out var bytes))
        {
            throw new FormatException("The " + what + " is not base64url.");
        }
        return bytes;
    }

    // A restored or hand-edited file with mismatched halves must fail at startup, not at 22:00
    // when the first push is rejected. Sign/verify proves the private key belongs to the point.
    private static void ValidatePair(byte[] publicKey, byte[] privateKey)
    {
        try
        {
            using var signer = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateKey
            });
            var challenge = Encoding.UTF8.GetBytes("palabras-vapid-key-check");
            var signature = signer.SignData(challenge, HashAlgorithmName.SHA256);
            using var verifier = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] }
            });
            if (!verifier.VerifyData(challenge, signature, HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException("The VAPID public and private keys do not belong together.");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("The VAPID key pair is not a valid P-256 pair.", exception);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, VapidKeys keys, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var file = new VapidKeyFile
            {
                V = 1,
                PublicKey = keys.PublicKey,
                PrivateKey = keys.PrivateKey,
                CreatedAt = keys.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            };
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                // 0600 from the first byte: the private key never exists in a wider-mode file, not
                // even during the few milliseconds before a chmod would have run.
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, file, FileJson, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process published first; its key is authoritative and is read back.
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsConfigurationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or CryptographicException or FormatException or NotSupportedException or ArgumentException;

    private sealed class VapidKeyFile
    {
        public int V { get; set; }
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}
