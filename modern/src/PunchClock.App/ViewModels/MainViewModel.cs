using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using PunchClock.Core.Abstractions;
using PunchClock.Core.Domain;
using PunchClock.Core.Security;
using PunchClock.Core.Services;

namespace PunchClock.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly EmployeeService _employees;
    private readonly PunchService _punches;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;

    private Employee? _selectedEmployee;
    private string _pin = string.Empty;
    private string _message = string.Empty;

    public MainViewModel(EmployeeService employees, PunchService punches, IClock clock)
    {
        _employees = employees;
        _punches = punches;
        _clock = clock;

        PunchInCommand = new RelayCommand(() => Punch(PunchDirection.In), CanPunch);
        PunchOutCommand = new RelayCommand(() => Punch(PunchDirection.Out), CanPunch);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(Time));
            OnPropertyChanged(nameof(Date));
        };
        _timer.Start();

        ReloadEmployees();
    }

    public event Action? PinCleared;

    public ObservableCollection<Employee> Employees { get; } = [];

    public ICommand PunchInCommand { get; }

    public ICommand PunchOutCommand { get; }

    public string Time => _clock.Now.ToString("t", CultureInfo.CurrentCulture);

    public string Date => _clock.Now.ToString("D", CultureInfo.CurrentCulture);

    public Employee? SelectedEmployee
    {
        get => _selectedEmployee;
        set => SetProperty(ref _selectedEmployee, value);
    }

    public string Pin
    {
        get => _pin;
        set => SetProperty(ref _pin, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private bool CanPunch() => SelectedEmployee is not null && PinPolicy.IsValid(Pin);

    private void Punch(PunchDirection direction)
    {
        if (SelectedEmployee is not { } employee)
        {
            return;
        }

        var outcome = direction == PunchDirection.In
            ? _punches.PunchIn(employee.Id, Pin)
            : _punches.PunchOut(employee.Id, Pin);

        // The message is built from the stored row, so it is only shown after the write committed.
        Message = outcome.Status switch
        {
            PunchOutcomeStatus.Recorded =>
                $"{employee.FullName} punched {(direction == PunchDirection.In ? "in" : "out")} at {outcome.Punch!.OccurredAt.ToString("f", CultureInfo.CurrentCulture)}.",
            PunchOutcomeStatus.InvalidPin => "Incorrect PIN.",
            PunchOutcomeStatus.AlreadyPunchedIn => $"{employee.FullName} is already punched in. Use Punch Out.",
            PunchOutcomeStatus.NotPunchedIn => $"{employee.FullName} is not punched in. Use Punch In.",
            PunchOutcomeStatus.InactiveEmployee => $"{employee.FullName} is no longer active.",
            PunchOutcomeStatus.UnknownEmployee => "Employee not found. The list has been refreshed.",
            _ => "Punch not recorded.",
        };

        Pin = string.Empty;
        PinCleared?.Invoke();

        if (outcome.Succeeded)
        {
            SelectedEmployee = null;
        }

        if (outcome.Status is PunchOutcomeStatus.UnknownEmployee or PunchOutcomeStatus.InactiveEmployee)
        {
            ReloadEmployees();
        }
    }

    private void ReloadEmployees()
    {
        Employees.Clear();
        foreach (var employee in _employees.GetActive())
        {
            Employees.Add(employee);
        }
    }
}
