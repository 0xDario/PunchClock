namespace PunchClock.Core.Security;

public interface IPinHasher
{
    /// <summary>Returns a self-describing encoded hash (scheme, cost, salt, key).</summary>
    string Hash(string pin);

    /// <summary>Constant-time check of <paramref name="pin"/> against an encoded hash. Malformed hashes never verify.</summary>
    bool Verify(string pin, string encodedHash);
}
