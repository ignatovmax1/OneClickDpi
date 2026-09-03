using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace OneClickDpi.Core;

public interface IConnectivityProbe
{
    Task<ConnectivitySnapshot> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class HttpConnectivityProbe : IConnectivityProbe, IDisposable
{
    private static readonly IReadOnlyDictionary<ServiceKind, int> RequiredSuccesses =
        new Dictionary<ServiceKind, int>
        {
            [ServiceKind.Discord] = 2,
            [ServiceKind.YouTube] = 1,
            [ServiceKind.Telegram] = 1,
            [ServiceKind.WhatsApp] = 1
        };

    private static readonly IReadOnlyList<ProbeEndpoint> HttpEndpoints =
    [
        new(ServiceKind.Discord, "Discord API", new Uri("https://discord.com/api/v10/gateway"), TimeSpan.FromSeconds(6)),
        new(ServiceKind.Discord, "Discord CDN", new Uri("https://cdn.discordapp.com/embed/avatars/0.png"), TimeSpan.FromSeconds(6)),
        new(ServiceKind.YouTube, "YouTube Web", new Uri("https://www.youtube.com/robots.txt"), TimeSpan.FromSeconds(6), IsRequired: true),
        new(ServiceKind.YouTube, "YouTube CDN", new Uri("https://i.ytimg.com/generate_204"), TimeSpan.FromSeconds(6)),
        new(ServiceKind.Telegram, "Telegram Web", new Uri("https://web.telegram.org/"), TimeSpan.FromSeconds(6)),
        new(ServiceKind.Telegram, "Telegram Site", new Uri("https://telegram.org/robots.txt"), TimeSpan.FromSeconds(6)),
        new(ServiceKind.WhatsApp, "WhatsApp Web", new Uri("https://web.whatsapp.com/"), TimeSpan.FromSeconds(6))
    ];

    private static readonly ProbeEndpoint DiscordGatewayEndpoint = new(
        ServiceKind.Discord,
        "Discord Gateway",
        new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),
        TimeSpan.FromSeconds(7),
        IsRequired: true);

    private static readonly ProbeEndpoint TelegramMtProtoEndpoint = new(
        ServiceKind.Telegram,
        "Telegram MTProto",
        new Uri("tcp://telegram-dc:443"),
        TimeSpan.FromSeconds(5),
        IsRequired: true);

    private static readonly string[] TelegramDataCenters =
    [
        "149.154.167.50",
        "149.154.175.50",
        "91.108.56.130"
    ];

    private readonly HttpClient _client;

    public event Action<ConnectivitySnapshot>? ProbeCompleted;

    public HttpConnectivityProbe()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OneClickDpi/0.6 connectivity-check");
    }

    public async Task<ConnectivitySnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        var tasks = HttpEndpoints
            .Select(endpoint => ProbeHttpEndpointAsync(endpoint, cancellationToken))
            .Append(ProbeDiscordGatewayAsync(cancellationToken))
            .Append(ProbeTelegramMtProtoAsync(cancellationToken));
        var endpointResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        var services = endpointResults
            .GroupBy(result => result.Endpoint.Service)
            .Select(group => new ServiceProbeResult(
                group.Key,
                group.ToArray(),
                RequiredSuccesses[group.Key]))
            .OrderBy(result => result.Service)
            .ToArray();

        var snapshot = new ConnectivitySnapshot(services);
        ProbeCompleted?.Invoke(snapshot);
        return snapshot;
    }

    private async Task<EndpointProbeResult> ProbeHttpEndpointAsync(
        ProbeEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(endpoint.Timeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var success = statusCode is >= 200 and < 500
                && statusCode != (int)HttpStatusCode.ProxyAuthenticationRequired
                && statusCode != 451;

            return new EndpointProbeResult(
                endpoint,
                success,
                stopwatch.Elapsed,
                statusCode,
                success ? null : $"HTTP {statusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(endpoint, false, stopwatch.Elapsed, null, "Timeout");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(endpoint, false, stopwatch.Elapsed, null, exception.Message);
        }
    }

    private static async Task<EndpointProbeResult> ProbeDiscordGatewayAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscordGatewayEndpoint.Timeout);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "OneClickDpi/0.6 connectivity-check");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await socket.ConnectAsync(DiscordGatewayEndpoint.Uri, timeout.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var success = socket.State == WebSocketState.Open
                && result.MessageType == WebSocketMessageType.Text
                && payload.Contains("\"op\":10", StringComparison.Ordinal);

            if (socket.State == WebSocketState.Open)
            {
                socket.Abort();
            }

            return new EndpointProbeResult(
                DiscordGatewayEndpoint,
                success,
                stopwatch.Elapsed,
                success ? 101 : null,
                success ? null : "Gateway did not send a Discord Hello frame");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                DiscordGatewayEndpoint,
                false,
                stopwatch.Elapsed,
                null,
                "Gateway timeout");
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                DiscordGatewayEndpoint,
                false,
                stopwatch.Elapsed,
                null,
                exception.Message);
        }
    }

    private static async Task<EndpointProbeResult> ProbeTelegramMtProtoAsync(
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TelegramMtProtoEndpoint.Timeout);
        var attempts = TelegramDataCenters
            .Select(address => TryTelegramDataCenterSafelyAsync(address, timeout.Token))
            .ToList();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);
            if (await finished.ConfigureAwait(false))
            {
                await timeout.CancelAsync().ConfigureAwait(false);
                stopwatch.Stop();
                return new EndpointProbeResult(
                    TelegramMtProtoEndpoint,
                    true,
                    stopwatch.Elapsed,
                    null,
                    null);
            }
        }

        stopwatch.Stop();
        return new EndpointProbeResult(
            TelegramMtProtoEndpoint,
            false,
            stopwatch.Elapsed,
            null,
            timeout.IsCancellationRequested ? "MTProto timeout" : "No MTProto response");
    }

    private static async Task<bool> TryTelegramDataCenterSafelyAsync(
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TryTelegramDataCenterAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TryTelegramDataCenterAsync(
        string address,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Parse(address), 443, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        return await TelegramMtProtoHandshake.TryAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}
