namespace PunchClock.Core.Security;

/// <summary>
/// PINs are strings of ASCII digits. Leading zeros are significant: "0123" and "123" are
/// different PINs (the legacy app stored them as integers and could not tell them apart).
/// </summary>
public static class PinPolicy
{
    // Matches the legacy kiosk (3-6 digits) so existing staff PINs stay valid after migration.
    public const int MinLength = 3;
    public const int MaxLength = 6;

    /// <returns>null when valid, otherwise a user-facing reason.</returns>
    public static string? Validate(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < MinLength || pin.Length > MaxLength)
        {
            return $"PIN must be {MinLength}-{MaxLength} digits.";
        }

        return pin.All(char.IsAsciiDigit) ? null : "PIN must contain digits only.";
    }
}
