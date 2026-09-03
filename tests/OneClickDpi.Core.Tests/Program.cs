using OneClickDpi.Core;

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    using var liveProbe = new HttpConnectivityProbe();
    var liveSnapshot = await liveProbe.ProbeAsync(CancellationToken.None);
    foreach (var service in liveSnapshot.Services)
    {
        Console.WriteLine($"{service.Service}: healthy={service.IsHealthy}");
        foreach (var endpoint in service.Endpoints)
        {
            Console.WriteLine(
                $"  {endpoint.Endpoint.Name}: success={endpoint.IsSuccess} " +
                $"latency={endpoint.Latency.TotalMilliseconds:0}ms status={endpoint.StatusCode} error={endpoint.Error}");
        }
    }

    return liveSnapshot.AllHealthy ? 0 : 2;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("cached strategy is attempted first", CachedStrategyIsAttemptedFirst),
    ("best partial strategy is selected", BestPartialStrategyIsSelected),
    ("failed engines are skipped", FailedEnginesAreSkipped),
    ("json cache survives round trip", JsonCacheSurvivesRoundTrip),
    ("required protocol probe cannot be masked", RequiredProtocolProbeCannotBeMasked),
    ("stopped engine cannot win selection", StoppedEngineCannotWinSelection),
    ("catalog leaves Telegram transports untouched", CatalogLeavesTelegramUntouched),
    ("DPI target health ignores pass-through Telegram", DpiTargetHealthIgnoresTelegram),
    ("selective tunnel matches only supported targets", SelectiveTunnelMatchesOnlyTargets),
    ("ChatGPT VPN block is rejected", ChatGptVpnBlockIsRejected),
    ("Claude regional block is rejected", ClaudeRegionalBlockIsRejected),
    ("ordinary Cloudflare challenge remains browser-solvable", OrdinaryCloudflareChallengeIsAccepted)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static async Task CachedStrategyIsAttemptedFirst()
{
    var strategies = Strategies();
    var engine = new FakeEngine();
    var cache = new MemoryCache("second");
    var probe = new QueueProbe(Healthy());
    var coordinator = new AutoConnectCoordinator(strategies, engine, probe, cache, new FixedNetwork());

    var result = await coordinator.ConnectAsync(null, CancellationToken.None);

    Equal("second", result.Strategy.Id);
    Equal("second", engine.Started.Single());
}

static async Task BestPartialStrategyIsSelected()
{
    var strategies = Strategies();
    var engine = new FakeEngine();
    var cache = new MemoryCache(null);
    var probe = new QueueProbe(
        Snapshot(discord: true, youtube: false, telegram: false),
        Snapshot(discord: true, youtube: true, telegram: false),
        Healthy());
    var coordinator = new AutoConnectCoordinator(strategies, engine, probe, cache, new FixedNetwork());

    var result = await coordinator.ConnectAsync(null, CancellationToken.None);

    Equal("second", result.Strategy.Id);
    True(result.Connectivity.DpiTargetsHealthy, "The working Discord/YouTube strategy should win.");
    True(!result.Connectivity.AllHealthy, "Unavailable pass-through Telegram must still be reported.");
}

static async Task FailedEnginesAreSkipped()
{
    var strategies = Strategies();
    var engine = new FakeEngine("first");
    var probe = new QueueProbe(Healthy());
    var coordinator = new AutoConnectCoordinator(
        strategies,
        engine,
        probe,
        new MemoryCache(null),
        new FixedNetwork());

    var result = await coordinator.ConnectAsync(null, CancellationToken.None);

    Equal("second", result.Strategy.Id);
    Equal(2, engine.Started.Count);
}

