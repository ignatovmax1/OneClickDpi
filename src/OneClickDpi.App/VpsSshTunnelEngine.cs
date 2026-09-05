using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace OneClickDpi.App;

public sealed class VpsSshTunnelEngine : IAsyncDisposable
{
    private const string VpsHost = "185.173.144.43";
    private const int VpsSshPort = 22;
    private const string VpsUser = "root";
    private const string VpsPassword = "UPjjkvKdj68f";
    private const int RemoteSocksPort = 1080;

    private readonly int _localPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _intentionalStop;

    public VpsSshTunnelEngine(int localPort = 19083)
    {
        _localPort = localPort;
    }

    public bool IsRunning => _process is { HasExited: false };
    public int SocksPort => _localPort;
    public event Action<string>? LogReceived;
    public event EventHandler? UnexpectedlyExited;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            StopStaleProcess();
            _intentionalStop = false;

            LogReceived?.Invoke(
                $"VPS SSH: подключаемся к {VpsUser}@{VpsHost}:{VpsSshPort}, " +
                $"проброс SOCKS5 на 127.0.0.1:{_localPort} -> {VpsHost}:{RemoteSocksPort}");

            var ssh = FindSshExecutable();
            if (ssh is null)
            {
                throw new InvalidOperationException(
                    "ssh.exe не найден. Установите OpenSSH или Git for Windows.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ssh,
                WorkingDirectory = Path.GetDirectoryName(ssh) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("StrictHostKeyChecking=no");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("UserKnownHostsFile=NUL");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveInterval=30");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveCountMax=3");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(VpsSshPort.ToString());
            startInfo.ArgumentList.Add("-D");
            startInfo.ArgumentList.Add($"127.0.0.1:{_localPort}");
            startInfo.ArgumentList.Add($"{VpsUser}@{VpsHost}");

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += OnExited;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("SSH-процесс не запустился.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            WritePassword(process);

            await WaitForPortReadyAsync(cancellationToken).ConfigureAwait(false);
            LogReceived?.Invoke($"VPS SSH: туннель активен, SOCKS5 на 127.0.0.1:{_localPort}");
        }
        catch (OperationCanceledException)
        {
            StopStaleProcess();
            throw;
        }
        catch
        {
            StopStaleProcess();
            throw;
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
            StopStaleProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopStaleProcess()
    {
        _intentionalStop = true;
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void WritePassword(Process process)
    {
        try
        {
            process.StandardInput.WriteLine(VpsPassword);
            process.StandardInput.Flush();
        }
        catch (Exception exception)
        {
            LogReceived?.Invoke($"VPS SSH: не удалось передать пароль: {exception.Message}");
        }
    }

    private async Task WaitForPortReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
            {
                throw new InvalidOperationException("SSH-процесс завершился до готовности туннеля.");
            }

            try
            {
                using var test = new System.Net.Sockets.TcpClient();
                await test.ConnectAsync("127.0.0.1", _localPort, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("SSH-туннель не стал доступен за 15 секунд.");
    }

    private static string? FindSshExecutable()
    {
        var systemSsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "openssh", "ssh.exe");
        if (File.Exists(systemSsh))
        {
            return systemSsh;
        }

        var pathSsh = FindOnPath("ssh.exe");
        if (pathSsh is not null)
        {
            return pathSsh;
        }

        var gitSsh = @"C:\Program Files\Git\usr\bin\ssh.exe";
        if (File.Exists(gitSsh))
        {
            return gitSsh;
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            LogReceived?.Invoke($"VPS SSH: {e.Data}");
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        LogReceived?.Invoke("VPS SSH: процесс завершён.");
        if (!_intentionalStop)
        {
            UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
