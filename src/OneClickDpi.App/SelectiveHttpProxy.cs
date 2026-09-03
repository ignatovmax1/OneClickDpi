using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OneClickDpi.Core;

namespace OneClickDpi.App;

public sealed class SelectiveHttpProxy : IAsyncDisposable
{
    private const int MaximumHeaderSize = 64 * 1024;
    private readonly SelectiveRouteMatcher _matcher;
    private readonly string _socksHost;
    private readonly int _socksPort;
    private readonly string _aiSocksHost;
    private readonly int _aiSocksPort;
    private readonly int _listenPort;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _connections = [];
    private readonly object _connectionsGate = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public SelectiveHttpProxy(
        SelectiveRouteMatcher matcher,
        string socksHost = "127.0.0.1",
        int socksPort = 19050,
        string? aiSocksHost = null,
        int? aiSocksPort = null,
        int listenPort = 19081)
    {
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _socksHost = socksHost;
        _socksPort = socksPort;
        _aiSocksHost = aiSocksHost ?? socksHost;
        _aiSocksPort = aiSocksPort ?? socksPort;
        _listenPort = listenPort;
    }

    public bool IsRunning => _listener is not null;
    public int Port => _listenPort;
    public event Action<string>? LogReceived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        var listener = new TcpListener(IPAddress.Loopback, _listenPort);
        listener.Start(128);
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, _lifetime.Token);
        LogReceived?.Invoke($"Selective HTTP CONNECT proxy is listening on 127.0.0.1:{_listenPort}.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var task = HandleClientAsync(client, cancellationToken);
                lock (_connectionsGate)
                {
                    _connections.RemoveAll(connection => connection.IsCompleted);
                    _connections.Add(task);
                }
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
            LogReceived?.Invoke($"Local proxy accept loop failed: {exception.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            string? destination = null;
            try
            {
                await using var clientStream = client.GetStream();
                var request = await ReadHeaderAsync(clientStream, cancellationToken).ConfigureAwait(false);
                var route = ParseRequest(request.Header);
                destination = $"{route.Host}:{route.Port}";
                var selectedRoute = _matcher.GetRoute(route.Host);
                var tunneled = selectedRoute != SelectiveRoute.Direct;
                var upstreamSocksHost = selectedRoute == SelectiveRoute.Ai ? _aiSocksHost : _socksHost;
                var upstreamSocksPort = selectedRoute == SelectiveRoute.Ai ? _aiSocksPort : _socksPort;

                using var upstream = tunneled
                    ? await Socks5Connector.ConnectAsync(
                        upstreamSocksHost,
                        upstreamSocksPort,
                        route.Host,
                        route.Port,
                        cancellationToken).ConfigureAwait(false)
                    : await ConnectDirectAsync(route.Host, route.Port, cancellationToken).ConfigureAwait(false);
                await using var upstreamStream = upstream.GetStream();

                if (route.IsConnect)
                {
                    await clientStream.WriteAsync(
                        "HTTP/1.1 200 Connection Established\r\nProxy-Agent: OneClickDpi/0.6.6\r\n\r\n"u8.ToArray(),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var rewritten = RewriteForwardRequest(request.Header, route);
                    await upstreamStream.WriteAsync(rewritten, cancellationToken).ConfigureAwait(false);
                }

                if (request.TrailingData.Length > 0)
                {
                    await upstreamStream.WriteAsync(request.TrailingData, cancellationToken).ConfigureAwait(false);
                }

                LogReceived?.Invoke(
                    $"Proxy route {route.Host}:{route.Port} -> {selectedRoute.ToString().ToUpperInvariant()}.");

                using var relayLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var toUpstream = clientStream.CopyToAsync(upstreamStream, relayLifetime.Token);
                var toClient = upstreamStream.CopyToAsync(clientStream, relayLifetime.Token);
                await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
                await relayLifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException)
            {
                LogReceived?.Invoke(
                    $"Local proxy connection failed" +
                    (destination is null ? string.Empty : $" for {destination}") +
                    $": {exception.Message}");
            }
        }
    }

    private static async Task<TcpClient> ConnectDirectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<ProxyRequest> ReadHeaderAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (buffer.Length < MaximumHeaderSize && headerEnd < 0)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("Proxy client closed before sending a complete request.");
            }

            buffer.Write(chunk, 0, count);
            headerEnd = FindHeaderEnd(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        }

        if (headerEnd < 0)
        {
            throw new InvalidDataException("Proxy request header is too large or incomplete.");
        }

        var data = buffer.ToArray();
        var headerLength = headerEnd + 4;
        return new ProxyRequest(data[..headerLength], data[headerLength..]);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var index = 0; index <= data.Length - 4; index++)
        {
            if (data[index] == '\r' && data[index + 1] == '\n'
                && data[index + 2] == '\r' && data[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static ParsedRoute ParseRequest(byte[] headerBytes)
    {
        var header = Encoding.ASCII.GetString(headerBytes);
        var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd <= 0)
        {
            throw new InvalidDataException("Invalid proxy request line.");
        }

        var requestLine = header[..firstLineEnd].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new InvalidDataException("Invalid proxy request line.");
        }

        if (requestLine[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = ParseAuthority(requestLine[1], 443);
            return new ParsedRoute(host, port, true, requestLine[0], requestLine[1], requestLine[2]);
        }

        if (Uri.TryCreate(requestLine[1], UriKind.Absolute, out var uri))
        {
            return new ParsedRoute(
                uri.Host,
                uri.IsDefaultPort ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port,
                false,
                requestLine[0],
                string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery,
                requestLine[2]);
        }

        var hostHeader = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        if (hostHeader is null)
        {
            throw new InvalidDataException("Proxy request does not contain a host.");
        }

        var authority = hostHeader[(hostHeader.IndexOf(':') + 1)..].Trim();
        var parsed = ParseAuthority(authority, 80);
        return new ParsedRoute(parsed.Host, parsed.Port, false, requestLine[0], requestLine[1], requestLine[2]);
    }

    private static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                throw new InvalidDataException("Invalid IPv6 proxy authority.");
            }

            var host = authority[1..close];
            var port = close + 1 < authority.Length && authority[close + 1] == ':'
                ? ParsePort(authority[(close + 2)..])
                : defaultPort;
            return (host, port);
        }

        var separator = authority.LastIndexOf(':');
        if (separator > 0 && authority.Count(character => character == ':') == 1)
        {
            return (authority[..separator], ParsePort(authority[(separator + 1)..]));
        }

        return (authority, defaultPort);
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidDataException("Invalid proxy destination port.");
        }

        return port;
    }

    private static byte[] RewriteForwardRequest(byte[] headerBytes, ParsedRoute route)
    {
        var header = Encoding.ASCII.GetString(headerBytes);
        var lineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
        var rewritten = $"{route.Method} {route.RequestTarget} {route.Protocol}{header[lineEnd..]}";
        return Encoding.ASCII.GetBytes(rewritten);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] connections;
        lock (_connectionsGate)
        {
            connections = _connections.ToArray();
        }

        try
        {
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _lifetime.Dispose();
    }

    private sealed record ProxyRequest(byte[] Header, byte[] TrailingData);
    private sealed record ParsedRoute(
        string Host,
        int Port,
        bool IsConnect,
        string Method,
        string RequestTarget,
        string Protocol);
}

