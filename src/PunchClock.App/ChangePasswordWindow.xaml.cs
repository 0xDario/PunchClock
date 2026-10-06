using System.Windows;
using PunchClock.Core.Accounts;

namespace PunchClock.App;

public partial class ChangePasswordWindow : Window
{
    private readonly AccountService _accounts;
    private readonly AppUser _user;

    /// <param name="forced">The account has a temporary password set by an admin and cannot be used until it is replaced.</param>
    public ChangePasswordWindow(AccountService accounts, AppUser user, bool forced)
    {
        InitializeComponent();
        _accounts = accounts;
        _user = user;
        if (forced)
        {
            Intro.Text = "Your password was set by an admin. Choose your own password of 10 or more characters before continuing.";
        }

        Loaded += (_, _) => CurrentPassword.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var (current, chosen) = (CurrentPassword.Password, NewPassword.Password);
        if (chosen != RepeatPassword.Password)
        {
            Error.Text = "The two new passwords do not match.";
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            // Password hashing is deliberately slow; keep it off the UI thread.
            var result = await Task.Run(() => _accounts.ChangeOwnPasswordAsync(_user, current, chosen));
            if (result == PasswordChangeResult.Changed)
            {
                DialogResult = true;
                return;
            }

            Error.Text = result switch
            {
                PasswordChangeResult.InvalidCurrentPassword => "The current password is wrong.",
                PasswordChangeResult.PasswordRejected =>
                    $"The new password must be at least {AccountService.MinPasswordLength} characters and different from the current one.",
                PasswordChangeResult.TooManyAttempts =>
                    $"Too many wrong passwords. Try again in {AccountService.SignInLockout.TotalMinutes:0} minutes or ask an admin.",
                _ => "This account can no longer change its password.",
            };
        }
        catch (Exception ex)
        {
            Error.Text = $"The password could not be changed: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
