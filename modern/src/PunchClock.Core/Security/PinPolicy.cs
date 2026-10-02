namespace PunchClock.Core.Security;

public static class PinPolicy
{
    // Legacy PINs are 3-6 digits; keep 3 as the floor so migrated PINs stay valid.
    public const int MinLength = 3;
    public const int MaxLength = 8;

    /// <summary>PINs are digit strings, not integers: "0123" and "123" are different PINs.</summary>
    public static bool IsValid(string? pin) =>
        pin is not null
        && pin.Length is >= MinLength and <= MaxLength
        && pin.All(char.IsAsciiDigit);
}
