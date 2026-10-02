using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PunchClock.Core.Punches;

namespace PunchClock.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        _viewModel.Tick();
        _clockTimer.Tick += (_, _) => _viewModel.Tick();
        _clockTimer.Start();
        Closed += (_, _) => _clockTimer.Stop();
    }

    // PasswordBox.Password is not bindable by design, so the PIN is handed to the view model
    // here and cleared straight after every attempt.
    private Task PunchAsync(PunchDirection direction)
    {
        var pin = PinBox.Password;
        PinBox.Clear();
        return _viewModel.PunchAsync(direction, pin);
    }

    private async void PunchIn_Click(object sender, RoutedEventArgs e) => await PunchAsync(PunchDirection.In);

    private async void PunchOut_Click(object sender, RoutedEventArgs e) => await PunchAsync(PunchDirection.Out);

    private void PinBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void PinBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit))
        {
            e.CancelCommand();
        }
    }
}