internal static class Socks5Connector
{
    public static async Task<TcpClient> ConnectAsync(
        string proxyHost,
        int proxyPort,
        string destinationHost,
        int destinationPort,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxyHost, proxyPort, cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
            {
                throw new IOException("SOCKS5 proxy rejected no-authentication mode.");
            }

            var request = BuildConnectRequest(destinationHost, destinationPort);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var response = new byte[4];
            await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response[0] != 0x05 || response[1] != 0x00)
            {
                throw new IOException($"SOCKS5 proxy rejected destination with code {response[1]}.");
            }

            var addressLength = response[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
                _ => throw new IOException("SOCKS5 proxy returned an invalid address type.")
            };
            var remainder = new byte[addressLength + 2];
            await stream.ReadExactlyAsync(remainder, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static byte[] BuildConnectRequest(string host, int port)
    {
        using var buffer = new MemoryStream();
        buffer.Write([0x05, 0x01, 0x00]);

        if (IPAddress.TryParse(host, out var address))
        {
            buffer.WriteByte(address.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04);
            buffer.Write(address.GetAddressBytes());
        }
        else
        {
            var hostBytes = Encoding.UTF8.GetBytes(host);
            if (hostBytes.Length is 0 or > 255)
            {
                throw new InvalidDataException("SOCKS5 destination host is too long.");
            }

            buffer.WriteByte(0x03);
            buffer.WriteByte((byte)hostBytes.Length);
            buffer.Write(hostBytes);
        }

        buffer.WriteByte((byte)(port >> 8));
        buffer.WriteByte((byte)port);
        return buffer.ToArray();
    }

    private static async Task<int> ReadDomainLengthAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }
}
