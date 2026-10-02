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
}
