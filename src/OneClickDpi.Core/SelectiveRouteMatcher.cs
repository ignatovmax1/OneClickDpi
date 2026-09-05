using System.Net;
using System.Net.Sockets;

namespace OneClickDpi.Core;

public enum SelectiveRoute
{
    Direct,
    Tor,
    Ai
}

public sealed class SelectiveRouteMatcher
{
    private static readonly string[] TorDomainSuffixes =
    [
        "youtube.com",
        "youtu.be",
        "youtube-nocookie.com",
        "youtubeeducation.com",
        "youtube.googleapis.com",
        "youtubei.googleapis.com",
        "telegram.org",
        "telegram.me",
        "telegram.dog",
        "t.me",
        "telegra.ph",
        "telesco.pe",
        "tdesktop.com",
        "discord.com",
        "discord.gg",
        "discordapp.com",
        "discordapp.net",
        "discordcdn.com",
        "discord.media"
    ];

    private static readonly string[] AiDomainSuffixes =
    [
        "chatgpt.com",
        "openai.com",
        "oaistatic.com",
        "oaiusercontent.com",
        "openaimerge.com",
        "featuregates.org",
        "featureassets.org",
        "statsig.com",
        "statsigapi.net",
        "cdn.workos.com",
        "forwarder.workos.com",
        "setup.workos.com",
        "workoscdn.com",
        "workos.imgix.net",
        "challenges.cloudflare.com",
        "prodregistryv2.org",
        "claude.ai",
        "claudeusercontent.com",
        "anthropic.com",
        "whatsapp.com",
        "whatsapp.net",
        "api.whatsapp.com",
        "web.whatsapp.com",
        "static.whatsapp.net",
        "media.whatsapp.com",
        "mmg.whatsapp.net",
        "g.whatsapp.net"
    ];

    private static readonly IpNetwork[] TelegramNetworks =
    [
        IpNetwork.Parse("91.108.4.0/22"),
        IpNetwork.Parse("91.108.8.0/22"),
        IpNetwork.Parse("91.108.12.0/22"),
        IpNetwork.Parse("91.108.16.0/22"),
        IpNetwork.Parse("91.108.20.0/22"),
        IpNetwork.Parse("91.108.56.0/22"),
        IpNetwork.Parse("95.161.64.0/20"),
        IpNetwork.Parse("149.154.160.0/20"),
        IpNetwork.Parse("2001:67c:4e8::/48"),
        IpNetwork.Parse("2001:b28:f23c::/48"),
        IpNetwork.Parse("2001:b28:f23d::/48"),
        IpNetwork.Parse("2001:b28:f23f::/48")
    ];

    public SelectiveRoute GetRoute(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return SelectiveRoute.Direct;
        }

        var normalized = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (IPAddress.TryParse(normalized, out var address))
        {
            return TelegramNetworks.Any(network => network.Contains(address))
                ? SelectiveRoute.Tor
                : SelectiveRoute.Direct;
        }

        if (MatchesAnySuffix(normalized, AiDomainSuffixes))
        {
            return SelectiveRoute.Ai;
        }

        return MatchesAnySuffix(normalized, TorDomainSuffixes)
            ? SelectiveRoute.Tor
            : SelectiveRoute.Direct;
    }

    public bool ShouldTunnel(string host) => GetRoute(host) != SelectiveRoute.Direct;

    public bool ShouldUseAiTunnel(string host) => GetRoute(host) == SelectiveRoute.Ai;

    private static bool MatchesAnySuffix(string host, IEnumerable<string> suffixes) =>
        suffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.Ordinal)
            || host.EndsWith('.' + suffix, StringComparison.Ordinal));

    private sealed record IpNetwork(IPAddress Network, int PrefixLength)
    {
        public static IpNetwork Parse(string value)
        {
            var parts = value.Split('/', 2);
            var network = IPAddress.Parse(parts[0]);
            var prefixLength = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var maximum = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength is < 0 || prefixLength > maximum)
            {
                throw new FormatException($"Invalid prefix length in {value}.");
            }

            return new IpNetwork(network, prefixLength);
        }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != Network.AddressFamily)
            {
                return false;
            }

            var networkBytes = Network.GetAddressBytes();
            var addressBytes = address.GetAddressBytes();
            var completeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;

            for (var index = 0; index < completeBytes; index++)
            {
                if (networkBytes[index] != addressBytes[index])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (networkBytes[completeBytes] & mask) == (addressBytes[completeBytes] & mask);
        }
    }
}
