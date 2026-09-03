namespace OneClickDpi.Core;

public static class StrategyCatalog
{
    public static IReadOnlyList<StrategyProfile> CreateDefault(EnginePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return
        [
            BuildMultisplit(paths, splitPosition: 1, overlap: 681, "flowseal-split-1", "Balanced Split"),
            BuildFakeSplit(paths),
            BuildMultisplit(paths, splitPosition: 2, overlap: 652, "flowseal-split-2", "Split Position 2"),
            BuildBadSequence(paths)
        ];
    }

    private static StrategyProfile BuildMultisplit(
        EnginePaths paths,
        int splitPosition,
        int overlap,
        string id,
        string displayName)
    {
        var arguments = CommonPrefix();
        AddQuic(arguments, paths, repeats: 6);
        AddDiscordVoice(arguments, paths, repeats: 6);
        AddDiscordMedia(arguments, paths,
            "multisplit",
            $"--dpi-desync-split-seqovl={overlap}",
            $"--dpi-desync-split-pos={splitPosition}",
            $"--dpi-desync-split-seqovl-pattern={paths.Template("tls_clienthello_www_google_com.bin")}");
        AddGoogle(arguments, paths,
            "multisplit",
            $"--dpi-desync-split-seqovl={overlap}",
            $"--dpi-desync-split-pos={splitPosition}",
            $"--dpi-desync-split-seqovl-pattern={paths.Template("tls_clienthello_www_google_com.bin")}");
        AddGeneral(arguments, paths,
            "multisplit",
            $"--dpi-desync-split-seqovl={(splitPosition == 1 ? 568 : overlap)}",
            $"--dpi-desync-split-pos={splitPosition}",
            $"--dpi-desync-split-seqovl-pattern={paths.Template(splitPosition == 1
                ? "tls_clienthello_4pda_to.bin"
                : "tls_clienthello_www_google_com.bin")}");

