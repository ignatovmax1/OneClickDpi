using OneClickDpi.App;

try
{
    return await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL Tunnel test: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments.Length == 3
        && arguments[0].Equals("--ai-only", StringComparison.Ordinal))
    {
        using var aiTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var aiTunnel = new PsiphonTunnelEngine(
            arguments[1],
            arguments[2],
            Path.Combine(arguments[2], "smoke-psiphon.pid"));
        aiTunnel.LogReceived += message => Console.WriteLine("PSIPHON " + message);
        await aiTunnel.StartAsync(aiTimeout.Token);
        Console.WriteLine("RESULT AI route passed the live ChatGPT and Claude checks.");
        return 0;
    }

    if (arguments.Length is < 2 or > 4
        || arguments.Length == 3 && !arguments[2].Equals("--settings-test", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: TunnelSmoke <tor-assets-directory> <tor-data-directory> " +
            "[--settings-test | <psiphon-assets-directory> <psiphon-data-directory>]\n" +
            "       TunnelSmoke --ai-only <psiphon-assets-directory> <psiphon-data-directory>");
        return 64;
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    await using var tor = new TorTunnelEngine(arguments[0], arguments[1]);
    tor.LogReceived += message => Console.WriteLine("TOR " + message);
    PsiphonTunnelEngine? psiphon = null;
    if (arguments.Length == 4)
    {
        psiphon = new PsiphonTunnelEngine(
            arguments[2],
            arguments[3],
            Path.Combine(arguments[3], "smoke-psiphon.pid"));
        psiphon.LogReceived += message => Console.WriteLine("PSIPHON " + message);
    }

    var proxy = new SelectiveHttpProxy(
        new OneClickDpi.Core.SelectiveRouteMatcher(),
        socksPort: tor.SocksPort,
        aiSocksPort: psiphon?.SocksPort ?? tor.SocksPort);
    proxy.LogReceived += message => Console.WriteLine("PROXY " + message);

    try
    {
        await tor.StartAsync(timeout.Token);
        if (psiphon is not null)
        {
            await psiphon.StartAsync(timeout.Token);
        }

        await proxy.StartAsync(timeout.Token);
        using var probe = new TunnelConnectivityProbe(proxy.Port);
        var snapshot = await probe.ProbeAsync(timeout.Token);
        foreach (var service in snapshot.Services)
        {
            Console.WriteLine($"RESULT {service.Service} healthy={service.IsHealthy}");
            foreach (var endpoint in service.Endpoints)
            {
                Console.WriteLine(
                    $"  {endpoint.Endpoint.Name}: success={endpoint.IsSuccess} " +
                    $"latency={endpoint.Latency.TotalMilliseconds:0}ms status={endpoint.StatusCode} error={endpoint.Error}");
            }
        }

        if (arguments.Length == 3)
        {
            var backupPath = Path.Combine(arguments[1], "smoke-proxy-backup.json");
            using var settings = new WindowsProxyController(backupPath);
            settings.LogReceived += message => Console.WriteLine("SETTINGS " + message);
            settings.Enable(proxy.Port);
            if (!settings.IsEnabled)
            {
                throw new InvalidOperationException("Windows proxy controller did not enter the enabled state.");
            }

            settings.Disable();
            if (settings.IsEnabled || File.Exists(backupPath))
            {
                throw new InvalidOperationException("Windows proxy controller did not restore its backup.");
            }

            Console.WriteLine("SETTINGS round-trip passed");
        }

        return snapshot.HealthyServiceCount > 0 ? 0 : 2;
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        Console.Error.WriteLine("FAIL Tunnel test timed out before Tor became ready.");
        return 3;
    }
    finally
    {
        await proxy.DisposeAsync();
        if (psiphon is not null)
        {
            await psiphon.DisposeAsync();
        }
    }
}
