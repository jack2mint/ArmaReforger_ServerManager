using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeManager.Models;

public sealed class ServerConfig
{
    [JsonPropertyName("bindAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BindAddress { get; set; }

    [JsonPropertyName("bindPort")]
    public int BindPort { get; set; } = 2001;

    [JsonPropertyName("publicAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicAddress { get; set; }

    [JsonPropertyName("publicPort")]
    public int PublicPort { get; set; } = 2001;

    [JsonPropertyName("game")]
    public GameConfig Game { get; set; } = new();

    [JsonPropertyName("operating")]
    public OperatingConfig Operating { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GameConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "[NA1] ForgeManager | Udachne | Crossplay | MFO";

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("passwordAdmin")]
    public string PasswordAdmin { get; set; } = string.Empty;

    [JsonPropertyName("admins")]
    public List<string> Admins { get; set; } = [];

    [JsonPropertyName("scenarioId")]
    public string ScenarioId { get; set; } = "{42807BDC0929009F}Missions/CIE_Udachne_Conflict.conf";

    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; } = 64;

    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    [JsonPropertyName("crossPlatform")]
    public bool CrossPlatform { get; set; } = true;

    [JsonPropertyName("gameProperties")]
    public GamePropertiesConfig GameProperties { get; set; } = new();

    [JsonPropertyName("modsRequiredByDefault")]
    public bool ModsRequiredByDefault { get; set; } = true;

    [JsonPropertyName("mods")]
    public List<ModConfig> Mods { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GamePropertiesConfig
{
    [JsonPropertyName("serverMaxViewDistance")]
    public int ServerMaxViewDistance { get; set; } = 1600;

    [JsonPropertyName("serverMinGrassDistance")]
    public int ServerMinGrassDistance { get; set; } = 150;

    [JsonPropertyName("networkViewDistance")]
    public int NetworkViewDistance { get; set; } = 1500;

    [JsonPropertyName("disableThirdPerson")]
    public bool DisableThirdPerson { get; set; } = true;

    [JsonPropertyName("fastValidation")]
    public bool FastValidation { get; set; } = true;

    [JsonPropertyName("battlEye")]
    public bool BattlEye { get; set; } = true;

    [JsonPropertyName("VONDisableUI")]
    public bool VonDisableUi { get; set; }

    [JsonPropertyName("VONDisableDirectSpeechUI")]
    public bool VonDisableDirectSpeechUi { get; set; }

    [JsonPropertyName("VONCanTransmitCrossFaction")]
    public bool VonCanTransmitCrossFaction { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ModConfig
{
    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OperatingConfig
{
    [JsonPropertyName("lobbyPlayerSynchronise")]
    public bool LobbyPlayerSynchronise { get; set; } = true;

    [JsonPropertyName("playerSaveTime")]
    public int PlayerSaveTime { get; set; } = 120;

    [JsonPropertyName("aiLimit")]
    public int AiLimit { get; set; } = -1;

    [JsonPropertyName("slotReservationTimeout")]
    public int SlotReservationTimeout { get; set; } = 60;

    [JsonPropertyName("joinQueue")]
    public JoinQueueConfig JoinQueue { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class JoinQueueConfig
{
    [JsonPropertyName("maxSize")]
    public int MaxSize { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
