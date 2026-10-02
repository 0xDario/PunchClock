namespace PunchClock.Core.Domain;

public enum PunchState
{
    /// <summary>No punches yet, or the latest punch is an Out.</summary>
    PunchedOut,

    /// <summary>The latest punch is an In.</summary>
    PunchedIn,
}

public static class PunchRules
{
    public static PunchState StateAfter(Punch? latest) =>
        latest?.Direction == PunchDirection.In ? PunchState.PunchedIn : PunchState.PunchedOut;

    /// <summary>In is only valid while out, Out only while in. No toggling.</summary>
    public static bool IsAllowed(PunchState current, PunchDirection requested) => requested switch
    {
        PunchDirection.In => current == PunchState.PunchedOut,
        PunchDirection.Out => current == PunchState.PunchedIn,
        _ => false,
    };
}
