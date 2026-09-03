using System.IO;
using System.Text;

namespace OneClickDpi.App;

public sealed class LocalLogWriter : IDisposable
{
    private readonly object _sync = new();

    public LocalLogWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, $"oneclickdpi-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public string LogPath { get; }

    public void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:O} {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
        try
        {
            lock (_sync)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never terminate the network controller.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics must never terminate the network controller.
        }
    }

    public void Dispose()
    {
    }
}
