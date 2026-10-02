using System.Windows;
using PunchClock.Core.Accounts;

namespace PunchClock.App;

public partial class AdminSetupWindow : Window
{
    private readonly AccountService _accounts;

    public AdminSetupWindow(AccountService accounts)
    {
        InitializeComponent();
        _accounts = accounts;
        Loaded += (_, _) => DisplayName.Focus();
    }

    public AppUser? Admin { get; private set; }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Password.Password != RepeatPassword.Password)
        {
            Error.Text = "The two passwords do not match.";
            return;
        }

        var (displayName, username, password) = (DisplayName.Text, Username.Text, Password.Password);
        CreateButton.IsEnabled = false;
        try
        {
            Admin = await Task.Run(() => _accounts.CreateFirstAdminAsync(username, displayName, password));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Error.Text = ex.Message;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }
}
