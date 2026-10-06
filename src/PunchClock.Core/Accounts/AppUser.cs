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
/// <param name="EmployeeId">The employee this person also punches as, if any; they cannot correct those punches.</param>
/// <param name="MustChangePassword">Set by an admin-chosen password; the owner must replace it before anything else.</param>
public sealed record AppUser(
    long Id,
    string Username,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    string? PasswordHash,
    long? EmployeeId,
    bool MustChangePassword)
{
    public bool IsServiceAccount => Role is UserRole.System or UserRole.Migration;
}

/// <param name="MustChangePassword">True when someone other than the owner chose the password.</param>
public sealed record NewAppUser(
    string Username, string DisplayName, UserRole Role, string PasswordHash, long? EmployeeId = null, bool MustChangePassword = false);
