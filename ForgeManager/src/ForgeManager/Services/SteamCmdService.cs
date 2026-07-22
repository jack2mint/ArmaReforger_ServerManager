using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeManager.Services;

public sealed class SteamCmdService : IDisposable
{
    private const string SteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const int StableServerAppId = 1874900;
    private const int ExperimentalServerAppId = 1890870;
    private const int CopyBufferSize = 81920;
    private const int MaxArchiveEntries = 10_000;
    private const long MaxExtractedBytes = 1L * 1024 * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly object _processGate = new();
    private Process? _process;
    private bool _disposed;

    public bool IsBusy
    {
        get
        {
            lock (_processGate)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    public async Task EnsureInstalledAsync(
        string steamCmdFolder,
        IProgress<string> log,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(log);

        steamCmdFolder = NormalizeRequiredDirectory(steamCmdFolder, nameof(steamCmdFolder));
        Directory.CreateDirectory(steamCmdFolder);

        var executablePath = Path.Combine(steamCmdFolder, "steamcmd.exe");
        if (File.Exists(executablePath))
        {
            progress?.Report(100);
            log.Report("SteamCMD is already installed.");
            return;
        }

        var archivePath = Path.Combine(steamCmdFolder, "steamcmd.download.zip");
        try
        {
            log.Report("Downloading SteamCMD...");
            using var response = await _http.GetAsync(
                SteamCmdUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalLength = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                archivePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[CopyBufferSize];
            long downloaded = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                if (totalLength is > 0)
                    progress?.Report(downloaded * 100d / totalLength.Value);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            log.Report("Extracting SteamCMD...");
            SafeExtract(archivePath, steamCmdFolder);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }

        if (!File.Exists(executablePath))
            throw new InvalidDataException("SteamCMD extraction completed, but steamcmd.exe was not found.");

        progress?.Report(100);
        await RunAsync(
            executablePath,
            steamCmdFolder,
            ["+quit"],
            log,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InstallOrUpdateServerAsync(
        string steamCmdFolder,
        string serverFolder,
        int appId,
        bool validate,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(log);

        if (appId is not (StableServerAppId or ExperimentalServerAppId))
            throw new ArgumentOutOfRangeException(nameof(appId), "Only the stable and experimental Arma Reforger Server app IDs are allowed.");

        steamCmdFolder = NormalizeRequiredDirectory(steamCmdFolder, nameof(steamCmdFolder));
        serverFolder = NormalizeRequiredDirectory(serverFolder, nameof(serverFolder));

        var executablePath = Path.Combine(steamCmdFolder, "steamcmd.exe");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("steamcmd.exe is missing. Run Install SteamCMD + Server first.", executablePath);

        Directory.CreateDirectory(serverFolder);

        var arguments = new List<string>
        {
            "+force_install_dir",
            serverFolder,
            "+login",
            "anonymous",
            "+app_update",
            appId.ToString(CultureInfo.InvariantCulture)
        };
        if (validate)
            arguments.Add("validate");
        arguments.Add("+quit");

        await RunAsync(
            executablePath,
            steamCmdFolder,
            arguments,
            log,
            cancellationToken).ConfigureAwait(false);

        var serverExecutable = Path.Combine(serverFolder, "ArmaReforgerServer.exe");
        if (!File.Exists(serverExecutable))
            throw new InvalidDataException("SteamCMD finished, but ArmaReforgerServer.exe was not found in the selected folder.");
    }

    public void Cancel()
    {
        Process? process;
        lock (_processGate)
            process = _process;

        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill().
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows may deny termination if the process is already shutting down.
        }
    }

    private async Task RunAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                log.Report(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                log.Report("[stderr] " + eventArgs.Data);
        };

        lock (_processGate)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Another SteamCMD operation is already running.");
            _process = process;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("Could not start SteamCMD.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(Cancel);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Ensure redirected asynchronous output handlers have finished draining.
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"SteamCMD exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancel();
            throw;
        }
        finally
        {
            lock (_processGate)
            {
                if (ReferenceEquals(_process, process))
                    _process = null;
            }
        }
    }

    private static void SafeExtract(string archivePath, string destination)
    {
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var destinationPrefix = destinationRoot + Path.DirectorySeparatorChar;
        long extractedBytes = 0;
        var entryCount = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (++entryCount > MaxArchiveEntries)
                throw new InvalidDataException("The SteamCMD archive contains an unreasonable number of entries.");

            if (IsUnixSymbolicLink(entry))
                throw new InvalidDataException("The SteamCMD archive contains an unsupported symbolic link.");

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaxExtractedBytes)
                throw new InvalidDataException("The SteamCMD archive expands beyond the allowed safety limit.");

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetPath, destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsafe path detected in the SteamCMD archive.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var targetDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidDataException("The SteamCMD archive contains an invalid file path.");
            Directory.CreateDirectory(targetDirectory);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static bool IsUnixSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return (unixMode & UnixFileTypeMask) == UnixSymbolicLink;
    }

    private static string NormalizeRequiredDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A folder path is required.", parameterName);

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A failed cleanup should not hide the original download or extraction error.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Cancel();
        _http.Dispose();
    }
}
