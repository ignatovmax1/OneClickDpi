using System.Diagnostics;
using System.IO;

namespace OneClickDpi.App;

public sealed class TelegramProxyIntegration
{
    private readonly string _statePath;
    private readonly int _socksPort;

    public TelegramProxyIntegration(string statePath, int socksPort = 19050)
    {
        _statePath = statePath;
        _socksPort = socksPort;
    }

    public event Action<string>? LogReceived;

    public bool PromptIfNeeded()
    {
        if (File.Exists(_statePath))
        {
            return false;
        }

        var proxyUri = $"tg://socks?server=127.0.0.1&port={_socksPort}";
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = proxyUri,
                UseShellExecute = true
            });

            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(
                _statePath,
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{proxyUri}{Environment.NewLine}");
            LogReceived?.Invoke(
                "Opened Telegram's prefilled local SOCKS proxy screen; one confirmation is required by Telegram.");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogReceived?.Invoke($"Could not open Telegram proxy setup automatically: {exception.Message}");
            return false;
        }
    }
}
