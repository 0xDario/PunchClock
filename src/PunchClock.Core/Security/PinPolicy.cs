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

    /// <summary>
    /// Wrong PINs allowed per employee within <see cref="LockoutWindow"/>; after that the employee
    /// cannot punch or change their PIN until the window passes or a manager resets the PIN.
    /// A 3-digit PIN has only 1,000 values, so the kiosk must not be an unlimited guessing oracle.
    /// </summary>
    public const int MaxFailedAttempts = 5;

    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

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