        return new StrategyProfile(
            id,
            displayName,
            "Flowseal split profile for Discord and YouTube. Telegram traffic is left unchanged.",
            arguments);
    }

    private static StrategyProfile BuildFakeSplit(EnginePaths paths)
    {
        var arguments = CommonPrefix();
        AddQuic(arguments, paths, repeats: 6);
        AddDiscordVoice(arguments, paths, repeats: 6);
        AddDiscordMedia(arguments, paths,
            "fake,fakedsplit",
            "--dpi-desync-repeats=6",
            "--dpi-desync-fooling=ts",
            "--dpi-desync-fakedsplit-pattern=0x00",
            $"--dpi-desync-fake-tls={paths.Template("tls_clienthello_www_google_com.bin")}");
        AddGoogle(arguments, paths,
            "fake,fakedsplit",
            "--dpi-desync-repeats=6",
            "--dpi-desync-fooling=ts",
            "--dpi-desync-fakedsplit-pattern=0x00",
            $"--dpi-desync-fake-tls={paths.Template("tls_clienthello_www_google_com.bin")}");
        AddGeneral(arguments, paths,
            "fake,fakedsplit",
            "--dpi-desync-repeats=6",
            "--dpi-desync-fooling=ts",
            "--dpi-desync-fakedsplit-pattern=0x00",
            $"--dpi-desync-fake-tls={paths.Template("stun.bin")}",
            $"--dpi-desync-fake-tls={paths.Template("tls_clienthello_www_google_com.bin")}",
            $"--dpi-desync-fake-http={paths.Template("tls_clienthello_max_ru.bin")}");

        return new StrategyProfile(
            "flowseal-fakedsplit",
            "Fake Split",
            "Flowseal 1.10.1 ALT-style fake split for Discord and YouTube.",
            arguments);
    }

    private static StrategyProfile BuildBadSequence(EnginePaths paths)
    {
        var arguments = CommonPrefix();
        AddQuic(arguments, paths, repeats: 6);
        AddDiscordVoice(arguments, paths, repeats: 6);
        var mode = new[]
        {
            "--dpi-desync-repeats=6",
            "--dpi-desync-fooling=badseq",
            "--dpi-desync-badseq-increment=1000",
            $"--dpi-desync-fake-tls={paths.Template("tls_clienthello_www_google_com.bin")}"
        };
        AddDiscordMedia(arguments, paths, "fake,multisplit", mode);
        AddGoogle(arguments, paths, "fake,multisplit", mode);
        AddGeneral(arguments, paths,
            "fake,multisplit",
            "--dpi-desync-repeats=6",
            "--dpi-desync-fooling=badseq",
            "--dpi-desync-badseq-increment=1000",
            $"--dpi-desync-fake-tls={paths.Template("stun.bin")}",
            $"--dpi-desync-fake-tls={paths.Template("tls_clienthello_www_google_com.bin")}",
            $"--dpi-desync-fake-http={paths.Template("tls_clienthello_max_ru.bin")}");

        return new StrategyProfile(
            "flowseal-badseq",
            "Bad Sequence",
            "Flowseal 1.10.1 ALT4-style bad sequence profile for Discord and YouTube.",
            arguments);
    }

    private static List<string> CommonPrefix() =>
    [
        "--wf-tcp=80,443,2053,2083,2087,2096,8443",
        "--wf-udp=443,19294-19344,50000-50100"
    ];

    private static void AddQuic(List<string> arguments, EnginePaths paths, int repeats)
    {
        arguments.AddRange(
        [
            "--filter-udp=443",
            $"--hostlist={paths.List("list-general.txt")}",
            $"--hostlist={paths.List("list-google.txt")}",
            $"--hostlist-exclude={paths.List("exclude.txt")}",
            $"--ipset-exclude={paths.List("ipset-exclude.txt")}",
            "--dpi-desync=fake",
            $"--dpi-desync-repeats={repeats}",
            $"--dpi-desync-fake-quic={paths.Template("quic_initial_www_google_com.bin")}",
            "--new"
        ]);
    }

    private static void AddDiscordVoice(List<string> arguments, EnginePaths paths, int repeats)
    {
        arguments.AddRange(
        [
            "--filter-udp=19294-19344,50000-50100",
            "--filter-l7=discord,stun",
            "--dpi-desync=fake",
            $"--dpi-desync-fake-discord={paths.Template("ACTIVE_DISCORD_UDP.bin")}",
            $"--dpi-desync-fake-stun={paths.Template("ACTIVE_DISCORD_UDP.bin")}",
            $"--dpi-desync-repeats={repeats}",
            "--new"
        ]);
    }

    private static void AddDiscordMedia(
        List<string> arguments,
        EnginePaths paths,
        string desyncMode,
        params string[] modeArguments)
    {
        arguments.Add("--filter-tcp=2053,2083,2087,2096,8443");
        arguments.Add("--hostlist-domains=discord.media");
        arguments.Add($"--dpi-desync={desyncMode}");
        arguments.AddRange(modeArguments);
        arguments.Add("--new");
    }

    private static void AddGoogle(
        List<string> arguments,
        EnginePaths paths,
        string desyncMode,
        params string[] modeArguments)
    {
        arguments.Add("--filter-tcp=443");
        arguments.Add($"--hostlist={paths.List("list-google.txt")}");
        arguments.Add("--ip-id=zero");
        arguments.Add($"--dpi-desync={desyncMode}");
        arguments.AddRange(modeArguments);
        arguments.Add("--new");
    }

    private static void AddGeneral(
        List<string> arguments,
        EnginePaths paths,
        string desyncMode,
        params string[] modeArguments)
    {
        arguments.Add("--filter-tcp=80,443");
        arguments.Add($"--hostlist={paths.List("list-general.txt")}");
        arguments.Add($"--hostlist-exclude={paths.List("exclude.txt")}");
        arguments.Add($"--ipset-exclude={paths.List("ipset-exclude.txt")}");
        arguments.Add($"--dpi-desync={desyncMode}");
        arguments.AddRange(modeArguments);
    }
}
