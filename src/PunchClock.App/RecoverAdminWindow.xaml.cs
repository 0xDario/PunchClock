using System.Windows;
using System.Windows.Controls;
using PunchClock.Core.Accounts;

namespace PunchClock.App;

/// <summary>
/// Recovers an admin account with a response signed by the owner key. The challenge lives only in
/// this window: closing it, or choosing another account, discards it and its response.
/// </summary>
public partial class RecoverAdminWindow : Window
{
    private readonly AdminRecoveryService _recovery;
    private RecoveryChallenge? _challenge;

    public RecoverAdminWindow(AdminRecoveryService recovery)
    {
        InitializeComponent();
        _recovery = recovery;
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
            var admins = (await Task.Run(() => _recovery.ListRecoverableAsync()))
                .Select(u => new AdminChoice(u))
                .ToList();
            Account.ItemsSource = admins;
            Account.Focus();
            if (admins.Count == 1)
            {
                // Raises SelectionChanged, which issues the challenge.
                Account.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            Error.Text = $"The admin accounts could not be loaded: {ex.Message}";
        }
    }

    private async void Account_SelectionChanged(object sender, SelectionChangedEventArgs e) => await IssueChallengeAsync();

    /// <summary>Replaces any challenge with a new one for the selected account.</summary>
    private async Task IssueChallengeAsync()
    {
        _challenge = null;
        Challenge.Text = "";
        Response.Clear();
        CopyButton.IsEnabled = RecoverButton.IsEnabled = false;
        if (Account.SelectedItem is not AdminChoice choice)
        {
            return;
        }

        try
        {
            var challenge = await Task.Run(() => _recovery.IssueChallengeAsync(choice.User.Id));
            if (!ReferenceEquals(Account.SelectedItem, choice))
            {
                return;
            }

            if (challenge is null)
            {
                Error.Text = "That account is no longer an active admin account.";
                return;
            }

            _challenge = challenge;
            Challenge.Text = challenge.Code;
            ChallengeNote.Text = $"For account id {challenge.UserId}. It works for {AdminRecoveryService.ChallengeLifetime.TotalMinutes:0} minutes and only while this window stays open.";
            Error.Text = "";
            CopyButton.IsEnabled = RecoverButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Error.Text = $"A challenge could not be issued: {ex.Message}";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Challenge.Text);
        }
        catch (Exception ex)
        {
            Error.Text = $"Could not copy: {ex.Message}";
        }
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (_challenge is not { } challenge || Account.SelectedItem is not AdminChoice choice)
        {
            Error.Text = "Choose the admin account to recover.";
            return;
        }

        if (Password.Password != RepeatPassword.Password)
        {
            Error.Text = "The two passwords do not match.";
            return;
        }

        var (response, password, reason) = (Response.Text, Password.Password, Reason.Text);
        RecoverButton.IsEnabled = false;
        try
        {
            // Password hashing is deliberately slow; keep it off the UI thread.
            var result = await Task.Run(() => _recovery.RecoverAsync(challenge, response, password, reason));
            if (result == RecoveryResult.Recovered)
            {
                RecoveredUsername = choice.User.Username;
                DialogResult = true;
                return;
            }

            if (result is RecoveryResult.Expired or RecoveryResult.AlreadyUsed)
            {
                await IssueChallengeAsync();
                Error.Text = "That challenge can no longer be used. A new one is shown above: send it to the owner for a new response.";
                return;
            }

            Error.Text = result switch
            {
                RecoveryResult.ReasonRequired => "Enter a reason for the recovery.",
                RecoveryResult.PasswordRejected => $"The temporary password must be at least {AccountService.MinPasswordLength} characters.",
                RecoveryResult.InvalidResponse => "That response is not the owner's signature of this challenge. Check it was typed exactly, and that the owner signed the challenge shown above.",
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
            RecoverButton.IsEnabled = _challenge is { IsUsed: false };
        }
    }
}
