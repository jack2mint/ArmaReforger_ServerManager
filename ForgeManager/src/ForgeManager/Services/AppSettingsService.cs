using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ForgeManager.Models;

namespace ForgeManager.Services;

public sealed class AppSettingsService
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public string ApplicationDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);

    public string? PortableServerRoot => FindPortableServerRoot();

    public bool IsPortableMode => PortableServerRoot is not null;

    public string SettingsDirectory => PortableServerRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForgeManager");

    public string SettingsPath => Path.Combine(SettingsDirectory, "forgemanager.settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SettingsDirectory);

        AppSettings settings;
        if (!File.Exists(SettingsPath))
        {
            settings = DiscoverDefaults();
        }
        else
        {
            try
            {
                await using var stream = File.OpenRead(SettingsPath);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, cancellationToken)
                           ?? DiscoverDefaults();
            }
            catch
            {
                var backup = SettingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(SettingsPath, backup, overwrite: true);
                settings = DiscoverDefaults();
            }
        }

        ApplyDetectedLayout(settings);
        await SaveAsync(settings, cancellationToken);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            ApplyDetectedLayout(settings);
            Directory.CreateDirectory(SettingsDirectory);
            var temp = SettingsPath + $".{Environment.ProcessId}.tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, _jsonOptions), cancellationToken);
            File.Move(temp, SettingsPath, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void ApplyDetectedLayout(AppSettings settings)
    {
        var portableRoot = PortableServerRoot;
        if (portableRoot is not null)
        {
            // Drop-in mode is authoritative. Stale paths from an older installation cannot override
            // the server and config placed beside the manager executable.
            settings.ServerExecutablePath = Path.Combine(portableRoot, "ArmaReforgerServer.exe");
            settings.ConfigPath = Path.Combine(portableRoot, "configs", "config.json");
            settings.ProfilePath = Path.Combine(portableRoot, "profile");
        }
        else
        {
            var detected = DiscoverServerRoot();
            if (detected is not null)
            {
                if (string.IsNullOrWhiteSpace(settings.ServerExecutablePath) || !File.Exists(settings.ServerExecutablePath))
                    settings.ServerExecutablePath = Path.Combine(detected, "ArmaReforgerServer.exe");
                if (string.IsNullOrWhiteSpace(settings.ConfigPath) || !File.Exists(settings.ConfigPath))
                    settings.ConfigPath = Path.Combine(detected, "configs", "config.json");
                if (string.IsNullOrWhiteSpace(settings.ProfilePath))
                    settings.ProfilePath = Path.Combine(detected, "profile");
            }
        }

        if (string.IsNullOrWhiteSpace(settings.ClientExecutablePath) || !File.Exists(settings.ClientExecutablePath))
            settings.ClientExecutablePath = DiscoverClientExecutable() ?? string.Empty;
    }

    private AppSettings DiscoverDefaults()
    {
        var settings = new AppSettings();
        ApplyDetectedLayout(settings);
        return settings;
    }

    private string? FindPortableServerRoot()
    {
        var root = ApplicationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return File.Exists(Path.Combine(root, "ArmaReforgerServer.exe")) ? root : null;
    }

    private string? DiscoverServerRoot()
    {
        var baseDir = ApplicationDirectory;
        var parent = Directory.GetParent(baseDir)?.FullName;
        var candidates = new[]
        {
            baseDir,
            parent,
            Path.Combine(baseDir, "ArmaReforgerServer"),
            Path.Combine(parent ?? baseDir, "ArmaReforgerServer"),
            Environment.CurrentDirectory
        }
        .OfType<string>()
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase);

        return candidates.FirstOrDefault(root => File.Exists(Path.Combine(root, "ArmaReforgerServer.exe")));
    }

    private static string? DiscoverClientExecutable()
    {
        var candidates = new List<string>();

        void AddSteamRoot(string? steamRoot)
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
                return;

            steamRoot = Environment.ExpandEnvironmentVariables(steamRoot.Replace('/', Path.DirectorySeparatorChar));
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Arma Reforger", "ArmaReforgerSteam.exe"));

            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
                return;

            try
            {
                var text = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var libraryRoot = match.Groups["path"].Value.Replace("\\\\", "\\");
                    candidates.Add(Path.Combine(libraryRoot, "steamapps", "common", "Arma Reforger", "ArmaReforgerSteam.exe"));
                }
            }
            catch
            {
                // Client discovery is optional; the user can still browse manually.
            }
        }

        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            AddSteamRoot(key?.GetValue("SteamPath") as string);
        }
        catch
        {
            // Registry access is optional.
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }
}
