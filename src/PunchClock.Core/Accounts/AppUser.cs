namespace PunchClock.Core.Accounts;

public enum UserRole
{
    System,
    Migration,
    Admin,
    Manager,
}

/// <summary>An account that signs in with a password (admin, manager) or a built-in service account.</summary>
/// <param name="PasswordHash">Null only for the system and migration service accounts, which cannot sign in.</param>
public sealed record AppUser(
    long Id,
    string Username,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    string? PasswordHash,
    long? EmployeeId)
{
    public bool IsServiceAccount => Role is UserRole.System or UserRole.Migration;
}

public sealed record NewAppUser(string Username, string DisplayName, UserRole Role, string PasswordHash);
