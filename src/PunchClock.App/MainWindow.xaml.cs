using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PunchClock.Core.Accounts;
using PunchClock.Core.Punches;

namespace PunchClock.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppServices _services;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(MainViewModel viewModel, AppServices services)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _services = services;

        _viewModel.Tick();
        _clockTimer.Tick += (_, _) => _viewModel.Tick();
        _clockTimer.Start();
        Closed += (_, _) => _clockTimer.Stop();
    }

    // PasswordBox.Password is not bindable by design, so the PIN is handed to the view model
    // here and cleared straight after every attempt.
    private async Task PunchAsync(PunchDirection direction)
    {
        var employee = _viewModel.SelectedEmployee;
        var pin = PinBox.Password;
        PinBox.Clear();

        var result = await _viewModel.PunchAsync(direction, pin);

        // Imported employees' legacy PINs were stored in plain text: the punch counts, then
        // the employee is asked to choose a new PIN.
        if (result is { Accepted: true, PinMustChange: true } && employee is not null)
        {
            new ChangePinWindow(_services.Employees, employee.Id, pin) { Owner = this }.ShowDialog();
        }
    }

    private async void PunchIn_Click(object sender, RoutedEventArgs e) => await PunchAsync(PunchDirection.In);

    private async void PunchOut_Click(object sender, RoutedEventArgs e) => await PunchAsync(PunchDirection.Out);

    private async void Admin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppUser? admin;
            if (await _services.Accounts.NeedsFirstAdminAsync())
            {
                var setup = new AdminSetupWindow(_services.Accounts) { Owner = this };
                admin = setup.ShowDialog() == true ? setup.Admin : null;
            }
            else
            {
                var signIn = new SignInWindow(_services.Accounts) { Owner = this };
                admin = signIn.ShowDialog() == true ? signIn.User : null;
            }

            if (admin is null)
            {
                return;
            }

            if (admin.Role != UserRole.Admin)
            {
                await _services.Accounts.SignOutAsync(admin);
                MessageBox.Show(this, "Only admins can open site administration.", "PunchClock", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            new AdminWindow(_services, admin) { Owner = this }.ShowDialog();
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            _viewModel.Show($"Admin action failed: {ex.Message}", isError: true);
        }
    }

    private void PinBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => DigitInput.Filter(e);

    private void PinBox_Pasting(object sender, DataObjectPastingEventArgs e) => DigitInput.FilterPaste(e);
}

internal static class DigitInput
{
    public static void Filter(TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    public static void FilterPaste(DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit))
        {
            e.CancelCommand();
        }
    }
}
