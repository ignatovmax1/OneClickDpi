using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace OneClickDpi.App;

public sealed class VpsSshTunnelEngine : IAsyncDisposable
{
    private readonly int _localPort;
    private Process? _process;
    private bool _ready;
    private bool _intentionalStop;
    public VpsSshTunnelEngine(int localPort = 19083) => _localPort = localPort;
    public bool IsRunning => _ready && _process is { HasExited: false };
    public int SocksPort => _localPort;
    public event Action<string>? LogReceived;
    public event EventHandler? UnexpectedlyExited;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _intentionalStop = false;
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        // Reuse this PC's existing SSH identity and known-host record. Never embed credentials.
        foreach (var argument in new[] { "-N", "-T", "-D", $"127.0.0.1:{_localPort}",
            "-o", "HostName=185.173.144.43", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ExitOnForwardFailure=yes", "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "vps" })
            info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) LogReceived?.Invoke("VPS SSH: " + args.Data);
        };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process) && _ready && !_intentionalStop)
            {
                _ready = false;
                UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
            }
        };
        _process = process;
        try
        {
            if (!process.Start()) throw new IOException("Не удалось запустить SSH-туннель VPS.");
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new IOException($"SSH-туннель VPS завершился с кодом {process.ExitCode}. Проверьте подключение ssh vps на этом ПК.");
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", _localPort, timeout.Token).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token).ConfigureAwait(false);
                    var reply = new byte[2];
                    await stream.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
                    if (reply[0] != 5 || reply[1] != 0) throw new IOException("Некорректный ответ локального SOCKS5.");
                    if (process.HasExited) throw new IOException("SSH-туннель остановился при запуске.");
                    _ready = true;
                    LogReceived?.Invoke($"VPS SSH SOCKS5: 127.0.0.1:{_localPort} -> 185.173.144.43; ChatGPT, Claude, WhatsApp.");
                    return;
                }
                catch (SocketException) { await Task.Delay(100, timeout.Token).ConfigureAwait(false); }
            }
        }
        catch { await StopAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        _ready = false;
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
