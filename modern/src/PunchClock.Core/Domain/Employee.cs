namespace PunchClock.Core.Domain;

public sealed record Employee(long Id, string FirstName, string LastName, bool IsActive)
{
    public string FullName => $"{FirstName} {LastName}";
}
