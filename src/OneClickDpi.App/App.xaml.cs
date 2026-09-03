using System.Diagnostics;
using System.Windows;

namespace OneClickDpi.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (UpdateInstaller.IsApplyMode(eventArgs.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(UpdateInstaller.RunApplyMode(eventArgs.Args));
            return;
        }

        if (!TryBecomeSingleInstance() || HasAnotherMainInstance())
        {
            MessageBox.Show(
                "OneClick DPI уже запущен. Используйте открытое окно программы.",
                "OneClick DPI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ReleaseSingleInstance();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        StartupUpdateInfo startupInfo;
        try
        {
            startupInfo = UpdateInstaller.ProcessNormalStartupArguments(eventArgs.Args);
        }
        catch (Exception exception)
        {
            startupInfo = new StartupUpdateInfo("Не удалось завершить очистку обновления: " + exception.Message);
        }

        window.ShowStartupUpdateResult(startupInfo);
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        ReleaseSingleInstance();
        base.OnExit(eventArgs);
    }

    private bool TryBecomeSingleInstance()
    {
        _singleInstanceMutex = new Mutex(false, @"Local\OneClickDpi.SingleInstance");
        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        return _ownsSingleInstanceMutex;
    }

    private static bool HasAnotherMainInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.SessionId == current.SessionId)
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return false;
    }

    private void ReleaseSingleInstance()
    {
        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _ownsSingleInstanceMutex = false;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }
}
