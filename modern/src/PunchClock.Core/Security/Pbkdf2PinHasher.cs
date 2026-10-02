using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PunchClock.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a per-PIN random salt. Encoded as
/// <c>pbkdf2-sha256$iterations$salt$hash</c> (base64) so the work factor can be
/// raised later without invalidating stored hashes.
/// </summary>
/// <remarks>
/// A short numeric PIN has a tiny keyspace, so hashing only slows offline guessing;
/// it does not prevent it. It does stop PINs being read in plain text from the file.
/// </remarks>
public sealed class Pbkdf2PinHasher : IPinHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public const int DefaultIterations = 210_000;

    private readonly int _iterations;

    public Pbkdf2PinHasher(int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _iterations = iterations;
    }

    public string Hash(string pin)
    {
        if (!PinPolicy.IsValid(pin))
        {
            throw new ArgumentException(
                $"PIN must be {PinPolicy.MinLength}-{PinPolicy.MaxLength} digits.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(pin, salt, _iterations);
        return string.Join(
            '$',
            Scheme,
            _iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string pin, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(encodedHash);

        var parts = encodedHash.Split('$');
        if (parts.Length != 4
            || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(pin, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations, int length = HashSize) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, length);
}
