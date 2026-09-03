using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace OneClickDpi.App;

public static class UpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string HealthArgument = "--update-health";
    private const string ErrorArgument = "--update-error";
    private const string CleanupRunnerArgument = "--cleanup-runner";
    private const string CleanupPidArgument = "--cleanup-pid";
    private const string CleanupWorkArgument = "--cleanup-work";
    private const string CleanupDownloadArgument = "--cleanup-download";
    private const long MaximumExtractedBytes = 1024L * 1024 * 1024;
    private const int MaximumArchiveEntries = 5000;

    public static bool IsApplyMode(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0].Equals(ApplyArgument, StringComparison.Ordinal);

    public static int RunApplyMode(IReadOnlyList<string> arguments)
    {
        try
        {
            return RunApplyAsync(arguments).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                "Не удалось установить обновление. Текущая версия не была заменена.\n\n" + exception.Message,
                "OneClick DPI — обновление",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    public static void Launch(PreparedUpdate update, string installDirectory)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the running application executable.");
        var runnerDirectory = Path.Combine(GetUpdateRoot(), "Runner");
        Directory.CreateDirectory(runnerDirectory);
        var runnerPath = Path.Combine(
            runnerDirectory,
            $"OneClickDpi.Updater-{GetCurrentVersion()}-{Guid.NewGuid():N}.exe");
        File.Copy(currentExecutable, runnerPath, overwrite: false);

        var startInfo = new ProcessStartInfo
        {
            FileName = runnerPath,
            UseShellExecute = false,
            WorkingDirectory = runnerDirectory
        };
        startInfo.ArgumentList.Add(ApplyArgument);
        startInfo.ArgumentList.Add(update.PackagePath);
        startInfo.ArgumentList.Add(update.ManifestPath);
        startInfo.ArgumentList.Add(update.SignaturePath);
        startInfo.ArgumentList.Add(Path.GetFullPath(installDirectory));
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the update installer.");
    }

    public static StartupUpdateInfo ProcessNormalStartupArguments(IReadOnlyList<string> arguments)
    {
        var healthPath = ReadArgument(arguments, HealthArgument);
        var error = ReadArgument(arguments, ErrorArgument);
        var runnerPath = ReadArgument(arguments, CleanupRunnerArgument);
        var runnerPidText = ReadArgument(arguments, CleanupPidArgument);
        var workPath = ReadArgument(arguments, CleanupWorkArgument);
        var downloadPath = ReadArgument(arguments, CleanupDownloadArgument);

        if (!string.IsNullOrWhiteSpace(healthPath))
        {
            ValidateControlledPath(healthPath, allowFile: true);
            Directory.CreateDirectory(Path.GetDirectoryName(healthPath)!);
            File.WriteAllText(healthPath, GetCurrentVersion().ToString(3));
        }

        if (!string.IsNullOrWhiteSpace(runnerPath)
            && int.TryParse(runnerPidText, out var runnerPid))
        {
            _ = Task.Run(() => CleanupAfterRunnerAsync(
                runnerPath,
                runnerPid,
                workPath,
                downloadPath));
        }

        return new StartupUpdateInfo(error);
    }

    public static string GetUpdateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneClickDpi",
        "Updates");

    public static Version GetCurrentVersion() =>
        typeof(UpdateInstaller).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private static async Task<int> RunApplyAsync(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 6 || !int.TryParse(arguments[5], out var originalProcessId))
        {
            throw new InvalidDataException("Update installer arguments are invalid.");
        }

        var packagePath = ValidateControlledPath(arguments[1], allowFile: true);
        var manifestPath = ValidateControlledPath(arguments[2], allowFile: true);
        var signaturePath = ValidateControlledPath(arguments[3], allowFile: true);
        var installDirectory = ValidateInstallDirectory(arguments[4]);
        var installedExecutable = Path.Combine(installDirectory, "OneClickDpi.exe");
        if (!File.Exists(installedExecutable))
        {
            throw new InvalidDataException("The selected installation directory is not a OneClick DPI installation.");
        }

        if (!await FilesHaveSameHashAsync(Environment.ProcessPath!, installedExecutable).ConfigureAwait(false))
        {
            throw new CryptographicException("The update runner does not match the installed application.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String((await File.ReadAllTextAsync(signaturePath).ConfigureAwait(false)).Trim());
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The downloaded update signature is malformed.", exception);
        }

        var manifest = UpdateSecurity.VerifyAndParseManifest(manifestBytes, signatureBytes);
        if (!Path.GetFileName(packagePath).Equals(manifest.PackageFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The downloaded package name does not match its signed manifest.");
        }

        await UpdateSecurity.VerifyPackageAsync(packagePath, manifest, CancellationToken.None).ConfigureAwait(false);
        await WaitForOriginalApplicationAsync(originalProcessId, installedExecutable).ConfigureAwait(false);

        var transactionDirectory = Path.Combine(
            GetUpdateRoot(),
            "Transactions",
            $"{manifest.Version}-{Guid.NewGuid():N}");
        var stagingDirectory = Path.Combine(transactionDirectory, "staging");
        var backupDirectory = Path.Combine(transactionDirectory, "backup");
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);
        var downloadDirectory = Path.GetDirectoryName(packagePath)!;
        List<InstalledFileState>? states = null;

        try
        {
            var stagedFiles = ExtractVerifiedPackage(packagePath, manifest, stagingDirectory);
            states = ApplyFiles(stagedFiles, stagingDirectory, installDirectory, backupDirectory);
            var healthPath = Path.Combine(transactionDirectory, "startup.ok");
            var updatedProcess = StartInstalledApplication(
                installedExecutable,
                healthPath,
                transactionDirectory,
                downloadDirectory);
            if (await WaitForHealthAsync(updatedProcess, healthPath).ConfigureAwait(false))
            {
                updatedProcess.Dispose();
                return 0;
            }

            TryStopProcess(updatedProcess);
            RollBackFiles(states, installDirectory, backupDirectory);
            StartRollbackApplication(
                installedExecutable,
                "Новая версия не подтвердила успешный запуск. Выполнен автоматический откат.",
                transactionDirectory,
                downloadDirectory);
            return 2;
        }
        catch (Exception exception)
        {
            if (states is not null)
            {
                RollBackFiles(states, installDirectory, backupDirectory);
            }

            StartRollbackApplication(
                installedExecutable,
                "Обновление отменено: " + exception.Message,
                transactionDirectory,
                downloadDirectory);
            return 3;
        }
    }

    internal static IReadOnlyList<string> ExtractVerifiedPackage(
        string packagePath,
        UpdateManifest manifest,
        string stagingDirectory)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntries)
        {
            throw new InvalidDataException("Update archive contains an invalid number of entries.");
        }

        var expectedRoot = $"OneClickDpi-MVP-{manifest.Version}-win-x64/";
        var files = new List<string>();
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith(expectedRoot, StringComparison.Ordinal)
                || normalized.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException("Update archive contains an unexpected path.");
            }

            var relative = normalized[expectedRoot.Length..];
            if (string.IsNullOrEmpty(relative) || relative.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var segments = relative.Split('/');
            if (segments.Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException("Update archive contains an unsafe path.");
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes)
            {
                throw new InvalidDataException("Update archive expands beyond the allowed size.");
            }

            var destination = ResolveChildPath(stagingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            files.Add(relative.Replace('/', Path.DirectorySeparatorChar));
        }

        RequireStagedFile(files, "OneClickDpi.exe");
        RequireStagedFile(files, Path.Combine("Engine", "winws.exe"));
        RequireStagedFile(files, Path.Combine("Tunnel", "Tor", "tor.exe"));
        var executableVersionText = FileVersionInfo.GetVersionInfo(
            Path.Combine(stagingDirectory, "OneClickDpi.exe")).ProductVersion;
        var metadataSeparator = executableVersionText?.IndexOf('+') ?? -1;
        if (metadataSeparator >= 0)
        {
            executableVersionText = executableVersionText![..metadataSeparator];
        }

        if (!Version.TryParse(executableVersionText, out var executableVersion)
            || executableVersion != Version.Parse(manifest.Version))
        {
            throw new InvalidDataException("The application version inside the update package is invalid.");
        }

        return files;
    }

    private static List<InstalledFileState> ApplyFiles(
        IReadOnlyList<string> relativeFiles,
        string stagingDirectory,
        string installDirectory,
        string backupDirectory)
    {
        var orderedFiles = relativeFiles
            .OrderBy(path => Path.GetFileName(path).Equals("OneClickDpi.exe", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = new List<InstalledFileState>(orderedFiles.Length);
        try
        {
            foreach (var relative in orderedFiles)
            {
                var source = ResolveChildPath(stagingDirectory, relative);
                var destination = ResolveChildPath(installDirectory, relative);
                var existed = File.Exists(destination);
                if (existed)
                {
                    var backup = ResolveChildPath(backupDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                }

                states.Add(new InstalledFileState(relative, existed));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyFileAtomically(source, destination);
            }
        }
        catch
        {
            RollBackFiles(states, installDirectory, backupDirectory);
            throw;
        }

        return states;
    }

    private static void RollBackFiles(
        IReadOnlyList<InstalledFileState> states,
        string installDirectory,
        string backupDirectory)
    {
        foreach (var state in states.Reverse())
        {
            var destination = ResolveChildPath(installDirectory, state.RelativePath);
            try
            {
                if (state.Existed)
                {
                    var backup = ResolveChildPath(backupDirectory, state.RelativePath);
                    if (File.Exists(backup))
                    {
                        CopyFileAtomically(backup, destination);
                    }
                }
                else
                {
                    File.Delete(destination);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static Process StartInstalledApplication(
        string executable,
        string healthPath,
        string transactionDirectory,
        string downloadDirectory)
    {
        var info = CreateRestartInfo(executable, transactionDirectory, downloadDirectory);
        info.ArgumentList.Add(HealthArgument);
        info.ArgumentList.Add(healthPath);
        return Process.Start(info)
            ?? throw new InvalidOperationException("The updated application did not start.");
    }

    private static void StartRollbackApplication(
        string executable,
        string message,
        string transactionDirectory,
        string downloadDirectory)
    {
        if (!File.Exists(executable))
        {
            return;
        }

        var info = CreateRestartInfo(executable, transactionDirectory, downloadDirectory);
        info.ArgumentList.Add(ErrorArgument);
        info.ArgumentList.Add(message.Length <= 500 ? message : message[..500]);
        Process.Start(info);
    }

    private static ProcessStartInfo CreateRestartInfo(
        string executable,
        string transactionDirectory,
        string downloadDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        info.ArgumentList.Add(CleanupRunnerArgument);
        info.ArgumentList.Add(Environment.ProcessPath!);
        info.ArgumentList.Add(CleanupPidArgument);
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(CleanupWorkArgument);
        info.ArgumentList.Add(transactionDirectory);
        info.ArgumentList.Add(CleanupDownloadArgument);
        info.ArgumentList.Add(downloadDirectory);
        return info;
    }

    private static async Task<bool> WaitForHealthAsync(Process process, string healthPath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(healthPath))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task WaitForOriginalApplicationAsync(int processId, string expectedExecutable)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            try
            {
                var actualPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(actualPath)
                    && !Path.GetFullPath(actualPath).Equals(
                        Path.GetFullPath(expectedExecutable),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The update target process is invalid.");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task<bool> FilesHaveSameHashAsync(string first, string second)
    {
        var firstHash = await HashFileAsync(first).ConfigureAwait(false);
        var secondHash = await HashFileAsync(second).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream).ConfigureAwait(false);
    }

    private static void CopyFileAtomically(string source, string destination)
    {
        var temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static string ValidateInstallDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath)
            || string.Equals(pathRoot, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update installation path is unsafe.");
        }

        return fullPath;
    }

    private static string ValidateControlledPath(string path, bool allowFile)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(GetUpdateRoot()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || (!allowFile && fullPath.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Update working path is outside the controlled directory.");
        }

        return fullPath;
    }

    private static string ResolveChildPath(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update path escaped its target directory.");
        }

        return fullPath;
    }

    private static void RequireStagedFile(IEnumerable<string> files, string expected)
    {
        if (!files.Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Update package is missing {expected}.");
        }
    }

    private static string? ReadArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static async Task CleanupAfterRunnerAsync(
        string runnerPath,
        int runnerPid,
        string? workPath,
        string? downloadPath)
    {
        try
        {
            using var runner = Process.GetProcessById(runnerPid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await runner.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var runnerRoot = Path.Combine(GetUpdateRoot(), "Runner");
        var fullRunnerPath = Path.GetFullPath(runnerPath);
        var normalizedRunnerRoot = Path.GetFullPath(runnerRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (fullRunnerPath.StartsWith(normalizedRunnerRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullRunnerPath).StartsWith("OneClickDpi.Updater-", StringComparison.Ordinal))
        {
            TryDeleteFile(fullRunnerPath);
        }

        TryDeleteControlledDirectory(workPath);
        TryDeleteControlledDirectory(downloadPath);
    }

    private static void TryDeleteControlledDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var controlled = ValidateControlledPath(path, allowFile: false);
            if (Directory.Exists(controlled))
            {
                Directory.Delete(controlled, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record InstalledFileState(string RelativePath, bool Existed);
}

public sealed record StartupUpdateInfo(string? ErrorMessage);
