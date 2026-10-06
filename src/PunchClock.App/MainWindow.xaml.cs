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

        // Imported employees' legacy PINs were stored in plain text, so they are refused until
        // replaced. A new PIN completes the punch they asked for; cancelling records nothing.
        if (result is { Rejection: PunchRejection.PinChangeRequired } && employee is not null)
        {
            var change = new ChangePinWindow(_services.Employees, employee.Id, pin) { Owner = this };
            if (change.ShowDialog() == true && change.ChosenPin is { } newPin)
            {
                // The employee whose PIN just changed, not whoever is selected now.
                await _viewModel.PunchAsync(direction, newPin, employee);
            }
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
                // Whoever creates the first admin owns the site, so a kiosk user cannot do it:
                // it takes a Windows administrator running PunchClock elevated.
                if (!IsElevatedWindowsAdmin())
                {
                    MessageBox.Show(this,
                        "No PunchClock admin account exists yet. A Windows administrator must create it: right-click PunchClock, choose \"Run as administrator\", then click Admin.",
                        "PunchClock", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

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

            if (admin.Role is not (UserRole.Admin or UserRole.Manager))
            {
                await _services.Accounts.SignOutAsync(admin);
                MessageBox.Show(this, "Only admins and managers can open administration.", "PunchClock", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (admin.MustChangePassword)
            {
                // An admin chose this password, so it is not yet only this person's.
                if (new ChangePasswordWindow(_services.Accounts, admin, forced: true) { Owner = this }.ShowDialog() != true)
                {
                    await _services.Accounts.SignOutAsync(admin);
                    return;
                }

                admin = admin with { MustChangePassword = false };
            }

            new AdminWindow(_services, admin) { Owner = this }.ShowDialog();
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            _viewModel.Show($"Admin action failed: {ex.Message}", isError: true);
        }
    }

    private static bool IsElevatedWindowsAdmin()
    {
        // Under UAC this is true only for an elevated process, not for an admin's filtered token.
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
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
