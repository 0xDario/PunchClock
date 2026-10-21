using System.Windows;
using PunchClock.Core.Accounts;

namespace PunchClock.App;

public partial class SignInWindow : Window
{
    private readonly AccountService _accounts;
    private readonly AdminRecoveryService _recovery;

    public SignInWindow(AccountService accounts, AdminRecoveryService recovery)
    {
        InitializeComponent();
        _accounts = accounts;
        _recovery = recovery;
        Loaded += (_, _) => Username.Focus();
    }

    public AppUser? User { get; private set; }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var username = Username.Text;
        var password = Password.Password;
        Password.Clear();
        SignInButton.IsEnabled = false;
        try
        {
            // Password hashing is deliberately slow; keep it off the UI thread.
            User = await Task.Run(() => _accounts.SignInAsync(username, password));
            if (User is not null)
            {
                DialogResult = true;
                return;
            }

            Error.Text = "Wrong username or password.";
        }
        catch (Exception ex)
        {
            Error.Text = ex.Message;
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!_recovery.IsAvailable)
        {
            MessageBox.Show(this,
                "Ask another PunchClock admin to reset your password from Admin, Accounts.\n\n" +
                "This copy of PunchClock has no owner key, so it cannot recover an admin account when no admin can sign in.",
                "Forgot password", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var recover = new RecoverAdminWindow(_recovery) { Owner = this };
        if (recover.ShowDialog() == true && recover.RecoveredUsername is { } username)
        {
            Username.Text = username;
            Password.Clear();
            Error.Text = "";
            Notice.Text = "Sign in with the temporary password. You will then choose a new one.";
            Notice.Visibility = Visibility.Visible;
            Password.Focus();
        }
    }
}
