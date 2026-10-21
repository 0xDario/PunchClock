using System.Windows;
using PunchClock.Core.Accounts;

namespace PunchClock.App;

/// <summary>
/// Sets a temporary password on an admin account when no admin can sign in. Only opened when a
/// Windows administrator runs PunchClock elevated; the change is audited under their Windows name.
/// </summary>
public partial class RecoverAdminWindow : Window
{
    private readonly AccountService _accounts;
    private readonly string _windowsUser;

    public RecoverAdminWindow(AccountService accounts, string windowsUser)
    {
        InitializeComponent();
        _accounts = accounts;
        _windowsUser = windowsUser;
        Intro.Text = $"Choose the admin account that cannot sign in and give it a temporary password. Its owner must choose a new password at their next sign-in. The audit log records that {windowsUser} did this, and why.";
        Loaded += async (_, _) => await LoadAdminsAsync();
    }

    public string? RecoveredUsername { get; private set; }

    private sealed record AdminChoice(AppUser User)
    {
        public string Label => $"{User.DisplayName} ({User.Username})";
    }

    private async Task LoadAdminsAsync()
    {
        try
        {
            var admins = (await Task.Run(() => _accounts.ListAsync()))
                .Where(u => u is { Role: UserRole.Admin, IsActive: true })
                .Select(u => new AdminChoice(u))
                .ToList();
            Account.ItemsSource = admins;
            Account.SelectedIndex = admins.Count == 1 ? 0 : -1;
            RecoverButton.IsEnabled = admins.Count > 0;
            Account.Focus();
        }
        catch (Exception ex)
        {
            Error.Text = $"The admin accounts could not be loaded: {ex.Message}";
        }
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (Account.SelectedItem is not AdminChoice choice)
        {
            Error.Text = "Choose the admin account to recover.";
            return;
        }

        if (Password.Password != RepeatPassword.Password)
        {
            Error.Text = "The two passwords do not match.";
            return;
        }

        var (userId, password, reason) = (choice.User.Id, Password.Password, Reason.Text);
        RecoverButton.IsEnabled = false;
        try
        {
            // Password hashing is deliberately slow; keep it off the UI thread.
            var result = await Task.Run(() => _accounts.RecoverAdminAsync(userId, password, reason, _windowsUser));
            if (result == AccountChangeResult.Changed)
            {
                RecoveredUsername = choice.User.Username;
                DialogResult = true;
                return;
            }

            Error.Text = result switch
            {
                AccountChangeResult.ReasonRequired => "Enter a reason for the recovery.",
                AccountChangeResult.PasswordRejected => $"The temporary password must be at least {AccountService.MinPasswordLength} characters.",
                _ => "That account is no longer an active admin account.",
            };
        }
        catch (Exception ex)
        {
            Error.Text = $"The password could not be set: {ex.Message}";
        }
        finally
        {
            Password.Clear();
            RepeatPassword.Clear();
            RecoverButton.IsEnabled = true;
        }
    }
}
