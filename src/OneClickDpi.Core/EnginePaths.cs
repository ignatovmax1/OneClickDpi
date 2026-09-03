namespace OneClickDpi.Core;

public sealed class EnginePaths
{
    public EnginePaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        Executable = Resolve("winws.exe");
        WinDivertLibrary = Resolve("WinDivert.dll");
        WinDivertDriver = Resolve("WinDivert64.sys");
        CygwinRuntime = Resolve("cygwin1.dll");
        ListsDirectory = ResolveDirectory("Lists");
        PacketTemplatesDirectory = ResolveDirectory("Templates");
    }

    public string RootDirectory { get; }
    public string Executable { get; }
    public string WinDivertLibrary { get; }
    public string WinDivertDriver { get; }
    public string CygwinRuntime { get; }
    public string ListsDirectory { get; }
    public string PacketTemplatesDirectory { get; }

    public string List(string fileName) => ResolveChild(ListsDirectory, fileName);
    public string Template(string fileName) => ResolveChild(PacketTemplatesDirectory, fileName);

    private string Resolve(string fileName) => ResolveChild(RootDirectory, fileName);

    private string ResolveDirectory(string directoryName)
    {
        var resolved = ResolveChild(RootDirectory, directoryName);
        return resolved;
    }

    private static string ResolveChild(string parent, string child)
    {
        if (Path.GetFileName(child) != child)
        {
            throw new ArgumentException("Only a direct child name is allowed.", nameof(child));
        }

        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(fullParent, child));
        if (!resolved.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved engine path escaped its trusted directory.");
        }

        return resolved;
    }
}
