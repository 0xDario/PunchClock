using System.Windows;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;
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
            var employees = new EmployeeService(store, hasher, TimeProvider.System);
            var punches = new PunchService(store, hasher, TimeProvider.System);

#if DEBUG
            await DemoData.SeedIfEmptyAsync(employees);
#endif

            var viewModel = new MainViewModel(employees, punches, TimeProvider.System);
            MainWindow = new MainWindow(viewModel);
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
