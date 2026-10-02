namespace PunchClock.Core.Employees;

/// <summary>A staff member who can punch in and out at the kiosk.</summary>
/// <param name="PinHash">Encoded PIN hash produced by <see cref="Security.IPinHasher"/>. Never the PIN itself.</param>
public sealed record Employee(long Id, string FirstName, string LastName, bool IsActive, string PinHash)
{
    public string DisplayName => $"{FirstName} {LastName}";
}

public sealed record NewEmployee(string FirstName, string LastName, string PinHash, DateTimeOffset CreatedAtUtc);
