using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using OneClickDpi.Core;

namespace OneClickDpi.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly Brush IdleBrush = CreateFrozenBrush("#6C63FF");
    private static readonly Brush ConnectedBrush = CreateFrozenBrush("#2CCB88");
    private static readonly Brush BusyBrush = CreateFrozenBrush("#E2A93B");
    private static readonly Brush UnknownBrush = CreateFrozenBrush("#52607A");
    private static readonly Brush HealthyBrush = CreateFrozenBrush("#38D996");
    private static readonly Brush UnhealthyBrush = CreateFrozenBrush("#FF6577");

    private readonly AutoConnectCoordinator _coordinator;
    private readonly IDpiEngine _engine;
    private readonly HttpConnectivityProbe _probe;
    private readonly SelectiveTunnelCoordinator _tunnel;
    private readonly LocalLogWriter _logWriter;
    private readonly GitHubUpdateClient _updateClient;
    private readonly UpdateSettingsStore _updateSettings;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isBusy;
    private bool _isConnected;
    private bool _updateChecksStarted;
    private bool _updateBusy;
    private bool _updateLaunchStarted;
    private bool _installUpdatesOnExit;
    private int _automaticRestartAttempts;
    private Task? _periodicUpdateTask;
    private PreparedUpdate? _preparedUpdate;
    private string _statusText = "Готово к подключению";
    private string _detailText = "Нажмите кнопку — приложение само проверит и выберет стратегию.";
    private string _strategyText = "Стратегия будет выбрана автоматически";
    private string _updateText = "Автообновление: проверка при запуске";

    private MainViewModel(
        AutoConnectCoordinator coordinator,
        IDpiEngine engine,
        HttpConnectivityProbe probe,
        SelectiveTunnelCoordinator tunnel,
        LocalLogWriter logWriter,
        GitHubUpdateClient updateClient,
        UpdateSettingsStore updateSettings)
    {
        _coordinator = coordinator;
        _engine = engine;
        _probe = probe;
        _tunnel = tunnel;
        _logWriter = logWriter;
        _updateClient = updateClient;
        _updateSettings = updateSettings;
        _installUpdatesOnExit = updateSettings.LoadInstallOnExit();
        _engine.LogReceived += OnEngineLogReceived;
        _engine.UnexpectedlyExited += OnEngineUnexpectedlyExited;
        _probe.ProbeCompleted += OnProbeCompleted;
        _tunnel.LogReceived += OnTunnelLogReceived;
        _tunnel.ProgressChanged += OnTunnelProgressChanged;
        _tunnel.UnexpectedlyExited += OnTunnelUnexpectedlyExited;
        Services = new ObservableCollection<ServiceStatusViewModel>(
        [
            new(ServiceKind.Discord, "Discord", UnknownBrush),
            new(ServiceKind.YouTube, "YouTube", UnknownBrush),
            new(ServiceKind.Telegram, "Telegram", UnknownBrush),
            new(ServiceKind.ChatGPT, "ChatGPT", UnknownBrush),
            new(ServiceKind.Claude, "Claude", UnknownBrush),
            new(ServiceKind.WhatsApp, "WhatsApp", UnknownBrush)
        ]);
    }

    public static MainViewModel CreateDefault()
    {
        var localData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneClickDpi");
        var runtime = RuntimeAssetExtractor.EnsureExtracted(localData);
        var paths = new EnginePaths(runtime.EngineDirectory);
        var strategies = StrategyCatalog.CreateDefault(paths);
        var engine = new ProcessDpiEngine(paths, new EngineIntegrityValidator());
        var probe = new HttpConnectivityProbe();
        var cachePath = Path.Combine(localData, "strategy-cache.json");
        var logWriter = new LocalLogWriter(Path.Combine(
            localData,
            "Logs"));
        var tor = new TorTunnelEngine(
            runtime.TorDirectory,
            Path.Combine(localData, "TorData"));
        var psiphon = new PsiphonTunnelEngine(
            runtime.PsiphonDirectory,
            Path.Combine(localData, "PsiphonData"),
            Path.Combine(localData, "psiphon-process.pid"));
        var tunnel = new SelectiveTunnelCoordinator(
            tor,
            psiphon,
            new WindowsProxyController(Path.Combine(localData, "windows-proxy-backup.json")),
            new SelectiveRouteMatcher(),
            new TelegramProxyIntegration(Path.Combine(localData, "telegram-proxy-setup-19050.txt")));
        var coordinator = new AutoConnectCoordinator(
            strategies,
            engine,
            probe,
            new JsonStrategyCache(cachePath),
            new NetworkFingerprintProvider());
        return new MainViewModel(
            coordinator,
            engine,
            probe,
            tunnel,
            logWriter,
            new GitHubUpdateClient(Path.Combine(localData, "Updates")),
            new UpdateSettingsStore(Path.Combine(localData, "update-settings.json")));
    }

    public ObservableCollection<ServiceStatusViewModel> Services { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetField(ref _detailText, value);
    }

    public string StrategyText
    {
        get => _strategyText;
        private set => SetField(ref _strategyText, value);
    }

    public string UpdateText
    {
        get => _updateText;
        private set => SetField(ref _updateText, value);
    }

    public string UpdateButtonText => _updateBusy
        ? "…"
        : _preparedUpdate is null ? "ПРОВЕРИТЬ" : "УСТАНОВИТЬ";
    public bool CanUseUpdateButton => !_updateBusy && !_updateLaunchStarted;

    public bool InstallUpdatesOnExit
    {
        get => _installUpdatesOnExit;
        set
        {
            if (SetField(ref _installUpdatesOnExit, value))
            {
                _updateSettings.SaveInstallOnExit(value);
            }
        }
    }

    public string ButtonText => _isBusy ? "…" : _isConnected ? "ВЫКЛ" : "ВКЛ";
    public bool CanToggle => !_isBusy;
    public Brush ButtonBackground => _isBusy ? BusyBrush : _isConnected ? ConnectedBrush : IdleBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task StartUpdateChecksAsync()
    {
        if (_updateChecksStarted)
        {
            return;
        }

        _updateChecksStarted = true;
        await CheckAndDownloadUpdateAsync();
        _periodicUpdateTask = RunPeriodicUpdateChecksAsync();
    }

    public async Task InstallPreparedUpdateAsync()
    {
        if (_preparedUpdate is null || _updateBusy || _updateLaunchStarted)
        {
            return;
        }

        _updateBusy = true;
        NotifyUpdateState();
        try
        {
            if (_isConnected)
            {
                SetBusy(true);
                StatusText = "Подготовка обновления";
                DetailText = "Отключаем сетевой движок и восстанавливаем настройки Windows.";
                await DisconnectAsync();
            }

            UpdateInstaller.Launch(_preparedUpdate, AppContext.BaseDirectory);
            _updateLaunchStarted = true;
            UpdateText = $"Устанавливаем v{_preparedUpdate.Release.Version.ToString(3)}…";
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            _logWriter.Write("Updater launch failed: " + exception);
            UpdateText = "Не удалось запустить обновление: " + FriendlyError(exception);
        }
        finally
        {
            _updateBusy = false;
            SetBusy(false);
            NotifyUpdateState();
        }
    }

    public async Task HandleUpdateActionAsync()
    {
        if (_preparedUpdate is not null)
        {
            await InstallPreparedUpdateAsync();
            return;
        }

        await CheckAndDownloadUpdateAsync();
    }

    public void TryInstallPreparedUpdateOnExit()
    {
        if (_preparedUpdate is null || !_installUpdatesOnExit || _updateLaunchStarted)
        {
            return;
        }

        try
        {
            UpdateInstaller.Launch(_preparedUpdate, AppContext.BaseDirectory);
            _updateLaunchStarted = true;
        }
        catch (Exception exception)
        {
            _logWriter.Write("Updater-on-exit launch failed: " + exception);
        }
    }

    public async Task ToggleAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (!_isConnected)
        {
            _automaticRestartAttempts = 0;
        }

        SetBusy(true);
        try
        {
            if (_isConnected)
            {
                await DisconnectAsync();
            }
            else
            {
                await ConnectAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            StatusText = "Отключено";
            DetailText = "Работа приложения завершена.";
        }
        catch (Exception exception)
        {
            _logWriter.Write(exception.ToString());
            _isConnected = false;
            StatusText = "Не удалось подключиться";
            DetailText = FriendlyError(exception);
            StrategyText = "Проверьте компоненты и подключение к интернету";
            ResetServices("Ошибка проверки", UnhealthyBrush);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConnectAsync()
    {
        ResetServices("Ожидает проверки", UnknownBrush);
        var progress = new Progress<AutoConnectProgress>(UpdateProgress);
        var result = await _coordinator.ConnectAsync(progress, _lifetime.Token);
        var connectivity = result.Connectivity;
        string? tunnelError = null;

        StatusText = "Создаём локальный туннель";
        DetailText = "Подключаем Snowflake и отдельный маршрут ИИ, затем проверяем сервисы.";
        StrategyText = "Discord, ChatGPT и Claude используют отдельный AI-маршрут; видео YouTube остаётся быстрым";
        UpdateTunnelTargets("Подключение туннеля", BusyBrush);
        try
        {
            var tunnelConnectivity = await _tunnel.StartAsync(_lifetime.Token);
            connectivity = MergeConnectivity(connectivity, tunnelConnectivity);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logWriter.Write(exception.ToString());
            tunnelError = FriendlyError(exception);
        }

        _isConnected = true;
        StatusText = connectivity.AllHealthy ? "Подключено" : "Подключено частично";
        DetailText = _tunnel.TelegramSetupPromptOpened
            ? "Telegram открыт с готовыми параметрами. Один раз нажмите «Подключить» в его окне."
            : connectivity.AllHealthy
            ? "Discord, ChatGPT и Claude используют AI-маршрут; Telegram — Tor, видео YouTube идёт напрямую."
            : tunnelError is not null
                ? $"Локальный туннель не запустился: {tunnelError}"
                : "Выбрана лучшая доступная комбинация, но часть проверок не прошла.";
        StrategyText = _tunnel.IsRunning
            ? $"{result.Strategy.DisplayName} + локальные туннели 127.0.0.1"
            : $"Стратегия: {result.Strategy.DisplayName}";
        ApplyConnectivity(connectivity);
        NotifyButtonState();
    }

    private async Task DisconnectAsync()
    {
        StatusText = "Отключение туннеля";
        DetailText = "Восстанавливаем системные настройки Windows.";
        await _tunnel.StopAsync(_lifetime.Token);
        var progress = new Progress<AutoConnectProgress>(UpdateProgress);
        await _coordinator.DisconnectAsync(progress, _lifetime.Token);
        _isConnected = false;
        StatusText = "Отключено";
        DetailText = "Сетевой движок остановлен, исходные настройки Windows восстановлены.";
        StrategyText = "Последняя стратегия сохранена для этой сети";
        ResetServices("Не проверяется", UnknownBrush);
        NotifyButtonState();
    }

    private void UpdateProgress(AutoConnectProgress progress)
    {
        StatusText = progress.Stage switch
        {
            AutoConnectStage.StartingCandidate => "Автоподбор стратегии",
            AutoConnectStage.Probing => "Проверяем подключение",
            AutoConnectStage.Selecting => "Выбираем лучший профиль",
            AutoConnectStage.Stopping => "Отключение",
            _ => StatusText
        };
        DetailText = progress.Message;
        if (progress.CandidateCount > 0)
        {
            StrategyText = $"Кандидат {progress.CandidateIndex} из {progress.CandidateCount}";
        }
    }

    private void ApplyConnectivity(ConnectivitySnapshot snapshot)
    {
        foreach (var service in Services)
        {
            var result = snapshot.Services.FirstOrDefault(item => item.Service == service.Kind);
            if (result is null)
            {
                service.Update("Нет данных", UnhealthyBrush);
                continue;
            }

            var latency = result.AverageLatency == TimeSpan.MaxValue
                ? "нет ответа"
                : $"{result.AverageLatency.TotalMilliseconds:0} мс";
            var viaTunnel = result.Endpoints.Any(endpoint =>
                endpoint.Endpoint.Name.Contains("via tunnel", StringComparison.OrdinalIgnoreCase));
            service.Update(
                result.IsHealthy
                    ? viaTunnel
                        ? $"Туннель · {latency}"
                        : $"Доступен · {latency}"
                    : service.Kind is ServiceKind.Telegram or ServiceKind.ChatGPT or ServiceKind.Claude
                        ? "Туннель недоступен"
                        : "Недоступен",
                result.IsHealthy ? HealthyBrush : UnhealthyBrush);
        }
    }

    private void ResetServices(string detail, Brush indicator)
    {
        foreach (var service in Services)
        {
            service.Update(detail, indicator);
        }
    }

    private void UpdateTunnelTargets(string detail, Brush indicator)
    {
        foreach (var service in Services.Where(service =>
                     service.Kind is ServiceKind.Discord
                         or ServiceKind.YouTube
                         or ServiceKind.Telegram
                         or ServiceKind.ChatGPT
                         or ServiceKind.Claude
                         or ServiceKind.WhatsApp))
        {
            service.Update(detail, indicator);
        }
    }

    private static ConnectivitySnapshot MergeConnectivity(
        ConnectivitySnapshot direct,
        ConnectivitySnapshot tunneled)
    {
        var overlay = tunneled.Services.ToDictionary(service => service.Service);
        var merged = direct.Services
            .Where(service => !overlay.ContainsKey(service.Service))
            .Concat(tunneled.Services)
            .OrderBy(service => service.Service)
            .ToArray();
        return new ConnectivitySnapshot(merged);
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        NotifyButtonState();
    }

    private void NotifyButtonState()
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ButtonBackground));
    }

    private async Task CheckAndDownloadUpdateAsync()
    {
        if (_updateBusy || _preparedUpdate is not null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _updateBusy = true;
        UpdateText = "Автообновление: проверяем GitHub…";
        NotifyUpdateState();
        try
        {
            var release = await _updateClient.CheckAsync(
                UpdateInstaller.GetCurrentVersion(),
                _lifetime.Token);
            if (release is null)
            {
                UpdateText = $"Версия {UpdateInstaller.GetCurrentVersion().ToString(3)} · обновлений нет";
                return;
            }

            var progress = new Progress<int>(percentage =>
            {
                UpdateText = $"Скачиваем обновление v{release.Version.ToString(3)}: {percentage}%";
            });
            _preparedUpdate = await _updateClient.DownloadAsync(release, progress, _lifetime.Token);
            UpdateText = $"Обновление v{release.Version.ToString(3)} готово";
            _logWriter.Write($"Update {release.Version.ToString(3)} downloaded and verified.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logWriter.Write("Update check failed: " + exception);
            UpdateText = "Автообновление: проверим позже";
        }
        finally
        {
            _updateBusy = false;
            NotifyUpdateState();
        }
    }

    private async Task RunPeriodicUpdateChecksAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(4));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await CheckAndDownloadUpdateAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void NotifyUpdateState()
    {
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(CanUseUpdateButton));
    }

    private static string FriendlyError(Exception exception)
    {
        var message = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? aggregate.Message
            : exception.Message;

        return message.Length <= 220 ? message : message[..217] + "…";
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnEngineLogReceived(object? sender, string message) => _logWriter.Write(message);

    private void OnTunnelLogReceived(string message)
    {
        _logWriter.Write("TUNNEL " + message);
        if (!_isBusy || _isConnected)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (message.StartsWith("Psiphon AI attempt", StringComparison.Ordinal))
            {
                var region = message.Contains("region DE", StringComparison.Ordinal)
                    ? "Германию"
                    : message.Contains("region FI", StringComparison.Ordinal)
                        ? "Финляндию"
                        : "Нидерланды";
                DetailText = $"Подбираем маршрут ИИ через {region}: не более 25 секунд на вариант.";
            }
            else if (message.Contains("trying another region", StringComparison.Ordinal))
            {
                DetailText = "Текущий маршрут ИИ не ответил — автоматически пробуем следующую страну.";
            }
            else if (message.Contains("using Tor fallback", StringComparison.Ordinal))
            {
                DetailText = "Отдельный маршрут ИИ недоступен. Завершаем подключение через резервный туннель.";
            }
        });
    }

    private void OnTunnelProgressChanged(int percentage)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_isBusy && !_isConnected)
            {
                StatusText = "Создаём локальный туннель";
                DetailText = percentage < 100
                    ? $"Snowflake подключается: {percentage}%. Ожидание ограничено 100 секундами."
                    : "Основной туннель готов. Завершаем автоматический выбор маршрута ИИ.";
            }
        });
    }

    private void OnProbeCompleted(ConnectivitySnapshot snapshot)
    {
        var strategy = _engine.ActiveStrategyId ?? "no-engine";
        foreach (var service in snapshot.Services)
        {
            foreach (var endpoint in service.Endpoints)
            {
                _logWriter.Write(
                    $"Probe {strategy} / {endpoint.Endpoint.Name}: " +
                    $"{(endpoint.IsSuccess ? "OK" : "FAIL")} " +
                    $"{endpoint.Latency.TotalMilliseconds:0} ms" +
                    (endpoint.StatusCode is int status ? $" HTTP {status}" : string.Empty) +
                    (string.IsNullOrWhiteSpace(endpoint.Error) ? string.Empty : $" ({endpoint.Error})"));
            }
        }
    }

    private void OnEngineUnexpectedlyExited(object? sender, EventArgs eventArgs)
    {
        _logWriter.Write("The owned DPI engine exited unexpectedly.");
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _isConnected = false;
            StatusText = "Движок остановился";
            DetailText = "Профиль завершился с ошибкой. Приложение попробует другой вариант.";
            StrategyText = $"Журнал: {_logWriter.LogPath}";
            ResetServices("Требуется переподключение", UnhealthyBrush);
            NotifyButtonState();

            if (_automaticRestartAttempts < 2 && !_lifetime.IsCancellationRequested)
            {
                _automaticRestartAttempts++;
                _ = ReconnectAfterEngineFailureAsync();
            }
        });
    }

    private void OnTunnelUnexpectedlyExited(object? sender, EventArgs eventArgs)
    {
        _logWriter.Write("An owned local tunnel exited unexpectedly.");
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _isConnected = false;
            StatusText = "Туннель остановился";
            DetailText = "Системный прокси восстановлен. Приложение попробует переподключиться.";
            StrategyText = $"Журнал: {_logWriter.LogPath}";
            UpdateTunnelTargets("Требуется переподключение", UnhealthyBrush);
            NotifyButtonState();

            if (_automaticRestartAttempts < 2 && !_lifetime.IsCancellationRequested)
            {
                _automaticRestartAttempts++;
                _ = ReconnectAfterEngineFailureAsync();
            }
        });
    }

    private async Task ReconnectAfterEngineFailureAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
            if (_isBusy || _isConnected || _lifetime.IsCancellationRequested)
            {
                return;
            }

            SetBusy(true);
            StatusText = "Автопереподключение";
            await _tunnel.StopAsync(_lifetime.Token);
            await _coordinator.DisconnectAsync(null, _lifetime.Token);
            await ConnectAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logWriter.Write(exception.ToString());
            _isConnected = false;
            StatusText = "Автопереподключение не удалось";
            DetailText = FriendlyError(exception);
            ResetServices("Ошибка проверки", UnhealthyBrush);
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_periodicUpdateTask is not null)
        {
            try
            {
                await _periodicUpdateTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _engine.UnexpectedlyExited -= OnEngineUnexpectedlyExited;
        _tunnel.UnexpectedlyExited -= OnTunnelUnexpectedlyExited;
        _tunnel.LogReceived -= OnTunnelLogReceived;
        _tunnel.ProgressChanged -= OnTunnelProgressChanged;
        await _tunnel.DisposeAsync();
        await _engine.DisposeAsync();
        _engine.LogReceived -= OnEngineLogReceived;
        _probe.ProbeCompleted -= OnProbeCompleted;
        _probe.Dispose();
        _updateClient.Dispose();
        _logWriter.Dispose();
        _lifetime.Dispose();
    }
}

public sealed class ServiceStatusViewModel : INotifyPropertyChanged
{
    private string _detail = "Не проверяется";
    private Brush _indicator;

    public ServiceStatusViewModel(ServiceKind kind, string name, Brush indicator)
    {
        Kind = kind;
        Name = name;
        _indicator = indicator;
    }

    public ServiceKind Kind { get; }
    public string Name { get; }

    public string Detail
    {
        get => _detail;
        private set
        {
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public Brush Indicator
    {
        get => _indicator;
        private set
        {
            _indicator = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indicator)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string detail, Brush indicator)
    {
        Detail = detail;
        Indicator = indicator;
    }
}
