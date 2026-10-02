using System.Windows;
using PunchClock.App.ViewModels;
using PunchClock.Core.Abstractions;
using PunchClock.Core.Security;
using PunchClock.Core.Services;
using PunchClock.Data;

namespace PunchClock.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unexpected error. Nothing was recorded for this action.\n\n{args.Exception.Message}",
                "PunchClock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var database = SqliteDatabase.ForFile(AppPaths.DatabasePath());
            new MigrationRunner(database).Migrate();

            var hasher = new Pbkdf2PinHasher();
            var employeeStore = new SqliteEmployeeStore(database);
            var employees = new EmployeeService(employeeStore, hasher);
            var punches = new PunchService(employeeStore, new SqlitePunchStore(database), hasher, new SystemClock());

#if DEBUG
            if (employees.GetActive().Count == 0)
            {
                employees.Create("Demo", "Employee", "1234");
            }
#endif

            var window = new MainWindow { DataContext = new MainViewModel(employees, punches, new SystemClock()) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the database at {AppPaths.DatabasePath()}.\n\n{ex.Message}",
                "PunchClock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
