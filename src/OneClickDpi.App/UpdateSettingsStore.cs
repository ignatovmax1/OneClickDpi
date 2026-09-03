using System.IO;
using System.Text.Json;

namespace OneClickDpi.App;

public sealed class UpdateSettingsStore
{
    private readonly string _path;

    public UpdateSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public bool LoadInstallOnExit()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }

            var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(_path));
            return settings?.InstallOnExit ?? true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    public void SaveInstallOnExit(bool value)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new UpdateSettings(value)));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record UpdateSettings(bool InstallOnExit);
}
