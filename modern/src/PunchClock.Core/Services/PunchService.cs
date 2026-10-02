using PunchClock.Core.Abstractions;
using PunchClock.Core.Domain;
using PunchClock.Core.Security;

namespace PunchClock.Core.Services;

public enum PunchOutcomeStatus
{
    Recorded,
    UnknownEmployee,
    InactiveEmployee,
    InvalidPin,
    AlreadyPunchedIn,
    NotPunchedIn,
}

public sealed record PunchOutcome(PunchOutcomeStatus Status, Punch? Punch = null)
{
    public bool Succeeded => Status == PunchOutcomeStatus.Recorded;
}

/// <summary>
/// Kiosk punch flow. The employee states the direction explicitly; the service
/// verifies the PIN and refuses a direction that contradicts the current state,
/// instead of silently toggling as the legacy app did.
/// </summary>
public sealed class PunchService
{
    public const string KioskSource = "kiosk";

    private readonly IEmployeeStore _employees;
    private readonly IPunchStore _punches;
    private readonly IPinHasher _pinHasher;
    private readonly IClock _clock;

    public PunchService(IEmployeeStore employees, IPunchStore punches, IPinHasher pinHasher, IClock clock)
    {
        _employees = employees;
        _punches = punches;
        _pinHasher = pinHasher;
        _clock = clock;
    }

    public PunchState GetState(long employeeId) => PunchRules.StateAfter(_punches.GetLatest(employeeId));

    public PunchOutcome PunchIn(long employeeId, string pin) => Punch(employeeId, pin, PunchDirection.In);

    public PunchOutcome PunchOut(long employeeId, string pin) => Punch(employeeId, pin, PunchDirection.Out);

    private PunchOutcome Punch(long employeeId, string pin, PunchDirection direction)
    {
        var employee = _employees.Find(employeeId);
        if (employee is null)
        {
            return new PunchOutcome(PunchOutcomeStatus.UnknownEmployee);
        }

        if (!employee.IsActive)
        {
            return new PunchOutcome(PunchOutcomeStatus.InactiveEmployee);
        }

        var pinHash = _employees.GetPinHash(employeeId);
        if (pinHash is null || !PinPolicy.IsValid(pin) || !_pinHasher.Verify(pin, pinHash))
        {
            return new PunchOutcome(PunchOutcomeStatus.InvalidPin);
        }

        var result = _punches.TryAppend(new NewPunch(employeeId, direction, _clock.Now, KioskSource));
        if (result.Status == AppendPunchStatus.Appended)
        {
            return new PunchOutcome(PunchOutcomeStatus.Recorded, result.Punch);
        }

        return new PunchOutcome(direction == PunchDirection.In
            ? PunchOutcomeStatus.AlreadyPunchedIn
            : PunchOutcomeStatus.NotPunchedIn);
    }
}
