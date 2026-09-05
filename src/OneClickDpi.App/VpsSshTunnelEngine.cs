using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace OneClickDpi.App;

public sealed class VpsSshTunnelEngine : IAsyncDisposable
{
    private const string VpsHost = "185.173.144.43";
    private const int VpsSocksPort = 1080;

    private readonly int _localPort;
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private bool _intentionalStop;
    private Task? _acceptLoop;

    public VpsSshTunnelEngine(int localPort = 19083)
    {
        _localPort = localPort;
    }

    public bool IsRunning => _listener is not null;
    public int SocksPort => _localPort;
    public event Action<string>? LogReceived;
    public event EventHandler? UnexpectedlyExited;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _listener = new TcpListener(IPAddress.Loopback, _localPort);
        _listener.Start(128);
        _acceptLoop = AcceptLoopAsync(cancellationToken);
        LogReceived?.Invoke($"VPS SOCKS5: слушает на 127.0.0.1:{_localPort} -> {VpsHost}:{VpsSocksPort}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogReceived?.Invoke($"VPS SOCKS5 accept loop error: {exception.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            using var upstream = await ConnectToVpsAsync(cancellationToken).ConfigureAwait(false);
            using var clientStream = client.GetStream();
            using var upstreamStream = upstream.GetStream();

            var relay1 = clientStream.CopyToAsync(upstreamStream, cancellationToken);
            var relay2 = upstreamStream.CopyToAsync(clientStream, cancellationToken);
            await Task.WhenAny(relay1, relay2).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogReceived?.Invoke($"VPS SOCKS5 relay error: {exception.Message}");
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private async Task<TcpClient> ConnectToVpsAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(VpsHost, VpsSocksPort, cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        return client;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        try
        {
            _lifetime.Cancel();
            _listener?.Stop();
            _acceptLoop?.Wait(3000);
        }
        catch { }
        finally
        {
            _listener?.Dispose();
            _listener = null;
            _acceptLoop = null;
        }

        if (!_intentionalStop)
        {
            UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
