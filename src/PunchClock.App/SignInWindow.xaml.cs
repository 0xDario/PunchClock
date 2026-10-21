using System.Windows;
using PunchClock.Core.Accounts;

namespace PunchClock.App;

public partial class SignInWindow : Window
{
    private readonly AccountService _accounts;

    public SignInWindow(AccountService accounts)
    {
        InitializeComponent();
        _accounts = accounts;
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
        // Recovery sets a password without anyone signing in, so it takes the same Windows
        // administrator who could create the first admin; a kiosk user cannot take an account over.
        if (!WindowsAdmin.IsElevated())
        {
            MessageBox.Show(this,
                "Ask another PunchClock admin to reset your password from Admin, Accounts.\n\n" +
                "If no admin can sign in, a Windows administrator must close PunchClock, right-click it, choose \"Run as administrator\", click Admin, then Forgot password.",
                "Forgot password", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var recover = new RecoverAdminWindow(_accounts, WindowsAdmin.CurrentUserName()) { Owner = this };
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
