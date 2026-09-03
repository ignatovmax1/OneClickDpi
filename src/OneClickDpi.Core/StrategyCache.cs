using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneClickDpi.Core;

public interface IStrategyCache
{
    Task<string?> GetAsync(string networkFingerprint, CancellationToken cancellationToken);
    Task SetAsync(string networkFingerprint, string strategyId, CancellationToken cancellationToken);
}

public interface INetworkFingerprintProvider
{
    string GetFingerprint();
}

public sealed class NetworkFingerprintProvider : INetworkFingerprintProvider
{
    public string GetFingerprint()
    {
        var network = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(item => new
            {
                Interface = item,
                Properties = item.GetIPProperties()
            })
            .Where(item => item.Properties.GatewayAddresses.Count > 0)
            .OrderByDescending(item => item.Interface.Speed)
            .ThenBy(item => item.Interface.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (network is null)
        {
            return "offline";
        }

        var gateway = string.Join(',', network.Properties.GatewayAddresses.Select(item => item.Address));
        var dns = string.Join(',', network.Properties.DnsAddresses);
        var material = $"{network.Interface.Id}|{network.Interface.NetworkInterfaceType}|{gateway}|{dns}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash.AsSpan(0, 12));
    }
}

public sealed class JsonStrategyCache : IStrategyCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonStrategyCache(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<string?> GetAsync(string networkFingerprint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return entries.TryGetValue(networkFingerprint, out var entry) ? entry.StrategyId : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(
        string networkFingerprint,
        string strategyId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            entries[networkFingerprint] = new CacheEntry(strategyId, DateTimeOffset.UtcNow);

            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("Strategy cache path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, entries, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, CacheEntry>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, CacheEntry>>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    private sealed record CacheEntry(string StrategyId, DateTimeOffset UpdatedAt);
}
