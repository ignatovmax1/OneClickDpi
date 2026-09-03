using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneClickDpi.App;

public sealed class TorTunnelEngine : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tor.exe"] = "EA61BA0ED5B89D0622D2894B2A86F5FF34CE9B48E6E40D64341E7C0C7EE03E08",
            ["lyrebird.exe"] = "83D4D39D438A36066AF5161806A448B5D099033DDA901ECD0B2663EC58A5790F",
            ["pt_config.json"] = "3F11D303C30191B3B1D382B9BADD882D87FD87550D061F7D25A1B31226FC9B75",
            ["geoip"] = "AF9CCD060A712D090EE07D5678B5D45B0038EC1573116FAE724A6695A8485703",
            ["geoip6"] = "2393124667BA2CCB4C806F226A33B2EF7A8188D1BA55831C1A5D3DCA2B062514"
        };

    private readonly string _rootDirectory;
    private readonly string _dataDirectory;
    private readonly int _socksPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private TaskCompletionSource? _bootstrapped;
    private bool _intentionalStop;

    public TorTunnelEngine(string rootDirectory, string dataDirectory, int socksPort = 19050)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _socksPort = socksPort;
    }

    public bool IsRunning => _process is { HasExited: false };
    public int SocksPort => _socksPort;
    public event Action<string>? LogReceived;
    public event Action<int>? BootstrapProgressChanged;
    public event EventHandler? UnexpectedlyExited;

    private string TorExecutable => Path.Combine(_rootDirectory, "tor.exe");
    private string LyrebirdExecutable => Path.Combine(_rootDirectory, "lyrebird.exe");
    private string TransportConfig => Path.Combine(_rootDirectory, "pt_config.json");
    private string GeoIp => Path.Combine(_rootDirectory, "geoip");
    private string GeoIp6 => Path.Combine(_rootDirectory, "geoip6");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            await ValidateAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_dataDirectory);
            var torrcPath = Path.Combine(_dataDirectory, "oneclickdpi-torrc");
            await File.WriteAllTextAsync(
                torrcPath,
                BuildConfiguration(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            var bootstrapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bootstrapped = bootstrapped;
            _intentionalStop = false;
            var startInfo = new ProcessStartInfo
            {
                FileName = TorExecutable,
                WorkingDirectory = _rootDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(torrcPath);

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
                throw new InvalidOperationException("Tor tunnel process did not start.");
            }

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LogReceived?.Invoke($"Tor Snowflake tunnel started with process id {process.Id}.");

            try
            {
                await bootstrapped.Task
                    .WaitAsync(TimeSpan.FromSeconds(100), cancellationToken)
                    .ConfigureAwait(false);
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
        var process = _process;
        _process = null;
        _bootstrapped = null;
        if (process is null)
        {
            return;
        }

        _intentionalStop = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.OutputDataReceived -= OnOutput;
            process.ErrorDataReceived -= OnOutput;
            process.Exited -= OnExited;
            process.Dispose();
            LogReceived?.Invoke("Tor tunnel stopped.");
        }
    }

    private string BuildConfiguration()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TransportConfig, Encoding.UTF8));
        var bridges = document.RootElement
            .GetProperty("bridges")
            .GetProperty("snowflake")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        if (bridges.Length == 0)
        {
            throw new InvalidDataException("Tor Snowflake bridge configuration is empty.");
        }

        var lines = new List<string>
        {
            "ClientOnly 1",
            "AvoidDiskWrites 1",
            // Telegram Desktop connects to fixed DC IPs. SafeSocks deliberately rejects
            // IP-literal SOCKS destinations, while our local proxy already restricts those
            // routes to the validated Telegram network list.
            "SafeSocks 0",
            "TestSocks 0",
            "ClientUseIPv6 0",
            // Keep a broad exit pool while excluding regions that ChatGPT or Claude
            // do not support. A narrow country allowlist makes first bootstrap slow.
            "ExcludeExitNodes {ru},{by},{cn},{ir},{kp},{sy},{cu},{hk},{mo}",
            $"SocksPort 127.0.0.1:{_socksPort}",
            $"DataDirectory {QuotePath(_dataDirectory)}",
            $"GeoIPFile {QuotePath(GeoIp)}",
            $"GeoIPv6File {QuotePath(GeoIp6)}",
            "Log notice stdout",
            "UseBridges 1",
            // Tor on Windows passes quote characters through to CreateProcess for managed
            // transports. The process working directory is the validated bundle directory,
            // so a plain relative executable name is both safe and space-independent.
            "ClientTransportPlugin snowflake exec lyrebird.exe",
            $"__OwningControllerProcess {Environment.ProcessId}"
        };
        lines.AddRange(bridges.Select(bridge => $"Bridge {bridge}"));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string QuotePath(string path) => '"' + path.Replace('\\', '/') + '"';

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var files = new[]
        {
            TorExecutable,
            LyrebirdExecutable,
            TransportConfig,
            GeoIp,
            GeoIp6
        };

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"Required Tor component is missing: {Path.GetFileName(file)}", file);
            }

            await using var stream = File.OpenRead(file);
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            var expectedHash = ExpectedHashes[Path.GetFileName(file)];
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Tor integrity check failed for {Path.GetFileName(file)}. " +
                    $"Expected {expectedHash}, got {actualHash}.");
            }
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            return;
        }

        LogReceived?.Invoke(eventArgs.Data);
        const string marker = "Bootstrapped ";
        var markerIndex = eventArgs.Data.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var numberStart = markerIndex + marker.Length;
            var numberEnd = eventArgs.Data.IndexOf('%', numberStart);
            if (numberEnd > numberStart
                && int.TryParse(eventArgs.Data[numberStart..numberEnd], out var percentage))
            {
                BootstrapProgressChanged?.Invoke(percentage);
            }
        }

        if (eventArgs.Data.Contains("Bootstrapped 100%", StringComparison.OrdinalIgnoreCase))
        {
            _bootstrapped?.TrySetResult();
        }
    }

    private void OnExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process || !ReferenceEquals(process, _process))
        {
            return;
        }

        _bootstrapped?.TrySetException(
            new InvalidOperationException($"Tor tunnel exited with code {process.ExitCode}."));
        if (!_intentionalStop)
        {
            LogReceived?.Invoke($"Tor tunnel stopped unexpectedly with code {process.ExitCode}.");
            UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
