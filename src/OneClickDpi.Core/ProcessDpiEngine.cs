using System.Diagnostics;

namespace OneClickDpi.Core;

public interface IDpiEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    string? ActiveStrategyId { get; }
    event EventHandler<string>? LogReceived;
    event EventHandler? UnexpectedlyExited;
    Task StartAsync(StrategyProfile strategy, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class ProcessDpiEngine : IDpiEngine
{
    private readonly EnginePaths _paths;
    private readonly EngineIntegrityValidator _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public ProcessDpiEngine(EnginePaths paths, EngineIntegrityValidator validator)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public bool IsRunning => _process is { HasExited: false };
    public string? ActiveStrategyId { get; private set; }
    public event EventHandler<string>? LogReceived;
    public event EventHandler? UnexpectedlyExited;

    public async Task StartAsync(StrategyProfile strategy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await _validator.ValidateAsync(_paths, cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = _paths.Executable,
                WorkingDirectory = _paths.RootDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in strategy.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += OnExited;
            LogReceived?.Invoke(this, $"Starting strategy {strategy.Id}.");

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The DPI engine did not start.");
            }

            _process = process;
            ActiveStrategyId = strategy.Id;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException($"The DPI engine exited during startup with code {exitCode}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        ActiveStrategyId = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Exited -= OnExited;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The owned process exited between the state check and termination.
        }
        finally
        {
            process.OutputDataReceived -= OnOutput;
            process.ErrorDataReceived -= OnOutput;
            process.Exited -= OnExited;
            process.Dispose();
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            LogReceived?.Invoke(this, eventArgs.Data);
        }
    }

    private void OnExited(object? sender, EventArgs eventArgs)
    {
        if (ReferenceEquals(sender, _process))
        {
            ActiveStrategyId = null;
            var exitCode = sender is Process process ? process.ExitCode : -1;
            LogReceived?.Invoke(this, $"DPI engine stopped unexpectedly with code {exitCode}.");
            UnexpectedlyExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
