using System.ComponentModel;
using System.Windows;

namespace OneClickDpi.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeInProgress;
    private bool _closePrepared;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = MainViewModel.CreateDefault();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void ShowStartupUpdateResult(StartupUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ErrorMessage))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => MessageBox.Show(
            this,
            info.ErrorMessage,
            "OneClick DPI — обновление",
            MessageBoxButton.OK,
            MessageBoxImage.Warning));
    }

    private async void ToggleButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.ToggleAsync();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.HandleUpdateActionAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.StartUpdateChecksAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_closePrepared)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        IsEnabled = false;
        try
        {
            await _viewModel.DisposeAsync();
            _viewModel.TryInstallPreparedUpdateOnExit();
        }
        finally
        {
            _closePrepared = true;
            Close();
        }
    }
}
