namespace OneClickDpi.Core;

public readonly record struct AiServiceResponseVerdict(bool IsUsable, string? Error);

public static class AiServiceResponseClassifier
{
    private static readonly string[] ChatGptBlockMarkers =
    [
        "Unable to load site",
        "If you are using a VPN",
        "try turning it off",
        "vpn_blocked",
        "unsupported_country"
    ];

    private static readonly string[] ClaudeBlockMarkers =
    [
        "App unavailable",
        "only available in certain regions",
        "unsupported_country",
        "unsupported region"
    ];

    public static AiServiceResponseVerdict Evaluate(
        ServiceKind service,
        int statusCode,
        bool isCloudflareChallenge,
        string? body)
    {
        if (service is not (ServiceKind.ChatGPT or ServiceKind.Claude))
        {
            throw new ArgumentOutOfRangeException(nameof(service), service, "Expected an AI service.");
        }

        var markers = service == ServiceKind.ChatGPT
            ? ChatGptBlockMarkers
            : ClaudeBlockMarkers;
        if (!string.IsNullOrEmpty(body)
            && markers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return new AiServiceResponseVerdict(
                false,
                service == ServiceKind.ChatGPT
                    ? "Tunnel IP is blocked by ChatGPT"
                    : "Tunnel region is not supported by Claude");
        }

        if (statusCode == 451)
        {
            return new AiServiceResponseVerdict(false, "Service is unavailable in the tunnel region (HTTP 451)");
        }

        if (statusCode is >= 200 and < 400)
        {
            return new AiServiceResponseVerdict(true, null);
        }

        if (statusCode == 403 && isCloudflareChallenge)
        {
            return new AiServiceResponseVerdict(true, null);
        }

        return new AiServiceResponseVerdict(false, $"HTTP {statusCode}");
    }
}
