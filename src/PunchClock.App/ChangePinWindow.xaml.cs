using System.Windows;
using System.Windows.Input;
using PunchClock.Core.Employees;
using PunchClock.Core.Security;

namespace PunchClock.App;

public partial class ChangePinWindow : Window
{
    private readonly EmployeeService _employees;
    private readonly long _employeeId;
    private readonly string _currentPin;

    public ChangePinWindow(EmployeeService employees, long employeeId, string currentPin)
    {
        InitializeComponent();
        _employees = employees;
        _employeeId = employeeId;
        _currentPin = currentPin;
        Loaded += (_, _) => NewPin.Focus();
    }

    /// <summary>The PIN now in effect, once the dialog returns true.</summary>
    public string? ChosenPin { get; private set; }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var newPin = NewPin.Password;
        if (newPin != RepeatPin.Password)
        {
            Error.Text = "The two PINs do not match.";
            return;
        }

        if (PinPolicy.Validate(newPin) is { } problem)
        {
            Error.Text = problem;
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _employees.ChangeOwnPinAsync(_employeeId, _currentPin, newPin));
            if (result == PinChangeResult.Changed)
            {
                ChosenPin = newPin;
                DialogResult = true;
                return;
            }

            Error.Text = result == PinChangeResult.TooManyAttempts
                ? $"Too many incorrect PINs. Try again in {PinPolicy.LockoutWindow.TotalMinutes:0} minutes or ask a manager."
                : "The PIN could not be changed. Ask a manager.";
        }
        catch (Exception ex)
        {
            Error.Text = $"The PIN could not be changed: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Pin_PreviewTextInput(object sender, TextCompositionEventArgs e) => DigitInput.Filter(e);

    private void Pin_Pasting(object sender, DataObjectPastingEventArgs e) => DigitInput.FilterPaste(e);
}
