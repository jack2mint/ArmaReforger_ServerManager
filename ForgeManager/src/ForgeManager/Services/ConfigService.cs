using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using ForgeManager.Models;

namespace ForgeManager.Services;

public sealed class ConfigService
{
    private const int MaximumBackups = 50;
    private static readonly Regex ModIdRegex = new("^[0-9A-Fa-f]{16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ServerConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ServerConfig>(stream, _jsonOptions, cancellationToken)
               ?? throw new InvalidDataException("The server configuration is empty.");
    }

    public Task<string> LoadRawAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public IReadOnlyList<string> Validate(ServerConfig config)
    {
        var errors = new List<string>();

        if (config.BindPort is < 1 or > 65535)
            errors.Add("bindPort must be between 1 and 65535.");
        if (config.PublicPort is < 1 or > 65535)
            errors.Add("publicPort must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.Game.Name))
            errors.Add("game.name cannot be blank.");
        if (string.IsNullOrWhiteSpace(config.Game.ScenarioId))
            errors.Add("game.scenarioId cannot be blank.");
        if (string.Equals(config.Game.PasswordAdmin, "AdminPasswordHere", StringComparison.Ordinal))
            errors.Add("game.passwordAdmin still uses the insecure example value. Set a unique password or leave it blank to disable password-based admin login.");
        if (config.Game.MaxPlayers is < 1 or > 128)
            errors.Add("game.maxPlayers must be between 1 and 128.");
        if (config.Game.GameProperties.ServerMinGrassDistance is < 0 or > 150)
            errors.Add("serverMinGrassDistance must be between 0 and 150.");
        if (config.Game.GameProperties.ServerMaxViewDistance < 500)
            errors.Add("serverMaxViewDistance is unusually low; use at least 500.");
        if (config.Game.GameProperties.NetworkViewDistance < 500)
            errors.Add("networkViewDistance is unusually low; use at least 500.");

        var duplicates = config.Game.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.ModId))
            .GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var duplicate in duplicates)
            errors.Add($"Duplicate mod ID: {duplicate}");

        foreach (var mod in config.Game.Mods)
        {
            if (!ModIdRegex.IsMatch(mod.ModId ?? string.Empty))
                errors.Add($"Invalid mod ID: '{mod.ModId}'. Expected 16 hexadecimal characters.");
        }

        return errors;
    }

    public async Task SaveAsync(string path, ServerConfig config, CancellationToken cancellationToken = default)
    {
        var errors = Validate(config);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));

        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await SaveRawAsync(path, json, cancellationToken);
    }

    public async Task SaveRawAsync(string path, string json, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            var normalized = JsonSerializer.Serialize(document.RootElement, _jsonOptions);
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Config path has no parent directory.");
            Directory.CreateDirectory(directory);

            if (File.Exists(path))
            {
                var backupDirectory = Path.Combine(directory, "backups");
                Directory.CreateDirectory(backupDirectory);
                var backupPath = Path.Combine(backupDirectory, $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
                File.Copy(path, backupPath, overwrite: false);
                TrimBackups(backupDirectory);
            }

            var tempPath = path + $".{Environment.ProcessId}.tmp";
            await File.WriteAllTextAsync(tempPath, normalized + Environment.NewLine, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void TrimBackups(string backupDirectory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(backupDirectory, "config-*.json")
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(MaximumBackups))
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
            // Backup cleanup is best-effort and must never block a valid config save.
        }
        catch (UnauthorizedAccessException)
        {
            // The config save remains valid even if older backups cannot be removed.
        }
    }
}
