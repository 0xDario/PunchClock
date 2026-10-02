namespace PunchClock.Core.Security;

public interface IPinHasher
{
    string Hash(string pin);

    bool Verify(string pin, string encodedHash);
}
