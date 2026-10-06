namespace PunchClock.Core.Employees;

/// <summary>A staff member who can punch in and out at the kiosk.</summary>
/// <param name="PinHash">Encoded PIN hash produced by <see cref="Security.IPinHasher"/>. Never the PIN itself.</param>
/// <param name="PinMustChange">
/// The PIN is known to someone else (a legacy plain-text PIN, or one a manager assigned or reset)
/// and must be replaced before the employee's next punch is recorded.
/// </param>
public sealed record Employee(
    long Id,
    string FirstName,
    string LastName,
    bool IsActive,
    string PinHash,
    bool PinMustChange,
    long? LegacyId)
{
    public string DisplayName => $"{FirstName} {LastName}";
}

public sealed record NewEmployee(string FirstName, string LastName, string PinHash, bool PinMustChange);
