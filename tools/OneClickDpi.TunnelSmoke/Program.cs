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
    if (arguments is ["--vps-only"])
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var vps = new VpsSshTunnelEngine(19183);
        vps.LogReceived += Console.WriteLine;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await vps.StartAsync(deadline.Token);
            using var client = new HttpClient(new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy("socks5://127.0.0.1:19183"), UseProxy = true
            });
            var exitIp = (await client.GetStringAsync("https://api.ipify.org", deadline.Token)).Trim();
            if (exitIp != "185.173.144.43") throw new InvalidOperationException("Unexpected VPS exit IP: " + exitIp);
            Console.WriteLine("PASS VPS exit IP " + exitIp);
            if (attempt == 0)
            {
                await using var vpsProxy = new SelectiveHttpProxy(new OneClickDpi.Core.SelectiveRouteMatcher(), aiSocksPort: vps.SocksPort, listenPort: 19181);
                vpsProxy.LogReceived += Console.WriteLine;
                await vpsProxy.StartAsync(deadline.Token);
                using var http = new HttpClient(new HttpClientHandler { Proxy = new System.Net.WebProxy("http://127.0.0.1:19181"), UseProxy = true });
                foreach (var host in new[] { "chatgpt.com", "claude.ai", "web.whatsapp.com" })
                {
                    using var response = await http.GetAsync("https://" + host, deadline.Token);
                    Console.WriteLine($"VPS destination {host}: HTTP {(int)response.StatusCode}");
                    if (response.StatusCode == System.Net.HttpStatusCode.BadGateway) throw new IOException("Proxy connection failed: " + host);
                }
            }
            await vps.StopAsync(deadline.Token);
            if (vps.IsRunning) throw new InvalidOperationException("VPS failed to stop");
        }
        Console.WriteLine("PASS VPS start/stop/restart and three selective routes");
        return 0;
    }

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
