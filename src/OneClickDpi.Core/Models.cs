namespace OneClickDpi.Core;

public enum ServiceKind
{
    Discord,
    YouTube,
    Telegram,
    ChatGPT,
    Claude,
    WhatsApp
}

public sealed record ProbeEndpoint(
    ServiceKind Service,
    string Name,
    Uri Uri,
    TimeSpan Timeout,
    bool IsRequired = false);

public sealed record EndpointProbeResult(
    ProbeEndpoint Endpoint,
    bool IsSuccess,
    TimeSpan Latency,
    int? StatusCode,
    string? Error);

public sealed record ServiceProbeResult(
    ServiceKind Service,
    IReadOnlyList<EndpointProbeResult> Endpoints,
    int RequiredSuccesses)
{
    public int SuccessCount => Endpoints.Count(result => result.IsSuccess);
    public bool IsHealthy => SuccessCount >= RequiredSuccesses
        && Endpoints.Where(result => result.Endpoint.IsRequired).All(result => result.IsSuccess);
    public TimeSpan AverageLatency => SuccessCount == 0
        ? TimeSpan.MaxValue
        : TimeSpan.FromMilliseconds(
            Endpoints.Where(result => result.IsSuccess).Average(result => result.Latency.TotalMilliseconds));
}

public sealed record ConnectivitySnapshot(IReadOnlyList<ServiceProbeResult> Services)
{
    public bool AllHealthy => Services.Count > 0 && Services.All(service => service.IsHealthy);
    public bool DpiTargetsHealthy => IsServiceHealthy(ServiceKind.Discord)
        && IsServiceHealthy(ServiceKind.YouTube);
    public int HealthyServiceCount => Services.Count(service => service.IsHealthy);
    public int SuccessfulEndpointCount => Services.Sum(service => service.SuccessCount);

    public double Score
    {
        get
        {
            var successful = Services.SelectMany(service => service.Endpoints)
                .Where(endpoint => endpoint.IsSuccess)
                .ToArray();
            var latencyPenalty = successful.Length == 0
                ? 10_000
                : successful.Average(endpoint => endpoint.Latency.TotalMilliseconds);

            return (HealthyServiceCount * 10_000) + (SuccessfulEndpointCount * 1_000) - latencyPenalty;
        }
    }

    private bool IsServiceHealthy(ServiceKind service) =>
        Services.FirstOrDefault(result => result.Service == service)?.IsHealthy == true;
}

public sealed record StrategyProfile(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Arguments);

public enum AutoConnectStage
{
    Validating,
    StartingCandidate,
    Probing,
    Selecting,
    Connected,
    Stopping
}

public sealed record AutoConnectProgress(
    AutoConnectStage Stage,
    string Message,
    int CandidateIndex = 0,
    int CandidateCount = 0);

public sealed record AutoConnectResult(
    StrategyProfile Strategy,
    ConnectivitySnapshot Connectivity,
    string NetworkFingerprint);
