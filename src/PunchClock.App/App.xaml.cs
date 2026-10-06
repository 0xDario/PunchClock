using System.Windows;
using PunchClock.Core.Accounts;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;
using PunchClock.Core.Site;
using PunchClock.Data.Sqlite;

namespace PunchClock.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var database = await PunchClockDatabase.OpenAndMigrateAsync();

            var store = new SqlitePunchClockStore(database);
            var hasher = new Pbkdf2PinHasher();
            var services = new AppServices(
                new EmployeeService(store, hasher),
                new PunchService(store, hasher, TimeProvider.System),
                new AccountService(store, hasher),
                new SiteSettingsService(store));

#if DEBUG
            await DemoData.SeedIfRequestedAsync(services.Employees);
#endif

            var viewModel = new MainViewModel(services.Employees, services.Punches, services.Site, TimeProvider.System);
            MainWindow = new MainWindow(viewModel, services);
            MainWindow.Show();
            await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            // Never run the kiosk against a database it cannot open or verify.
            MessageBox.Show(
                $"PunchClock could not start.\n\n{ex.Message}",
                "PunchClock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

public sealed record AppServices(
    EmployeeService Employees,
    PunchService Punches,
    AccountService Accounts,
    SiteSettingsService Site);