static async Task JsonCacheSurvivesRoundTrip()
{
    var directory = Path.Combine(Path.GetTempPath(), "OneClickDpi.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var cache = new JsonStrategyCache(Path.Combine(directory, "cache.json"));
        await cache.SetAsync("network", "strategy", CancellationToken.None);
        var value = await cache.GetAsync("network", CancellationToken.None);
        Equal("strategy", value);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static Task RequiredProtocolProbeCannotBeMasked()
{
    var optionalEndpoint = new ProbeEndpoint(
        ServiceKind.Discord,
        "Optional",
        new Uri("https://example.test"),
        TimeSpan.FromSeconds(1));
    var requiredEndpoint = new ProbeEndpoint(
        ServiceKind.Discord,
        "Gateway",
        new Uri("wss://example.test"),
        TimeSpan.FromSeconds(1),
        IsRequired: true);
    var result = new ServiceProbeResult(
        ServiceKind.Discord,
        new[]
        {
            new EndpointProbeResult(optionalEndpoint, true, TimeSpan.FromMilliseconds(1), 200, null),
            new EndpointProbeResult(requiredEndpoint, false, TimeSpan.FromMilliseconds(1), null, "blocked")
        },
        RequiredSuccesses: 1);

    True(!result.IsHealthy, "A failed required protocol probe must keep the service unhealthy.");
    return Task.CompletedTask;
}

static async Task StoppedEngineCannotWinSelection()
{
    var engine = new FakeEngine();
    var probe = new CallbackProbe(() =>
    {
        engine.ForceExit();
        return Healthy();
    });
    var coordinator = new AutoConnectCoordinator(
        Strategies(),
        engine,
        probe,
        new MemoryCache(null),
        new FixedNetwork());

    try
    {
        await coordinator.ConnectAsync(null, CancellationToken.None);
        throw new InvalidOperationException("A stopped engine was incorrectly accepted.");
    }
    catch (AggregateException)
    {
    }
}

static Task CatalogLeavesTelegramUntouched()
{
    var paths = new EnginePaths(Path.Combine(Path.GetTempPath(), "OneClickDpi.Engine.Tests"));
    var catalog = StrategyCatalog.CreateDefault(paths);
    Equal(4, catalog.Count);
    foreach (var strategy in catalog)
    {
        True(!strategy.Arguments.Any(argument => argument.Contains("590-1400", StringComparison.Ordinal)),
            "Telegram call traffic must not be intercepted.");
        True(!strategy.Arguments.Any(argument => argument.Contains("5222", StringComparison.Ordinal)
            || argument.Contains("8888", StringComparison.Ordinal)),
            "Telegram MTProto-specific ports must not be intercepted.");
        True(!strategy.Arguments.Any(argument => argument.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)),
            "A strategy must never execute a BAT file.");
    }

    return Task.CompletedTask;
}

static Task DpiTargetHealthIgnoresTelegram()
{
    var snapshot = Snapshot(discord: true, youtube: true, telegram: false);
    True(snapshot.DpiTargetsHealthy, "Pass-through Telegram must not trigger more DPI strategy cycling.");
    True(!snapshot.AllHealthy, "The UI must still report that Telegram itself is unavailable.");
    return Task.CompletedTask;
}

static Task SelectiveTunnelMatchesOnlyTargets()
{
    var matcher = new SelectiveRouteMatcher();
    True(matcher.ShouldTunnel("www.youtube.com"), "YouTube must use the tunnel.");
    True(!matcher.ShouldTunnel("rr2---sn.googlevideo.com"), "YouTube video media must stay on the fast direct DPI path.");
    True(!matcher.ShouldTunnel("i.ytimg.com"), "YouTube static assets must stay on the fast direct DPI path.");
    True(!matcher.ShouldTunnel("yt3.ggpht.com"), "YouTube thumbnails must stay on the fast direct DPI path.");
    True(matcher.ShouldTunnel("web.telegram.org"), "Telegram Web must use the tunnel.");
    True(matcher.ShouldTunnel("149.154.167.50"), "Telegram DCs must use the tunnel.");
    True(matcher.ShouldTunnel("2001:b28:f23d::a"), "Telegram IPv6 DCs must use the tunnel.");
    True(matcher.ShouldTunnel("chatgpt.com"), "ChatGPT must use the tunnel.");
    Equal(SelectiveRoute.Ai, matcher.GetRoute("chatgpt.com"));
    True(matcher.ShouldTunnel("desktop.chatgpt.com"), "ChatGPT Desktop must use the tunnel.");
    True(matcher.ShouldTunnel("files.oaiusercontent.com"), "ChatGPT files must use the tunnel.");
    True(matcher.ShouldTunnel("auth.openai.com"), "OpenAI authentication must use the tunnel.");
    True(matcher.ShouldTunnel("claude.ai"), "Claude must use the tunnel.");
    Equal(SelectiveRoute.Ai, matcher.GetRoute("claude.ai"));
    True(matcher.ShouldTunnel("files.claudeusercontent.com"), "Claude files must use the tunnel.");
    True(matcher.ShouldTunnel("api.anthropic.com"), "Anthropic services must use the tunnel.");
    True(matcher.ShouldTunnel("discord.com"), "Discord must use the AI tunnel when direct Gateway access fails.");
    Equal(SelectiveRoute.Ai, matcher.GetRoute("gateway.discord.gg"));
    True(matcher.ShouldTunnel("cdn.discordapp.com"), "Discord CDN must follow the Discord tunnel route.");
    Equal(SelectiveRoute.Tor, matcher.GetRoute("web.telegram.org"));
    Equal(SelectiveRoute.Ai, matcher.GetRoute("discord.com"));
    True(!matcher.ShouldTunnel("googlevideo.com"), "YouTube media must remain direct.");
    True(!matcher.ShouldTunnel("example.com"), "Unrelated traffic must remain direct.");
    True(!matcher.ShouldTunnel("notyoutube.com"), "Suffix matching must respect label boundaries.");
    True(!matcher.ShouldTunnel("notopenai.com"), "OpenAI suffix matching must respect label boundaries.");
    True(!matcher.ShouldTunnel("notclaude.ai.example.com"), "Claude lookalikes must remain direct.");
    return Task.CompletedTask;
}

static Task ChatGptVpnBlockIsRejected()
{
    var verdict = AiServiceResponseClassifier.Evaluate(
        ServiceKind.ChatGPT,
        403,
        isCloudflareChallenge: true,
        "Unable to load site. If you are using a VPN, try turning it off.");
    True(!verdict.IsUsable, "A ChatGPT VPN rejection page must never be reported as healthy.");
    return Task.CompletedTask;
}

static Task ClaudeRegionalBlockIsRejected()
{
    var verdict = AiServiceResponseClassifier.Evaluate(
        ServiceKind.Claude,
        200,
        isCloudflareChallenge: false,
        "App unavailable. Claude is only available in certain regions right now.");
    True(!verdict.IsUsable, "A Claude regional rejection page must never be reported as healthy.");
    return Task.CompletedTask;
}

static Task OrdinaryCloudflareChallengeIsAccepted()
{
    var verdict = AiServiceResponseClassifier.Evaluate(
        ServiceKind.ChatGPT,
        403,
        isCloudflareChallenge: true,
        "Just a moment...");
    True(verdict.IsUsable, "A normal browser-solvable Cloudflare challenge must remain usable.");
    return Task.CompletedTask;
}

static IReadOnlyList<StrategyProfile> Strategies() =>
[
    new("first", "First", "", Array.Empty<string>()),
    new("second", "Second", "", Array.Empty<string>()),
    new("third", "Third", "", Array.Empty<string>())
];

static ConnectivitySnapshot Healthy() => Snapshot(true, true, true);

static ConnectivitySnapshot Snapshot(bool discord, bool youtube, bool telegram) =>
    new(new[]
    {
        Result(ServiceKind.Discord, discord),
        Result(ServiceKind.YouTube, youtube),
        Result(ServiceKind.Telegram, telegram)
    });

static ServiceProbeResult Result(ServiceKind service, bool success)
{
    var endpoint = new ProbeEndpoint(service, service.ToString(), new Uri("https://example.test"), TimeSpan.FromSeconds(1));
    var result = new EndpointProbeResult(endpoint, success, TimeSpan.FromMilliseconds(20), success ? 200 : null, null);
    return new ServiceProbeResult(service, new[] { result }, 1);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class FakeEngine(params string[] failingStrategyIds) : IDpiEngine
{
    private readonly HashSet<string> _failingStrategyIds = new(failingStrategyIds, StringComparer.Ordinal);
    private event EventHandler? UnexpectedExitHandlers;
    public List<string> Started { get; } = [];
    public bool IsRunning { get; private set; }
    public string? ActiveStrategyId { get; private set; }
    public event EventHandler<string>? LogReceived;
    public event EventHandler? UnexpectedlyExited
    {
        add => UnexpectedExitHandlers += value;
        remove => UnexpectedExitHandlers -= value;
    }

    public Task StartAsync(StrategyProfile strategy, CancellationToken cancellationToken)
    {
        Started.Add(strategy.Id);
        if (_failingStrategyIds.Contains(strategy.Id))
        {
            throw new InvalidOperationException("simulated engine failure");
        }

        IsRunning = true;
        ActiveStrategyId = strategy.Id;
        LogReceived?.Invoke(this, strategy.Id);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        ActiveStrategyId = null;
        return Task.CompletedTask;
    }

    public void ForceExit()
    {
        IsRunning = false;
        ActiveStrategyId = null;
        UnexpectedExitHandlers?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class QueueProbe(params ConnectivitySnapshot[] snapshots) : IConnectivityProbe
{
    private readonly Queue<ConnectivitySnapshot> _snapshots = new(snapshots);

    public Task<ConnectivitySnapshot> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.Dequeue());
}

file sealed class CallbackProbe(Func<ConnectivitySnapshot> callback) : IConnectivityProbe
{
    public Task<ConnectivitySnapshot> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(callback());
}

file sealed class MemoryCache(string? initial) : IStrategyCache
{
    private string? _value = initial;

    public Task<string?> GetAsync(string networkFingerprint, CancellationToken cancellationToken) =>
        Task.FromResult(_value);

    public Task SetAsync(string networkFingerprint, string strategyId, CancellationToken cancellationToken)
    {
        _value = strategyId;
        return Task.CompletedTask;
    }
}

file sealed class FixedNetwork : INetworkFingerprintProvider
{
    public string GetFingerprint() => "network";
}
