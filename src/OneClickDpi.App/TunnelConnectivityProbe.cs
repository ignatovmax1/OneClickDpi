using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using OneClickDpi.Core;

namespace OneClickDpi.App;

public sealed class TunnelConnectivityProbe : IDisposable
{
    private static readonly ProbeEndpoint DiscordApi = new(
        ServiceKind.Discord,
        "Discord API via tunnel",
        new Uri("https://discord.com/api/v10/gateway"),
        TimeSpan.FromSeconds(20),
        IsRequired: true);

    private static readonly ProbeEndpoint DiscordGateway = new(
        ServiceKind.Discord,
        "Discord Gateway via tunnel",
        new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),
        TimeSpan.FromSeconds(20),
        IsRequired: true);

    private static readonly HashSet<string> UnsupportedExitRegions = new(StringComparer.Ordinal)
    {
        "BY", "CN", "CU", "HK", "IR", "KP", "MO", "RU", "SY", "T1"
    };

    private static readonly ProbeEndpoint YouTubeWeb = new(
        ServiceKind.YouTube,
        "YouTube Web via tunnel",
        new Uri("https://www.youtube.com/robots.txt"),
        TimeSpan.FromSeconds(20),
        IsRequired: true);

    private static readonly ProbeEndpoint YouTubeCdn = new(
        ServiceKind.YouTube,
        "YouTube CDN via direct DPI",
        new Uri("https://i.ytimg.com/generate_204"),
        TimeSpan.FromSeconds(20));

    private static readonly ProbeEndpoint TelegramWeb = new(
        ServiceKind.Telegram,
        "Telegram Web via tunnel",
        new Uri("https://telegram.org/robots.txt"),
        TimeSpan.FromSeconds(20));

    private static readonly ProbeEndpoint TelegramMtProto = new(
        ServiceKind.Telegram,
        "Telegram MTProto via tunnel",
        new Uri("tcp://telegram-dc:443"),
        TimeSpan.FromSeconds(25),
        IsRequired: true);

    private static readonly ProbeEndpoint ChatGptWeb = new(
        ServiceKind.ChatGPT,
        "ChatGPT Web via tunnel",
        new Uri("https://chatgpt.com/"),
        TimeSpan.FromSeconds(25),
        IsRequired: true);

    private static readonly ProbeEndpoint ClaudeWeb = new(
        ServiceKind.Claude,
        "Claude Web via tunnel",
        new Uri("https://claude.ai/"),
        TimeSpan.FromSeconds(25),
        IsRequired: true);

    private static readonly ProbeEndpoint WhatsAppWeb = new(
        ServiceKind.WhatsApp,
        "WhatsApp Web via tunnel",
        new Uri("https://web.whatsapp.com/"),
        TimeSpan.FromSeconds(20));

    private static readonly ProbeEndpoint TunnelExitRegion = new(
        ServiceKind.ChatGPT,
        "Tunnel exit region",
        new Uri("https://chatgpt.com/cdn-cgi/trace"),
        TimeSpan.FromSeconds(25),
        IsRequired: true);

    private static readonly string[] TelegramDataCenters =
    [
        "149.154.167.50",
        "149.154.175.50",
        "91.108.56.130"
    ];

    private readonly HttpClient _client;
    private readonly int _proxyPort;

    public TunnelConnectivityProbe(int proxyPort = 19081)
    {
        _proxyPort = proxyPort;
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OneClickDpi/0.6.6 tunnel-check");
    }

    public async Task<ConnectivitySnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            ProbeHttpAsync(DiscordApi, cancellationToken),
            ProbeDiscordGatewayAsync(cancellationToken),
            ProbeHttpAsync(YouTubeWeb, cancellationToken),
            ProbeHttpAsync(YouTubeCdn, cancellationToken),
            ProbeHttpAsync(TelegramWeb, cancellationToken),
            ProbeTelegramAsync(cancellationToken),
            ProbeHttpAsync(ChatGptWeb, cancellationToken),
            ProbeHttpAsync(ClaudeWeb, cancellationToken),
            ProbeHttpAsync(WhatsAppWeb, cancellationToken),
            ProbeExitRegionAsync(cancellationToken)).ConfigureAwait(false);

        return new ConnectivitySnapshot(
        [
            new ServiceProbeResult(
                ServiceKind.Discord,
                results.Where(result => result.Endpoint.Service == ServiceKind.Discord).ToArray(),
                RequiredSuccesses: 2),
            new ServiceProbeResult(
                ServiceKind.YouTube,
                results.Where(result => result.Endpoint.Service == ServiceKind.YouTube).ToArray(),
                RequiredSuccesses: 2),
            new ServiceProbeResult(
                ServiceKind.Telegram,
                results.Where(result => result.Endpoint.Service == ServiceKind.Telegram).ToArray(),
                RequiredSuccesses: 1),
            new ServiceProbeResult(
                ServiceKind.ChatGPT,
                results.Where(result => result.Endpoint.Service == ServiceKind.ChatGPT).ToArray(),
                RequiredSuccesses: 2),
            new ServiceProbeResult(
                ServiceKind.Claude,
                results.Where(result => result.Endpoint.Service == ServiceKind.Claude).ToArray(),
                RequiredSuccesses: 1),
            new ServiceProbeResult(
                ServiceKind.WhatsApp,
                results.Where(result => result.Endpoint.Service == ServiceKind.WhatsApp).ToArray(),
                RequiredSuccesses: 1)
        ]);
    }

    private async Task<EndpointProbeResult> ProbeDiscordGatewayAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscordGateway.Timeout);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = new WebProxy($"http://127.0.0.1:{_proxyPort}");
        socket.Options.SetRequestHeader("User-Agent", "OneClickDpi/0.6.6 tunnel-check");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(DiscordGateway.Uri, timeout.Token).ConfigureAwait(false);
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
                DiscordGateway,
                success,
                stopwatch.Elapsed,
                success ? 101 : null,
                success ? null : "Gateway did not send a Discord Hello frame");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                DiscordGateway,
                false,
                stopwatch.Elapsed,
                null,
                "Tunnel Gateway timeout");
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                DiscordGateway,
                false,
                stopwatch.Elapsed,
                null,
                FormatException(exception));
        }
    }

    private async Task<EndpointProbeResult> ProbeHttpAsync(
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
                HttpCompletionOption.ResponseContentRead,
                timeout.Token).ConfigureAwait(false);
            var body = endpoint.Service is ServiceKind.ChatGPT or ServiceKind.Claude
                ? await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)
                : string.Empty;
            stopwatch.Stop();
            var status = (int)response.StatusCode;
            var cloudflareChallenge = status == 403 && IsCloudflareChallenge(response);
            var verdict = endpoint.Service is ServiceKind.ChatGPT or ServiceKind.Claude
                ? AiServiceResponseClassifier.Evaluate(endpoint.Service, status, cloudflareChallenge, body)
                : new AiServiceResponseVerdict(status is >= 200 and < 500 && status != 451, $"HTTP {status}");
            var success = verdict.IsUsable;
            var reportedEndpoint = cloudflareChallenge
                ? endpoint with { Name = endpoint.Name + " (Cloudflare check)" }
                : endpoint;
            return new EndpointProbeResult(
                reportedEndpoint,
                success,
                stopwatch.Elapsed,
                status,
                success ? null : verdict.Error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(endpoint, false, stopwatch.Elapsed, null, "Tunnel timeout");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(endpoint, false, stopwatch.Elapsed, null, FormatException(exception));
        }
    }

    private async Task<EndpointProbeResult> ProbeExitRegionAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TunnelExitRegion.Timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TunnelExitRegion.Uri);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var status = (int)response.StatusCode;
            var region = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))?[4..]
                .ToUpperInvariant();
            var endpoint = TunnelExitRegion with
            {
                Name = string.IsNullOrWhiteSpace(region)
                    ? TunnelExitRegion.Name
                    : $"{TunnelExitRegion.Name} ({region})"
            };
            var success = status is >= 200 and < 400
                && region is not null
                && !UnsupportedExitRegions.Contains(region);
            var error = success
                ? null
                : status is < 200 or >= 400
                    ? $"HTTP {status}"
                    : string.IsNullOrWhiteSpace(region)
                        ? "Tunnel exit country was not reported"
                        : $"Unsupported tunnel exit country: {region}";
            return new EndpointProbeResult(endpoint, success, stopwatch.Elapsed, status, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                TunnelExitRegion,
                false,
                stopwatch.Elapsed,
                null,
                "Tunnel region check timeout");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                TunnelExitRegion,
                false,
                stopwatch.Elapsed,
                null,
                FormatException(exception));
        }
    }

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages);
    }

    private static bool IsCloudflareChallenge(HttpResponseMessage response) =>
        response.Headers.TryGetValues("cf-mitigated", out var values)
        && values.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase));

    private async Task<EndpointProbeResult> ProbeTelegramAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TelegramMtProto.Timeout);
        var stopwatch = Stopwatch.StartNew();
        var attempts = TelegramDataCenters
            .Select(address => ProbeTelegramDataCenterSafelyAsync(address, timeout.Token))
            .ToList();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);
            if (await finished.ConfigureAwait(false))
            {
                await timeout.CancelAsync().ConfigureAwait(false);
                stopwatch.Stop();
                return new EndpointProbeResult(TelegramMtProto, true, stopwatch.Elapsed, null, null);
            }
        }

        stopwatch.Stop();
        return new EndpointProbeResult(
            TelegramMtProto,
            false,
            stopwatch.Elapsed,
            null,
            timeout.IsCancellationRequested ? "Tunnel MTProto timeout" : "No tunneled MTProto response");
    }

    private async Task<bool> ProbeTelegramDataCenterSafelyAsync(
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _proxyPort, cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var connectRequest = Encoding.ASCII.GetBytes(
                $"CONNECT {address}:443 HTTP/1.1\r\nHost: {address}:443\r\nProxy-Connection: keep-alive\r\n\r\n");
            await stream.WriteAsync(connectRequest, cancellationToken).ConfigureAwait(false);
            var responseHeader = await ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!responseHeader.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)
                && !responseHeader.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return await TelegramMtProtoHandshake.TryAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string> ReadHttpHeaderAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            await stream.ReadExactlyAsync(single, cancellationToken).ConfigureAwait(false);
            buffer.WriteByte(single[0]);
            if (buffer.Length >= 4)
            {
                var data = buffer.GetBuffer();
                var end = checked((int)buffer.Length);
                if (data[end - 4] == '\r' && data[end - 3] == '\n'
                    && data[end - 2] == '\r' && data[end - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(data, 0, end);
                }
            }
        }

        throw new InvalidDataException("Local proxy returned an invalid HTTP response.");
    }

    public void Dispose() => _client.Dispose();
}
