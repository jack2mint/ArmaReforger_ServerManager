using System.Diagnostics;
using System.IO;
using ForgeManager.Models;

namespace ForgeManager.Services;

public sealed class ServerProcessService : IDisposable
{
    private const int InitialLogTailBytes = 1024 * 1024;

    private Process? _process;
    private bool _manualStop;
    private bool _ownsProcess;
    private CancellationTokenSource? _logTailCts;
    private Task? _logTailTask;

    public event Action<string, bool>? OutputReceived;
    public event Action<int, bool>? ProcessExited;

    public bool IsRunning => _process is { HasExited: false };
    public bool IsExternalInstance => IsRunning && !_ownsProcess;
    public bool CanSendInput => IsRunning && _ownsProcess;
    public int? ProcessId => IsRunning ? _process?.Id : null;
    public DateTimeOffset? StartedAt { get; private set; }

    public bool TryAttachToExisting(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (IsRunning)
            return true;
        if (string.IsNullOrWhiteSpace(settings.ServerExecutablePath))
            return false;

        string expectedExecutable;
        try
        {
            expectedExecutable = Path.GetFullPath(settings.ServerExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var executableDirectory = Path.GetDirectoryName(expectedExecutable);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return false;

        string profilePath;
        try
        {
            profilePath = string.IsNullOrWhiteSpace(settings.ProfilePath)
                ? Path.Combine(executableDirectory, "profile")
                : Path.GetFullPath(settings.ProfilePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var candidates = Process.GetProcessesByName("ArmaReforgerServer");

        try
        {
            foreach (var candidate in candidates)
            {
                if (!PathMatches(candidate, expectedExecutable))
                    continue;

                AttachProcess(candidate, profilePath);
                return true;
            }

            // Access to MainModule can be blocked in unusual permission setups. If exactly one
            // server process exists, attaching to that single instance is safer than starting a duplicate.
            if (candidates.Length == 1)
            {
                AttachProcess(candidates[0], profilePath);
                return true;
            }
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                if (!ReferenceEquals(candidate, _process))
                    candidate.Dispose();
            }
        }

        return false;
    }

    public Task StartAsync(AppSettings settings, bool addonsRepair = false, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("The server is already running.");

        // A BAT file or another launcher may have started the server after the manager opened.
        // Attach instead of creating a second instance that would conflict on the same ports/profile.
        if (TryAttachToExisting(settings))
            return Task.CompletedTask;

        ResetProcessReference();

        if (!File.Exists(settings.ServerExecutablePath))
            throw new FileNotFoundException("ArmaReforgerServer.exe was not found.", settings.ServerExecutablePath);
        if (!File.Exists(settings.ConfigPath))
            throw new FileNotFoundException("The server config was not found.", settings.ConfigPath);

        var profilePath = Path.GetFullPath(settings.ProfilePath);
        Directory.CreateDirectory(profilePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(settings.ServerExecutablePath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.ServerExecutablePath))!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(Path.GetFullPath(settings.ConfigPath));
        startInfo.ArgumentList.Add("-profile");
        startInfo.ArgumentList.Add(profilePath);
        startInfo.ArgumentList.Add("-maxFPS");
        startInfo.ArgumentList.Add(Math.Clamp(settings.MaxFps, 10, 240).ToString());
        if (addonsRepair)
            startInfo.ArgumentList.Add("-addonsRepair");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                OutputReceived?.Invoke(args.Data, false);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                OutputReceived?.Invoke(args.Data, true);
        };

        _manualStop = false;
        _ownsProcess = true;
        WireExitHandler(process);

        if (!process.Start())
            throw new InvalidOperationException("Windows did not start ArmaReforgerServer.exe.");

        _process = process;
        StartedAt = DateTimeOffset.Now;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task SendInputAsync(string command)
    {
        if (!IsRunning || _process is null)
            throw new InvalidOperationException("The server is not running.");
        if (!_ownsProcess)
            throw new InvalidOperationException("This server was started outside the manager. Its existing stdin stream cannot be attached, but live logs and process controls remain available.");

        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (!IsRunning || _process is null)
            return;

        _manualStop = true;
        timeout ??= TimeSpan.FromSeconds(3);

        if (!_ownsProcess)
        {
            // Windows does not expose a way to reclaim stdin/stdout for an already-running console
            // process. Stop the attached process tree directly when the user explicitly requests it.
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            return;
        }

        try
        {
            await _process.StandardInput.WriteLineAsync("quit");
            await _process.StandardInput.FlushAsync();
        }
        catch
        {
            // Some server builds do not accept stdin. The process-tree stop below is the fallback.
        }

        using var cts = new CancellationTokenSource(timeout.Value);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
    }

    public void ForceStop()
    {
        if (!IsRunning || _process is null)
            return;

        _manualStop = true;
        _process.Kill(entireProcessTree: true);
    }

    public void Detach()
    {
        StopLogTail();
        ResetProcessReference();
    }

    public void Dispose() => Detach();

    private void AttachProcess(Process process, string profilePath)
    {
        ResetProcessReference();

        _process = process;
        _ownsProcess = false;
        _manualStop = false;
        process.EnableRaisingEvents = true;
        WireExitHandler(process);

        try
        {
            StartedAt = new DateTimeOffset(process.StartTime);
        }
        catch
        {
            StartedAt = DateTimeOffset.Now;
        }

        StartLogTail(Path.GetFullPath(profilePath));
    }

    private void WireExitHandler(Process process)
    {
        process.Exited += (_, _) =>
        {
            var exitCode = 0;
            try { exitCode = process.ExitCode; } catch { }
            var wasManual = _manualStop;
            StartedAt = null;
            StopLogTail();
            ProcessExited?.Invoke(exitCode, wasManual);
        };
    }

    private static bool PathMatches(Process process, string expectedExecutable)
    {
        try
        {
            var actual = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actual) &&
                   string.Equals(Path.GetFullPath(actual), expectedExecutable, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void StartLogTail(string profilePath)
    {
        StopLogTail();
        _logTailCts = new CancellationTokenSource();
        _logTailTask = Task.Run(() => TailLatestConsoleLogAsync(profilePath, _logTailCts.Token));
    }

    private async Task TailLatestConsoleLogAsync(string profilePath, CancellationToken cancellationToken)
    {
        string? currentPath = null;
        long position = 0;
        var nextDiscoveryAt = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested && IsRunning)
        {
            try
            {
                if (currentPath is null || !File.Exists(currentPath) || DateTimeOffset.UtcNow >= nextDiscoveryAt)
                {
                    var latestPath = FindLatestConsoleLog(profilePath);
                    nextDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(3);
                    if (latestPath is null)
                    {
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }

                    if (!string.Equals(currentPath, latestPath, StringComparison.OrdinalIgnoreCase))
                    {
                        currentPath = latestPath;
                        var length = new FileInfo(latestPath).Length;
                        position = Math.Max(0, length - InitialLogTailBytes);
                    }
                }

                var logPath = currentPath;
                if (string.IsNullOrWhiteSpace(logPath))
                {
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                await using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 32 * 1024,
                    useAsync: true);

                if (position > stream.Length)
                    position = 0;

                stream.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);

                if (position > 0)
                    _ = await reader.ReadLineAsync(cancellationToken);

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                    OutputReceived?.Invoke(line, false);

                position = stream.Position;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // The server may be rotating or writing the log. Retry on the next poll.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep process attachment active even when the profile log cannot be read.
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string? FindLatestConsoleLog(string profilePath)
    {
        var logsRoot = Path.Combine(profilePath, "logs");
        if (!Directory.Exists(logsRoot))
            return null;

        return Directory.EnumerateFiles(logsRoot, "console.log", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private void StopLogTail()
    {
        try { _logTailCts?.Cancel(); } catch { }
        _logTailCts?.Dispose();
        _logTailCts = null;
        _logTailTask = null;
    }

    private void ResetProcessReference()
    {
        _process?.Dispose();
        _process = null;
        _ownsProcess = false;
        StartedAt = null;
    }
}
