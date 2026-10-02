namespace PunchClock.Core.Domain;

/// <summary>The explicit action an employee chose. Never inferred from previous rows.</summary>
public enum PunchDirection
{
    In = 1,
    Out = 2,
}
