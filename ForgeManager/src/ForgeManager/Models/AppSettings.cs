namespace ForgeManager.Models;

public sealed class AppSettings
{
    public string ServerExecutablePath { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string ProfilePath { get; set; } = string.Empty;
    public string ClientExecutablePath { get; set; } = string.Empty;
    public int MaxFps { get; set; } = 60;
    public bool AutoRestart { get; set; } = true;
    public int RestartDelaySeconds { get; set; } = 10;
    public string LocalJoinAddress { get; set; } = "127.0.0.1";
    public bool AutoDetectLocalJoinAddress { get; set; } = true;
    public string SteamCmdPath { get; set; } = string.Empty;
    public string ServerInstallPath { get; set; } = string.Empty;
    public bool UseExperimentalServer { get; set; } = false;
}
