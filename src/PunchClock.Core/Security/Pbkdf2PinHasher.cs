using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PunchClock.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a per-PIN random salt. Encoded as
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;key b64&gt;</c>, so the cost can be raised
/// later without invalidating stored hashes.
/// </summary>
/// <remarks>
/// A 3-6 digit PIN has at most a million values, so no hash makes a stolen database safe on its
/// own. Hashing stops casual disclosure (the legacy app stored PINs in plain text); file ACLs and
/// the audit chain carry the rest.
/// </remarks>
public sealed class Pbkdf2PinHasher : IPinHasher
{
    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int KeySize = 32;

    private readonly int _iterations;

    public Pbkdf2PinHasher(int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _iterations = iterations;
    }

    public string Hash(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Derive(pin, salt, _iterations, KeySize);
        return string.Join('$', Scheme, _iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    public bool Verify(string pin, string encodedHash)
    {
        if (pin is null || !TryParse(encodedHash, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        var actual = Derive(pin, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, length);

    private static bool TryParse(string? encoded, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = key = [];

        var parts = encoded?.Split('$');
        if (parts is not [Scheme, var iter, var saltB64, var keyB64]
            || !int.TryParse(iter, NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations < 1)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(saltB64);
            key = Convert.FromBase64String(keyB64);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && key.Length > 0;
    }
}
