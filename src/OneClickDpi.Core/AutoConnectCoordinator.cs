namespace OneClickDpi.Core;

public sealed class AutoConnectCoordinator
{
    private readonly IReadOnlyList<StrategyProfile> _strategies;
    private readonly IDpiEngine _engine;
    private readonly IConnectivityProbe _probe;
    private readonly IStrategyCache _cache;
    private readonly INetworkFingerprintProvider _networkFingerprintProvider;

    public AutoConnectCoordinator(
        IReadOnlyList<StrategyProfile> strategies,
        IDpiEngine engine,
        IConnectivityProbe probe,
        IStrategyCache cache,
        INetworkFingerprintProvider networkFingerprintProvider)
    {
        _strategies = strategies is { Count: > 0 }
            ? strategies
            : throw new ArgumentException("At least one strategy is required.", nameof(strategies));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _networkFingerprintProvider = networkFingerprintProvider
            ?? throw new ArgumentNullException(nameof(networkFingerprintProvider));
    }

    public async Task<AutoConnectResult> ConnectAsync(
        IProgress<AutoConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        var networkFingerprint = _networkFingerprintProvider.GetFingerprint();
        var cachedStrategyId = await _cache.GetAsync(networkFingerprint, cancellationToken).ConfigureAwait(false);
        var candidates = OrderCandidates(cachedStrategyId);
        var successfulCandidates = new List<CandidateResult>();
        var failures = new List<Exception>();

        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[index];
                progress?.Report(new AutoConnectProgress(
                    AutoConnectStage.StartingCandidate,
                    $"Проверяем стратегию {candidate.DisplayName}",
                    index + 1,
                    candidates.Count));

                try
                {
                    await _engine.StartAsync(candidate, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);

                    progress?.Report(new AutoConnectProgress(
                        AutoConnectStage.Probing,
                        "Проверяем Discord, YouTube и Telegram",
                        index + 1,
                        candidates.Count));
                    var snapshot = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
                    if (!_engine.IsRunning)
                    {
                        throw new InvalidOperationException(
                            $"The DPI engine stopped while testing strategy {candidate.DisplayName}.");
                    }

                    successfulCandidates.Add(new CandidateResult(candidate, snapshot));

                    // Telegram is deliberately pass-through: current throttling is IP-based and
                    // cannot be improved by cycling local DPI profiles. Stop once the services
                    // actually targeted by this engine are healthy.
                    if (snapshot.DpiTargetsHealthy)
                    {
                        break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add(exception);
                }
            }

            if (successfulCandidates.Count == 0)
            {
                throw new AggregateException("No strategy could start and complete connectivity checks.", failures);
            }

            progress?.Report(new AutoConnectProgress(AutoConnectStage.Selecting, "Выбираем лучший результат"));
            var winner = successfulCandidates
                .OrderByDescending(candidate => candidate.Snapshot.Score)
                .ThenBy(candidate => CandidateOrder(candidate.Profile.Id, candidates))
                .First();

            if (!string.Equals(_engine.ActiveStrategyId, winner.Profile.Id, StringComparison.Ordinal))
            {
                await _engine.StartAsync(winner.Profile, cancellationToken).ConfigureAwait(false);
            }

            await _cache.SetAsync(networkFingerprint, winner.Profile.Id, cancellationToken).ConfigureAwait(false);
            progress?.Report(new AutoConnectProgress(
                AutoConnectStage.Connected,
                winner.Snapshot.AllHealthy ? "Подключено" : "Подключено частично"));

            return new AutoConnectResult(winner.Profile, winner.Snapshot, networkFingerprint);
        }
        catch
        {
            await _engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(
        IProgress<AutoConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AutoConnectProgress(AutoConnectStage.Stopping, "Отключаем защиту"));
        await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<StrategyProfile> OrderCandidates(string? cachedStrategyId)
    {
        if (string.IsNullOrWhiteSpace(cachedStrategyId))
        {
            return _strategies;
        }

        return _strategies
            .OrderBy(strategy => string.Equals(strategy.Id, cachedStrategyId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(strategy => CandidateOrder(strategy.Id, _strategies))
            .ToArray();
    }

    private static int CandidateOrder(string strategyId, IReadOnlyList<StrategyProfile> candidates)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (string.Equals(candidates[index].Id, strategyId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private sealed record CandidateResult(StrategyProfile Profile, ConnectivitySnapshot Snapshot);
}
