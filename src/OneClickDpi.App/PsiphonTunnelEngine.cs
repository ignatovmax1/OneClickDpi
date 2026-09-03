using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneClickDpi.Core;

namespace OneClickDpi.App;

public sealed class PsiphonTunnelEngine : IAsyncDisposable
{
    private static readonly string[] EgressRegions = ["GB", "US", "CA", "FR", "DE", "FI", "NL"];
    private static readonly TimeSpan ConnectionAttemptTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ServiceValidationTimeout = TimeSpan.FromSeconds(20);
    private static readonly (ServiceKind Service, Uri Uri)[] AiValidationEndpoints =
    [
        (ServiceKind.ChatGPT, new Uri("https://chatgpt.com/")),
        (ServiceKind.Claude, new Uri("https://claude.ai/"))
    ];
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["psiphon-tunnel-core.exe"] = "AEC4C8221808227E8CFE50EFCC9C6F18964FE8928A25B3D925973BFF33B874B2",
            ["server_list.dat"] = "02DA7488733CB5920E36AF675A3C62D0278330079338E41C453CADB527BFF4D2",
            ["psiphon-template.json"] = "FDB63A008F9AE1515C2722B80B753D6A1C5465FE8A3E00EF711FB36E8E96CC84"
        };

    private readonly string _rootDirectory;
    private readonly string _dataDirectory;
    private readonly string _statePath;
    private readonly int _socksPort;
    private readonly int _httpPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private TaskCompletionSource? _connected;
    private bool _intentionalStop;

    public PsiphonTunnelEngine(
        string rootDirectory,
        string dataDirectory,
        string statePath,
        int socksPort = 19083,
        int httpPort = 19082)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _statePath = Path.GetFullPath(statePath);
        _socksPort = socksPort;
        _httpPort = httpPort;
    }

    public bool IsRunning => _process is { HasExited: false };
    public int SocksPort => _socksPort;
    public event Action<string>? LogReceived;
    public event EventHandler? UnexpectedlyExited;

    private string Executable => Path.Combine(_rootDirectory, "psiphon-tunnel-core.exe");
    private string TemplateConfiguration => Path.Combine(_rootDirectory, "psiphon-template.json");
    private string ServerList => Path.Combine(_rootDirectory, "server_list.dat");

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
            RecoverStaleProcess();
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var configurationPath = Path.Combine(_dataDirectory, "oneclickdpi-psiphon.json");
            Exception? lastError = null;
            for (var index = 0; index < EgressRegions.Length; index++)
            {
                var region = EgressRegions[index];
                LogReceived?.Invoke(
                    $"Psiphon AI attempt {index + 1}/{EgressRegions.Length}: region {region}, " +
                    $"timeout {(int)ConnectionAttemptTimeout.TotalSeconds} seconds.");
                try
                {
                    await StartAttemptAsync(configurationPath, region, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is TimeoutException or InvalidOperationException)
                {
                    lastError = exception;
                    LogReceived?.Invoke($"Psiphon region {region} rejected: {exception.Message}");
                    if (index + 1 < EgressRegions.Length)
                    {
                        LogReceived?.Invoke(
                            $"Psiphon region {region} did not pass the AI service check; trying another region.");
                    }
                }
            }

            throw new TimeoutException(
                "Ни один из доступных маршрутов ИИ не прошёл проверку ChatGPT и Claude.",
                lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAttemptAsync(
        string configurationPath,
        string region,
        CancellationToken cancellationToken)
    {
        await WriteRuntimeConfigurationAsync(configurationPath, region, cancellationToken)
            .ConfigureAwait(false);

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connected = connected;
        _intentionalStop = false;
        var startInfo = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = _rootDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(configurationPath);
        startInfo.ArgumentList.Add("-dataRootDirectory");
        startInfo.ArgumentList.Add(_dataDirectory);
        startInfo.ArgumentList.Add("-serverList");
        startInfo.ArgumentList.Add(ServerList);

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
            throw new InvalidOperationException("Psiphon tunnel process did not start.");
        }

        _process = process;
        try
        {
            await File.WriteAllTextAsync(
                _statePath,
                process.Id.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LogReceived?.Invoke(
                $"Psiphon AI tunnel started with process id {process.Id} for region {region}.");

            await connected.Task
                .WaitAsync(ConnectionAttemptTimeout, cancellationToken)
                .ConfigureAwait(false);
            await ValidateAiServicesAsync(region, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ValidateAiServicesAsync(string region, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ServiceValidationTimeout);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{_httpPort}"),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");

        var checks = AiValidationEndpoints.Select(endpoint =>
            ValidateAiServiceAsync(client, endpoint.Service, endpoint.Uri, timeout.Token));
        AiServiceResponseVerdict[] results;
        try
        {
            results = await Task.WhenAll(checks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"AI service validation timed out for Psiphon region {region}.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"AI service validation failed for Psiphon region {region}: {exception.Message}",
                exception);
        }

        var failed = results.Where(result => !result.IsUsable).ToArray();
        if (failed.Length > 0)
        {
            throw new InvalidOperationException(
                $"Psiphon region {region} was rejected: " +
                string.Join("; ", failed.Select(result => result.Error ?? "AI service unavailable")) + ".");
        }

        LogReceived?.Invoke($"Psiphon region {region} passed the ChatGPT and Claude checks.");
    }

    private static async Task<AiServiceResponseVerdict> ValidateAiServiceAsync(
        HttpClient client,
        ServiceKind service,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var challenge = response.Headers.TryGetValues("cf-mitigated", out var values)
            && values.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase));
        return AiServiceResponseClassifier.Evaluate(service, (int)response.StatusCode, challenge, body);
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
        _connected = null;
        _intentionalStop = true;
        try
        {
            if (process is { HasExited: false })
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
        catch (Win32Exception)
        {
        }
        finally
        {
            if (process is not null)
            {
                process.OutputDataReceived -= OnOutput;
                process.ErrorDataReceived -= OnOutput;
                process.Exited -= OnExited;
                process.Dispose();
                LogReceived?.Invoke("Psiphon AI tunnel stopped.");
            }

            TryDeleteStateFile();
        }
    }

    private async Task WriteRuntimeConfigurationAsync(
        string destination,
        string region,
        CancellationToken cancellationToken)
    {
        var template = await File.ReadAllTextAsync(
            TemplateConfiguration,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        var configuration = JsonNode.Parse(template)?.AsObject()
            ?? throw new InvalidDataException("Psiphon configuration template is invalid.");

        configuration["DataRootDirectory"] = _dataDirectory;
        configuration["LocalSocksProxyPort"] = _socksPort;
        configuration["LocalHttpProxyPort"] = _httpPort;
        configuration["EgressRegion"] = region;
        configuration["ClientPlatform"] = "Windows_OneClickDpi";
        configuration["EnableUpgradeDownload"] = false;
        configuration["EnableFeedbackUpload"] = false;
        configuration["EmitDiagnosticNetworkParameters"] = false;
        configuration["EmitDiagnosticNotices"] = false;
        configuration["EmitServerAlerts"] = false;

        foreach (var name in new[]
                 {
                     "NetworkID",
                     "DeviceRegion",
                     "MigrateDataStoreDirectory",
                     "MigrateObfuscatedServerListDownloadDirectory",
                     "MigrateRemoteServerListDownloadFilename",
                     "MigrateUpgradeDownloadFilename"
                 })
        {
            configuration.Remove(name);
        }

        await File.WriteAllTextAsync(
            destination,
            configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        foreach (var file in new[] { Executable, TemplateConfiguration, ServerList })
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException(
                    $"Required Psiphon component is missing: {Path.GetFileName(file)}",
                    file);
            }

            await using var stream = File.OpenRead(file);
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            var expectedHash = ExpectedHashes[Path.GetFileName(file)];
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Psiphon integrity check failed for {Path.GetFileName(file)}.");
            }
        }
    }

    private void RecoverStaleProcess()
    {
        if (!File.Exists(_statePath)
            || !int.TryParse(File.ReadAllText(_statePath), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var processPath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(processPath)
                && Path.GetFullPath(processPath).Equals(Executable, StringComparison.OrdinalIgnoreCase)
                && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                LogReceived?.Invoke("Recovered a stale OneClickDpi Psiphon process.");
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            TryDeleteStateFile();
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(eventArgs.Data);
            var root = document.RootElement;
            if (!root.TryGetProperty("noticeType", out var typeElement))
            {
                return;
            }

            var noticeType = typeElement.GetString();
            root.TryGetProperty("data", out var data);
            switch (noticeType)
            {
                case "ListeningSocksProxyPort":
                    LogReceived?.Invoke($"Psiphon SOCKS proxy is listening on 127.0.0.1:{_socksPort}.");
                    break;
                case "ConnectedServerRegion":
                    if (data.TryGetProperty("serverRegion", out var region))
                    {
                        LogReceived?.Invoke($"Psiphon AI exit region: {region.GetString()}.");
                    }
                    break;
                case "Tunnels":
                    if (data.TryGetProperty("count", out var count) && count.GetInt32() > 0)
                    {
                        _connected?.TrySetResult();
                        LogReceived?.Invoke("Psiphon AI tunnel connected.");
                    }
                    break;
                case "Error":
                    if (data.TryGetProperty("message", out var error))
                    {
                        LogReceived?.Invoke($"Psiphon: {error.GetString()}");
                    }
                    break;
            }
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or FormatException)
        {
        }
    }

    private void OnExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process || !ReferenceEquals(process, _process))
        {
            return;
        }

        _connected?.TrySetException(
            new InvalidOperationException($"Psiphon AI tunnel exited with code {process.ExitCode}."));
        TryDeleteStateFile();
        if (!_intentionalStop)
        {
            LogReceived?.Invoke($"Psiphon AI tunnel stopped unexpectedly with code {process.ExitCode}.");
            UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TryDeleteStateFile()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
