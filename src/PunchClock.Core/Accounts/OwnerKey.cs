using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PunchClock.Core.Accounts;

/// <summary>
/// The site owner's recovery keys. Only public keys are here; each private key stays with its
/// owner, off the kiosk PC, so nobody with access to the PC can recover an admin account alone.
/// Make a key with the PunchClock.OwnerKey tool and add the public key it prints.
/// </summary>
public static class OwnerKeys
{
    /// <summary>Base64 SubjectPublicKeyInfo of each trusted ECDSA P-256 key.</summary>
    public static IReadOnlyList<string> Trusted { get; } =
    [
        // Dario Turchi, site owner; fingerprint a69cf00996d01267. Added 2026-10-21.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBzO1Ltj5YsuRhhU0CGFAVga5TnL3b5Z9/fwvVJiH0pKyLCGljVkxl7EI9JrGsSgSwQgT605OpY+rrJRNpQgPpw==",
    ];
}

/// <summary>
/// One request to recover an admin account, shown on the kiosk as a short code. The owner signs
/// the code with their private key; the signature is the response the kiosk accepts once.
/// </summary>
public sealed class RecoveryChallenge
{
    private const byte Version = 1;
    private const int NonceLength = 11;

    // version (1) + account id (4) + issued, Unix seconds (4) + nonce (11) = 20 bytes, 32 characters.
    private const int PayloadLength = 20;

    private RecoveryChallenge(byte[] payload, long userId, DateTimeOffset issuedUtc)
    {
        Payload = payload;
        UserId = userId;
        IssuedUtc = issuedUtc;
        Code = OwnerKeyCode.Group(OwnerKeyCode.Encode(payload), 4);
    }

    /// <summary>The admin account this challenge recovers.</summary>
    public long UserId { get; }

    /// <summary>When the kiosk issued it, by the database clock, to the second.</summary>
    public DateTimeOffset IssuedUtc { get; }

    /// <summary>What the owner is given, for example <c>0C1A-...</c>.</summary>
    public string Code { get; }

    internal byte[] Payload { get; }

    /// <summary>Set once a recovery with this challenge has been committed; it is never accepted again.</summary>
    public bool IsUsed { get; internal set; }

    internal static RecoveryChallenge Create(long userId, DateTimeOffset issuedUtc)
    {
        if (userId is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var payload = new byte[PayloadLength];
        payload[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), (uint)userId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), checked((uint)issuedUtc.ToUnixTimeSeconds()));
        RandomNumberGenerator.Fill(payload.AsSpan(9, NonceLength));
        return Parse(payload);
    }

    /// <summary>Reads a code typed or pasted by the owner. Case, spaces and dashes do not matter.</summary>
    /// <exception cref="FormatException">Not a challenge code.</exception>
    public static RecoveryChallenge Parse(string code) => Parse(OwnerKeyCode.Decode(code));

    private static RecoveryChallenge Parse(byte[] payload)
    {
        if (payload.Length != PayloadLength || payload[0] != Version)
        {
            throw new FormatException("This is not a PunchClock recovery challenge.");
        }

        var userId = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1));
        var issued = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(5)));
        return new RecoveryChallenge(payload, userId, issued);
    }
}

/// <summary>The signature scheme shared by the kiosk and the owner's signing tool.</summary>
public static class OwnerKeySignature
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("PunchClock admin recovery v1\n");

    /// <summary>Signs a challenge with the owner's private key; the result is the response code.</summary>
    public static string Sign(ECDsa privateKey, RecoveryChallenge challenge) =>
        OwnerKeyCode.Group(OwnerKeyCode.Encode(
            privateKey.SignData(SignedBytes(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)), 5);

    /// <summary>The trusted key that signed <paramref name="response"/> for this challenge, or null.</summary>
    public static string? FindSigner(RecoveryChallenge challenge, string response, IEnumerable<string> trustedPublicKeys)
    {
        byte[] signature;
        try
        {
            signature = OwnerKeyCode.Decode(response);
        }
        catch (FormatException)
        {
            return null;
        }

        var data = SignedBytes(challenge);
        foreach (var publicKey in trustedPublicKeys)
        {
            using var key = ImportPublicKey(publicKey);
            if (key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return publicKey;
            }
        }

        return null;
    }

    /// <summary>Base64 SubjectPublicKeyInfo, the form <see cref="OwnerKeys.Trusted"/> holds.</summary>
    public static string ExportPublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>Short id of a public key for the audit log: the first 8 bytes of its SHA-256, in hex.</summary>
    public static string Fingerprint(string publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)).AsSpan(0, 8));

    /// <exception cref="ArgumentException">Not an ECDSA P-256 public key.</exception>
    public static ECDsa ImportPublicKey(string publicKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (key.KeySize != 256)
            {
                throw new ArgumentException("Owner keys must be ECDSA P-256.", nameof(publicKey));
            }

            return key;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            key.Dispose();
            throw new ArgumentException("Not an owner public key.", nameof(publicKey), ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] SignedBytes(RecoveryChallenge challenge) => [.. Purpose, .. challenge.Payload];
}

/// <summary>
/// Crockford base32: no I, L, O or U, so codes survive being read aloud or retyped. Decoding
/// ignores case, spaces and dashes and reads I and L as 1 and O as 0.
/// </summary>
internal static class OwnerKeyCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }

    /// <exception cref="FormatException">A character outside the alphabet, or trailing bits that are not zero.</exception>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            if (c is '-' || char.IsWhiteSpace(c))
            {
                continue;
            }

            var value = char.ToUpperInvariant(c) switch
            {
                'O' => 0,
                'I' or 'L' => 1,
                var u => Alphabet.IndexOf(u),
            };
            if (value < 0)
            {
                throw new FormatException($"'{c}' cannot appear in a recovery code.");
            }

            buffer = ((buffer << 5) | value) & 0xFFF;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        // Leftover bits only pad the last character; anything else means a mistyped code.
        if (bits >= 5 || (buffer & ((1 << bits) - 1)) != 0)
        {
            throw new FormatException("The code is incomplete or mistyped.");
        }

        return [.. bytes];
    }

    public static string Group(string code, int size) =>
        string.Join('-', Enumerable.Range(0, (code.Length + size - 1) / size)
            .Select(i => code.Substring(i * size, Math.Min(size, code.Length - i * size))));
}
