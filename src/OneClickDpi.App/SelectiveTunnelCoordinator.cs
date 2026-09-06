using OneClickDpi.Core;

namespace OneClickDpi.App;

public sealed class SelectiveTunnelCoordinator : IAsyncDisposable
{
    private readonly TorTunnelEngine _tor;
    private readonly VpsSshTunnelEngine _vps;
    private readonly WindowsProxyController _windowsProxy;
    private readonly SelectiveRouteMatcher _matcher;
    private readonly TelegramProxyIntegration _telegramProxy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SelectiveHttpProxy? _localProxy;
    private TunnelConnectivityProbe? _probe;

    public SelectiveTunnelCoordinator(
        TorTunnelEngine tor,
        VpsSshTunnelEngine vps,
        WindowsProxyController windowsProxy,
        SelectiveRouteMatcher matcher,
        TelegramProxyIntegration telegramProxy)
    {
        _tor = tor ?? throw new ArgumentNullException(nameof(tor));
        _vps = vps ?? throw new ArgumentNullException(nameof(vps));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _telegramProxy = telegramProxy ?? throw new ArgumentNullException(nameof(telegramProxy));
        _tor.LogReceived += ForwardLog;
        _vps.LogReceived += ForwardLog;
        _tor.BootstrapProgressChanged += ForwardProgress;
        _windowsProxy.LogReceived += ForwardLog;
        _telegramProxy.LogReceived += ForwardLog;
        _tor.UnexpectedlyExited += OnTorUnexpectedlyExited;
        _vps.UnexpectedlyExited += OnVpsUnexpectedlyExited;
        _windowsProxy.RecoverStaleSettings();
    }

    public bool IsRunning => _tor.IsRunning && _localProxy?.IsRunning == true && _windowsProxy.IsEnabled;
    public bool AiTunnelRunning => _vps.IsRunning;
    public bool TelegramSetupPromptOpened { get; private set; }
    public event Action<string>? LogReceived;
    public event Action<int>? ProgressChanged;
    public event EventHandler? UnexpectedlyExited;

    public async Task<ConnectivitySnapshot> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramSetupPromptOpened = false;
            if (IsRunning && _probe is not null)
            {
                return await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var startLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var torStart = _tor.StartAsync(startLifetime.Token);
                var vpsStart = _vps.StartAsync(startLifetime.Token);
                try
                {
                    await torStart.ConfigureAwait(false);
                }
                catch
                {
                    await startLifetime.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await vpsStart.ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    throw;
                }

                try
                {
                    await vpsStart.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogReceived?.Invoke(
                        $"VPS SSH tunnel unavailable; AI services will not work: {exception.Message}");
                }

                _localProxy = new SelectiveHttpProxy(
                    _matcher,
                    socksPort: _tor.SocksPort,
                    aiSocksPort: _vps.SocksPort);
                _localProxy.LogReceived += ForwardLog;
                await _localProxy.StartAsync(cancellationToken).ConfigureAwait(false);

                _probe = new TunnelConnectivityProbe(_localProxy.Port);
                var snapshot = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
                LogSnapshot(snapshot);
                if (snapshot.HealthyServiceCount == 0)
                {
                    throw new InvalidOperationException(
                        "Локальный туннель запустился, но целевые сервисы через него не ответили.");
                }

                _windowsProxy.Enable(_localProxy.Port);
                TelegramSetupPromptOpened = _telegramProxy.PromptIfNeeded();
                return snapshot;
            }
            catch
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _windowsProxy.Disable();

        var probe = _probe;
        _probe = null;
        probe?.Dispose();

        var localProxy = _localProxy;
        _localProxy = null;
        if (localProxy is not null)
        {
            localProxy.LogReceived -= ForwardLog;
            await localProxy.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _vps.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _tor.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void LogSnapshot(ConnectivitySnapshot snapshot)
    {
        foreach (var endpoint in snapshot.Services.SelectMany(service => service.Endpoints))
        {
            LogReceived?.Invoke(
                $"{endpoint.Endpoint.Name}: {(endpoint.IsSuccess ? "OK" : "FAIL")} " +
                $"{endpoint.Latency.TotalMilliseconds:0} ms" +
                (endpoint.StatusCode is int status ? $" HTTP {status}" : string.Empty) +
                (string.IsNullOrWhiteSpace(endpoint.Error) ? string.Empty : $" ({endpoint.Error})"));
        }
    }

    private void ForwardLog(string message) => LogReceived?.Invoke(message);
    private void ForwardProgress(int percentage) => ProgressChanged?.Invoke(percentage);

    private void OnTorUnexpectedlyExited(object? sender, EventArgs eventArgs)
    {
        try
        {
            _windowsProxy.Disable();
        }
        catch (Exception exception)
        {
            LogReceived?.Invoke($"Failed to restore proxy after Tor exit: {exception.Message}");
        }

        UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
    }

    private void OnVpsUnexpectedlyExited(object? sender, EventArgs eventArgs)
    {
        try
        {
            _windowsProxy.Disable();
        }
        catch (Exception exception)
        {
            LogReceived?.Invoke($"Failed to restore proxy after VPS SSH exit: {exception.Message}");
        }

        LogReceived?.Invoke("VPS SSH tunnel stopped; reconnect is required.");
        UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _tor.LogReceived -= ForwardLog;
        _vps.LogReceived -= ForwardLog;
        _tor.BootstrapProgressChanged -= ForwardProgress;
        _windowsProxy.LogReceived -= ForwardLog;
        _telegramProxy.LogReceived -= ForwardLog;
        _tor.UnexpectedlyExited -= OnTorUnexpectedlyExited;
        _vps.UnexpectedlyExited -= OnVpsUnexpectedlyExited;
        await _tor.DisposeAsync().ConfigureAwait(false);
        await _vps.DisposeAsync().ConfigureAwait(false);
        _windowsProxy.Dispose();
        _gate.Dispose();
    }
}
