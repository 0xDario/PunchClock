using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;
using PunchClock.Core.Site;

namespace PunchClock.App;

public sealed record EmployeeOption(long Id, string DisplayName);

public sealed partial class MainViewModel(
    EmployeeService employees, PunchService punches, SiteSettingsService site, TimeProvider clock) : ObservableObject
{
    // The zone punches are recorded in, so the clock on screen agrees with the punch messages.
    private TimeZoneInfo _zone = clock.LocalTimeZone;

    public ObservableCollection<EmployeeOption> Employees { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPunch))]
    public partial EmployeeOption? SelectedEmployee { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPunch))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "Select your name, enter your PIN, then choose Punch In or Punch Out.";

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial string Clock { get; set; } = "";

    [ObservableProperty]
    public partial string Date { get; set; } = "";

    public bool CanPunch => SelectedEmployee is not null && !IsBusy;

    public async Task LoadAsync()
    {
        _zone = SiteTime.ResolveZone(await site.GetTimeZoneIdAsync(), clock.LocalTimeZone);
        Tick();

        Employees.Clear();
        foreach (var employee in await employees.ListActiveAsync())
        {
            Employees.Add(new EmployeeOption(employee.Id, employee.DisplayName));
        }
    }

    public void Tick()
    {
        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _zone);
        Clock = now.ToString("t", CultureInfo.CurrentCulture);
        Date = now.ToString("D", CultureInfo.CurrentCulture);
    }

    /// <param name="employee">Who is punching; the current selection when null.</param>
    /// <returns>The outcome, or null when nothing was attempted or the attempt failed.</returns>
    public async Task<PunchResult?> PunchAsync(PunchDirection direction, string pin, EmployeeOption? employee = null)
    {
        employee ??= SelectedEmployee;
        if (employee is null)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            // PIN hashing is deliberately slow; keep it off the UI thread.
            var result = await Task.Run(() => punches.PunchAsync(employee.Id, pin, direction));
            Show(Describe(employee, direction, result), isError: !result.Accepted);
            if (result.Accepted)
            {
                SelectedEmployee = null;
            }

            return result;
        }
        catch (Exception ex)
        {
            // The message only ever claims success after the commit returned.
            Show($"Punch NOT recorded: {ex.Message}", isError: true);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Show(string message, bool isError)
    {
        Message = message;
        MessageIsError = isError;
    }

    private static string Describe(EmployeeOption employee, PunchDirection direction, PunchResult result)
    {
        var lastAt = result.LastPunch?.OccurredAtLocal.ToString("f", CultureInfo.CurrentCulture);
        return result.Rejection switch
        {
            null => $"{employee.DisplayName} punched {(direction == PunchDirection.In ? "in" : "out")} at {result.Punch!.OccurredAtLocal.ToString("t", CultureInfo.CurrentCulture)}.",
            PunchRejection.InvalidPin => "Incorrect PIN. Nothing was recorded.",
            PunchRejection.TooManyAttempts => $"Too many incorrect PINs. Try again in {PinPolicy.LockoutWindow.TotalMinutes:0} minutes or ask a manager to reset your PIN. Nothing was recorded.",
            PunchRejection.PinChangeRequired => "Your PIN must be replaced before you can punch. Nothing was recorded.",
            PunchRejection.AlreadyPunchedIn => $"You are already punched in since {lastAt}. If you forgot to punch out, punch out now and ask a manager to correct the time.",
            PunchRejection.NotPunchedIn => "You are not punched in, so there is nothing to punch out of. Ask a manager if a punch-in is missing.",
            PunchRejection.ClockBehindLastPunch => $"This computer's clock is earlier than your last punch ({lastAt}). Nothing was recorded; tell a manager.",
            PunchRejection.EmployeeInactive or PunchRejection.EmployeeNotFound => "This employee cannot punch. Ask a manager.",
            _ => "Nothing was recorded.",
        };
    }
}
