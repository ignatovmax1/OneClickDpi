using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OneClickDpi.App;

public sealed class WindowsProxyController : IDisposable
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string[] ManagedValues =
    [
        "ProxyEnable",
        "ProxyServer",
        "ProxyOverride",
        "AutoConfigURL"
    ];

    private readonly string _backupPath;
    private readonly object _gate = new();
    private bool _enabled;
    private bool _disposed;

    public WindowsProxyController(string backupPath)
    {
        _backupPath = backupPath;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public event Action<string>? LogReceived;

    public void RecoverStaleSettings()
    {
        lock (_gate)
        {
            if (_enabled || !File.Exists(_backupPath))
            {
                return;
            }

            RestoreBackupCore();
            LogReceived?.Invoke("Recovered Windows proxy settings left by an interrupted previous run.");
        }
    }

    public void Enable(int localProxyPort)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled)
            {
                return;
            }

            RecoverStaleSettings();
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
                ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");

            var existingProxyEnabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            var existingAutoConfig = key.GetValue("AutoConfigURL") as string;
            if (existingProxyEnabled || !string.IsNullOrWhiteSpace(existingAutoConfig))
            {
                LogReceived?.Invoke(
                    "An existing Windows proxy configuration will be saved and temporarily replaced.");
            }

            var backup = new ProxySettingsBackup(
                ManagedValues.ToDictionary(
                    name => name,
                    name => RegistryValueBackup.Capture(key, name),
                    StringComparer.Ordinal),
                DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
            File.WriteAllText(_backupPath, JsonSerializer.Serialize(backup), System.Text.Encoding.UTF8);

            try
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"127.0.0.1:{localProxyPort}", RegistryValueKind.String);
                key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
                NotifyWindows();
                _enabled = true;
                LogReceived?.Invoke($"Windows system proxy points to 127.0.0.1:{localProxyPort}.");
            }
            catch
            {
                RestoreBackupCore();
                throw;
            }
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            if (!_enabled && !File.Exists(_backupPath))
            {
                return;
            }

            RestoreBackupCore();
            _enabled = false;
            LogReceived?.Invoke("Windows system proxy settings were restored.");
        }
    }

    private void RestoreBackupCore()
    {
        if (!File.Exists(_backupPath))
        {
            return;
        }

        var json = File.ReadAllText(_backupPath, System.Text.Encoding.UTF8);
        var backup = JsonSerializer.Deserialize<ProxySettingsBackup>(json)
            ?? throw new InvalidDataException("The Windows proxy backup is invalid.");
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");

        foreach (var name in ManagedValues)
        {
            if (backup.Values.TryGetValue(name, out var value))
            {
                value.Restore(key, name);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        NotifyWindows();
        File.Delete(_backupPath);
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    private void OnProcessExit(object? sender, EventArgs eventArgs)
    {
        try
        {
            Disable();
        }
        catch
        {
            // The persisted backup is intentionally retained for recovery on next start.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Disable();
        }
        finally
        {
            _disposed = true;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr internet,
        int option,
        IntPtr buffer,
        int bufferLength);

    private sealed record ProxySettingsBackup(
        Dictionary<string, RegistryValueBackup> Values,
        DateTimeOffset CreatedAt);

    private sealed record RegistryValueBackup(
        bool Exists,
        RegistryValueKind Kind,
        string? StringValue,
        int? DwordValue)
    {
        public static RegistryValueBackup Capture(RegistryKey key, string name)
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                return new RegistryValueBackup(false, RegistryValueKind.None, null, null);
            }

            var kind = key.GetValueKind(name);
            return kind switch
            {
                RegistryValueKind.DWord => new RegistryValueBackup(true, kind, null, Convert.ToInt32(value)),
                RegistryValueKind.String or RegistryValueKind.ExpandString =>
                    new RegistryValueBackup(true, kind, Convert.ToString(value), null),
                _ => throw new InvalidOperationException($"Unsupported Windows proxy value type: {name} ({kind}).")
            };
        }

        public void Restore(RegistryKey key, string name)
        {
            if (!Exists)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                return;
            }

            object value = Kind switch
            {
                RegistryValueKind.DWord => DwordValue ?? 0,
                RegistryValueKind.String or RegistryValueKind.ExpandString => StringValue ?? string.Empty,
                _ => throw new InvalidDataException($"Unsupported saved registry type for {name}: {Kind}.")
            };
            key.SetValue(name, value, Kind);
        }
    }
}
