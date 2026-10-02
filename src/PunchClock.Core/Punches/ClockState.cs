namespace PunchClock.Core.Punches;

public enum ClockStatus
{
    Out,
    In,
}

public static class ClockState
{
    /// <summary>
    /// Status follows the employee's latest punch by time (see
    /// <see cref="Persistence.IPunchClockUnitOfWork.FindLatestPunchAsync"/>), never by row id.
    /// No punches at all means punched out.
    /// </summary>
    public static ClockStatus From(Punch? latest) =>
        latest?.Direction == PunchDirection.In ? ClockStatus.In : ClockStatus.Out;
}
