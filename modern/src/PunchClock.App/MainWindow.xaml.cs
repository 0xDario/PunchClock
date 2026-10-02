using System.Windows;
using PunchClock.App.ViewModels;

namespace PunchClock.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PinCleared += () => PinBox.Clear();
            }
        };
    }

    // PasswordBox.Password is not bindable by design, so it is pushed to the view model here.
    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Pin = PinBox.Password;
        }
    }
}
