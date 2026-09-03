using System.Reflection;
using System.Security.Cryptography;
using System.IO;

namespace OneClickDpi.App;

internal sealed record RuntimeLayout(string RootDirectory)
{
    public string EngineDirectory => Path.Combine(RootDirectory, "Engine");
    public string TorDirectory => Path.Combine(RootDirectory, "Tunnel", "Tor");
    public string PsiphonDirectory => Path.Combine(RootDirectory, "Tunnel", "Psiphon");
}

internal static class RuntimeAssetExtractor
{
    private const string ResourcePrefix = "OneClickDpi.RuntimeAsset/";

    public static RuntimeLayout EnsureExtracted(string localDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);

        var assembly = typeof(RuntimeAssetExtractor).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var rootDirectory = Path.GetFullPath(Path.Combine(localDataDirectory, "Runtime", version));
        Directory.CreateDirectory(rootDirectory);

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException("В программу не встроены компоненты подключения.");
        }

        foreach (var resourceName in resourceNames)
        {
            var relativeName = resourceName[ResourcePrefix.Length..].Replace('\\', '/');
            var segments = relativeName.Split('/', StringSplitOptions.None);
            if (segments.Length < 2
                || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            {
                throw new InvalidDataException($"Недопустимое имя встроенного компонента: {resourceName}");
            }

            var destination = ResolveChildPath(rootDirectory, segments);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureFileMatchesResource(assembly, resourceName, destination);
        }

        return new RuntimeLayout(rootDirectory);
    }

    private static void EnsureFileMatchesResource(
        Assembly assembly,
        string resourceName,
        string destination)
    {
        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Не удалось прочитать встроенный компонент: {resourceName}");

        if (File.Exists(destination) && FilesMatch(source, destination))
        {
            return;
        }

        source.Position = 0;
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.WriteThrough))
            {
                source.CopyTo(output);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool FilesMatch(Stream source, string destination)
    {
        var existing = new FileInfo(destination);
        if (existing.Length != source.Length)
        {
            return false;
        }

        var sourceHash = SHA256.HashData(source);
        using var destinationStream = File.OpenRead(destination);
        var destinationHash = SHA256.HashData(destinationStream);
        return CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
    }

    private static string ResolveChildPath(string rootDirectory, IReadOnlyList<string> segments)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(segments.Aggregate(root, Path.Combine));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь встроенного компонента вышел за пределы служебной папки.");
        }

        return destination;
    }
}
