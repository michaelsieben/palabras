using System.Globalization;
using System.Security.Cryptography;

namespace Palabras.Accounts;

internal sealed class AccountPasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    internal string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashLength);
        return $"pbkdf2-sha256$v1$i={Iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal bool Verify(string encodedHash, string password)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2-sha256" || parts[1] != "v1" || !parts[2].StartsWith("i=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 210_000 || iterations > 2_000_000)
        {
            return false;
        }
        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            if (salt.Length != SaltLength || expected.Length != HashLength)
            {
                return false;
            }
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashLength);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
