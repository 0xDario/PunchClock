using System.Windows;
using System.Windows.Input;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Employees;
using PunchClock.Core.Security;

namespace PunchClock.App;

/// <summary>
/// Site administration for a signed-in admin, or staff management for a signed-in manager.
/// Every change is attributed to that account by the database's audit triggers, which also
/// enforce the role rules; this window only collects input.
/// </summary>
public partial class AdminWindow : Window
{
    private readonly AppServices _services;
    private readonly AppUser _admin;
    private bool _signedOut;

    public AdminWindow(AppServices services, AppUser admin)
    {
        InitializeComponent();
        _services = services;
        _admin = admin;
        SignedInAs.Text = $"Signed in as {admin.DisplayName} ({admin.Username})";
        TimeZoneBox.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        if (admin.Role != UserRole.Admin)
        {
            // Managers look after staff; site settings and accounts are admin-only in the database.
            SiteTab.Visibility = AccountsTab.Visibility = Visibility.Collapsed;
            EmployeesTab.IsSelected = true;
        }

        Loaded += async (_, _) => await RunAsync(ReloadAsync);
        Closed += async (_, _) => await SignOutOnceAsync();
    }

    private AuditActor Actor => AuditActor.ForUser(_admin.Id);

    private async Task ReloadAsync()
    {
        var zoneId = await _services.Site.GetTimeZoneIdAsync();
        TimeZoneBox.SelectedValue = zoneId ?? TimeZoneInfo.Local.Id;
        CurrentZone.Text = zoneId is null
            ? $"Not set. New punches use this computer's zone ({TimeZoneInfo.Local.Id}) until a zone is saved."
            : $"Saved: {zoneId}";

        AccountList.ItemsSource = await _services.Accounts.ListAsync();
        EmployeeList.ItemsSource = await _services.Employees.ListAsync(activeOnly: false);
    }

    private async void SaveTimeZone_Click(object sender, RoutedEventArgs e)
    {
        if (TimeZoneBox.SelectedValue is not string zoneId)
        {
            Show("Pick a time zone first.", isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            await _services.Site.SetTimeZoneAsync(_admin, zoneId);
            await ReloadAsync();
            Show($"Site time zone set to {zoneId}.");
        });
    }

    private async void DeactivateAccount_Click(object sender, RoutedEventArgs e) => await SetAccountActiveAsync(false);

    private async void ActivateAccount_Click(object sender, RoutedEventArgs e) => await SetAccountActiveAsync(true);

    private async Task SetAccountActiveAsync(bool isActive)
    {
        if (AccountList.SelectedItem is not AppUser target)
        {
            Show("Select an account first.", isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _services.Accounts.SetActiveAsync(_admin, target.Id, isActive, AccountReason.Text);
            await ReloadAsync();
            Show(result switch
            {
                AccountChangeResult.Changed => $"{target.Username} is now {(isActive ? "active" : "inactive")}.",
                AccountChangeResult.NotAllowed => "The system account and your own account cannot be changed here.",
                _ => "That account no longer exists.",
            }, isError: result != AccountChangeResult.Changed);
        });
    }

    private async void DeactivateEmployee_Click(object sender, RoutedEventArgs e) => await SetEmployeeActiveAsync(false);

    private async void ActivateEmployee_Click(object sender, RoutedEventArgs e) => await SetEmployeeActiveAsync(true);

    private async Task SetEmployeeActiveAsync(bool isActive)
    {
        if (EmployeeList.SelectedItem is not Employee target)
        {
            Show("Select an employee first.", isError: true);
            return;
        }

        await RunAsync(async () =>
        {
            await _services.Employees.SetActiveAsync(Actor, target.Id, isActive);
            await ReloadAsync();
            Show($"{target.DisplayName} is now {(isActive ? "active" : "inactive")}.");
        });
    }

    private async void ResetPin_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeeList.SelectedItem is not Employee target)
        {
            Show("Select an employee first.", isError: true);
            return;
        }

        var (pin, reason) = (TemporaryPin.Password, PinResetReason.Text);
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _services.Employees.ResetPinAsync(_admin, target.Id, pin, reason));
            if (result == PinResetResult.Reset)
            {
                TemporaryPin.Clear();
            }

            await ReloadAsync();
            Show(result switch
            {
                PinResetResult.Reset => $"{target.DisplayName}'s PIN was reset. They must choose a new one at their next punch.",
                PinResetResult.PinRejected => PinPolicy.Validate(pin) ?? "That PIN is not allowed.",
                PinResetResult.NotAllowed => "Enter a reason for the reset.",
                _ => "That employee no longer exists or is inactive.",
            }, isError: result != PinResetResult.Reset);
        });
    }

    private async void AddEmployee_Click(object sender, RoutedEventArgs e)
    {
        var (first, last, pin) = (NewFirstName.Text, NewLastName.Text, NewPin.Password);
        await RunAsync(async () =>
        {
            await Task.Run(() => _services.Employees.CreateAsync(Actor, first, last, pin));
            NewFirstName.Clear();
            NewLastName.Clear();
            NewPin.Clear();
            await ReloadAsync();
            Show($"{first.Trim()} {last.Trim()} added.");
        });
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await SignOutOnceAsync();
        Close();
    }

    private async Task SignOutOnceAsync()
    {
        if (_signedOut)
        {
            return;
        }

        _signedOut = true;
        try
        {
            await _services.Accounts.SignOutAsync(_admin);
        }
        catch
        {
            // Sign-out is a courtesy record; the window closes regardless.
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // Includes the database's own refusals, e.g. "site_setting: admin only".
            Show(ex.Message, isError: true);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Show(string message, bool isError = false)
    {
        Status.Text = message;
        Status.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkGreen;
    }

    private void Pin_PreviewTextInput(object sender, TextCompositionEventArgs e) => DigitInput.Filter(e);

    private void Pin_Pasting(object sender, DataObjectPastingEventArgs e) => DigitInput.FilterPaste(e);
}
