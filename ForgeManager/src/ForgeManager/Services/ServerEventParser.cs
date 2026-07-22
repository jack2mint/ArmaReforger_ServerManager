using System.Net;
using System.Text.RegularExpressions;
using ForgeManager.Models;

namespace ForgeManager.Services;

public sealed class ServerEventParser
{
    private static readonly Regex IpRegex = new(
        @"(?<!\d)(?<ip>(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})(?::(?<port>\d{1,5}))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdRegex = new(
        """(?ix)\b(?:player(?:id)?|identity(?:id)?|steam(?:id)?|client(?:id)?)\s*[:=#]\s*["']?(?<id>\d{2,20})""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameAssignmentRegex = new(
        """(?ix)\b(?:name|playername|displayname)\s*[:=]\s*["'](?<name>[^"']{1,64})["']""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedNameRegex = new(
        """["'](?<name>[^"']{1,64})["']""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlayerPrefixRegex = new(
        @"(?ix)\bplayer(?:\s+\d+)?\s*(?:\([^)]*\))?\s*[:=-]?\s*(?<name>[A-Za-z0-9_\-\.\[\] ]{2,64}?)(?=\s+(?:connected|joined|disconnected|left|killed|was killed)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KillByRegex = new(
        @"(?ix)\b(?<victim>[A-Za-z0-9_\-\.\[\] ]{2,64}?)\s+(?:was\s+)?killed\s+by\s+(?<killer>[A-Za-z0-9_\-\.\[\] ]{2,64}?)(?:\s+(?:with|using)\s+(?<weapon>[^,;\[]+?))?(?:\s+(?:at|from)\s+(?<distance>\d+(?:\.\d+)?\s*m))?(?:$|[,;\[])" ,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KillerFirstRegex = new(
        @"(?ix)\b(?<killer>[A-Za-z0-9_\-\.\[\] ]{2,64}?)\s+killed\s+(?<victim>[A-Za-z0-9_\-\.\[\] ]{2,64}?)(?:\s+(?:with|using)\s+(?<weapon>[^,;\[]+?))?(?:\s+(?:at|from)\s+(?<distance>\d+(?:\.\d+)?\s*m))?(?:$|[,;\[])" ,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StructuredKillRegex = new(
        """(?ix)(?:OnPlayerKilled|PlayerKilled|KillFeed).*?(?:killer|instigator)\s*[:=]\s*["']?(?<killer>[^,;"']+)["']?.*?(?:victim|player)\s*[:=]\s*["']?(?<victim>[^,;"']+)["']?(?:.*?weapon\s*[:=]\s*["']?(?<weapon>[^,;"']+)["']?)?(?:.*?distance\s*[:=]\s*(?<distance>\d+(?:\.\d+)?\s*m?))?""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ParsedServerEvent Parse(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return new ParsedServerEvent();

        var line = WebUtility.HtmlDecode(rawLine).Trim();
        var lower = line.ToLowerInvariant();

        var kill = TryParseKill(line, lower);
        if (kill.Kind != ParsedServerEventKind.None)
            return kill;

        if (LooksLikeGameMasterEvent(lower))
        {
            var identity = ExtractIdentity(line);
            return new ParsedServerEvent
            {
                Kind = ParsedServerEventKind.GameMaster,
                PlayerName = identity.Name,
                PlayerId = identity.Id,
                Address = identity.Address,
                Details = CleanDetails(line)
            };
        }

        if (LooksLikeJoin(lower))
        {
            var identity = ExtractIdentity(line);
            return new ParsedServerEvent
            {
                Kind = ParsedServerEventKind.PlayerJoined,
                PlayerName = identity.Name,
                PlayerId = identity.Id,
                Address = identity.Address,
                Details = CleanDetails(line)
            };
        }

        if (LooksLikeLeave(lower))
        {
            var identity = ExtractIdentity(line);
            return new ParsedServerEvent
            {
                Kind = ParsedServerEventKind.PlayerLeft,
                PlayerName = identity.Name,
                PlayerId = identity.Id,
                Address = identity.Address,
                Details = CleanDetails(line)
            };
        }

        if (LooksLikePlayerSnapshot(lower))
        {
            var identity = ExtractIdentity(line);
            if (!string.IsNullOrWhiteSpace(identity.Name) || !string.IsNullOrWhiteSpace(identity.Id))
            {
                return new ParsedServerEvent
                {
                    Kind = ParsedServerEventKind.PlayerObserved,
                    PlayerName = identity.Name,
                    PlayerId = identity.Id,
                    Address = identity.Address,
                    Details = CleanDetails(line)
                };
            }
        }

        return new ParsedServerEvent();
    }

    private static ParsedServerEvent TryParseKill(string line, string lower)
    {
        if (!(lower.Contains("killed", StringComparison.Ordinal) ||
              lower.Contains("playerkilled", StringComparison.Ordinal) ||
              lower.Contains("onplayerkilled", StringComparison.Ordinal) ||
              lower.Contains("killfeed", StringComparison.Ordinal)))
        {
            return new ParsedServerEvent();
        }

        Match match = StructuredKillRegex.Match(line);
        if (!match.Success)
            match = KillByRegex.Match(line);
        if (!match.Success)
            match = KillerFirstRegex.Match(line);

        if (!match.Success)
        {
            return new ParsedServerEvent
            {
                Kind = ParsedServerEventKind.Kill,
                Killer = "Unknown",
                Victim = "Unknown",
                Weapon = string.Empty,
                Distance = string.Empty,
                IsFriendlyFire = lower.Contains("friendly fire", StringComparison.Ordinal) || lower.Contains("teamkill", StringComparison.Ordinal),
                IsSuicide = lower.Contains("suicide", StringComparison.Ordinal),
                Details = CleanDetails(line)
            };
        }

        var killer = CleanName(match.Groups["killer"].Value);
        var victim = CleanName(match.Groups["victim"].Value);
        var suicide = lower.Contains("suicide", StringComparison.Ordinal) ||
                      (!string.IsNullOrWhiteSpace(killer) && killer.Equals(victim, StringComparison.OrdinalIgnoreCase));

        return new ParsedServerEvent
        {
            Kind = ParsedServerEventKind.Kill,
            Killer = string.IsNullOrWhiteSpace(killer) ? "Unknown" : killer,
            Victim = string.IsNullOrWhiteSpace(victim) ? "Unknown" : victim,
            Weapon = CleanName(match.Groups["weapon"].Value),
            Distance = CleanName(match.Groups["distance"].Value),
            IsFriendlyFire = lower.Contains("friendly fire", StringComparison.Ordinal) ||
                             lower.Contains("teamkill", StringComparison.Ordinal) ||
                             lower.Contains("team kill", StringComparison.Ordinal),
            IsSuicide = suicide,
            Details = CleanDetails(line)
        };
    }

    private static bool LooksLikeJoin(string lower) =>
        HasPlayerContext(lower) &&
        (ContainsWord(lower, "joined") || ContainsWord(lower, "connected") || lower.Contains("player registered", StringComparison.Ordinal)) &&
        !lower.Contains("backend", StringComparison.Ordinal) &&
        !lower.Contains("server connected", StringComparison.Ordinal) &&
        !lower.Contains("networking connected", StringComparison.Ordinal);

    private static bool LooksLikeLeave(string lower) =>
        HasPlayerContext(lower) &&
        (ContainsWord(lower, "left") || ContainsWord(lower, "disconnected") || lower.Contains("connection lost", StringComparison.Ordinal) || lower.Contains("player removed", StringComparison.Ordinal)) &&
        !lower.Contains("backend", StringComparison.Ordinal);

    private static bool LooksLikePlayerSnapshot(string lower) =>
        HasPlayerContext(lower) &&
        (lower.Contains("playerid", StringComparison.Ordinal) || lower.Contains("identityid", StringComparison.Ordinal)) &&
        IpRegex.IsMatch(lower);

    private static bool LooksLikeGameMasterEvent(string lower)
    {
        var gmContext = lower.Contains("game master", StringComparison.Ordinal) ||
                        lower.Contains("gamemaster", StringComparison.Ordinal) ||
                        lower.Contains("scr_editor", StringComparison.Ordinal) ||
                        lower.Contains("editormode", StringComparison.Ordinal) ||
                        Regex.IsMatch(lower, @"\bgm\b", RegexOptions.CultureInvariant);
        if (!gmContext)
            return false;

        return lower.Contains("enter", StringComparison.Ordinal) ||
               lower.Contains("leave", StringComparison.Ordinal) ||
               lower.Contains("spawn", StringComparison.Ordinal) ||
               lower.Contains("delete", StringComparison.Ordinal) ||
               lower.Contains("remove", StringComparison.Ordinal) ||
               lower.Contains("place", StringComparison.Ordinal) ||
               lower.Contains("teleport", StringComparison.Ordinal) ||
               lower.Contains("possess", StringComparison.Ordinal) ||
               lower.Contains("switch", StringComparison.Ordinal) ||
               lower.Contains("event", StringComparison.Ordinal) ||
               lower.Contains("action", StringComparison.Ordinal);
    }

    private static bool HasPlayerContext(string lower) =>
        lower.Contains("player", StringComparison.Ordinal) ||
        lower.Contains("client", StringComparison.Ordinal) ||
        lower.Contains("identity", StringComparison.Ordinal);

    private static bool ContainsWord(string value, string word) =>
        Regex.IsMatch(value, $@"\b{Regex.Escape(word)}\b", RegexOptions.CultureInvariant);

    private static (string Name, string Id, string Address) ExtractIdentity(string line)
    {
        var id = IdRegex.Match(line).Groups["id"].Value.Trim();
        var addressMatch = IpRegex.Match(line);
        var address = addressMatch.Success
            ? addressMatch.Groups["port"].Success
                ? $"{addressMatch.Groups["ip"].Value}:{addressMatch.Groups["port"].Value}"
                : addressMatch.Groups["ip"].Value
            : string.Empty;

        var name = NameAssignmentRegex.Match(line).Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name))
            name = QuotedNameRegex.Match(line).Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name))
            name = PlayerPrefixRegex.Match(line).Groups["name"].Value;

        return (CleanName(name), id, address);
    }

    private static string CleanName(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '\'', '"', ',', ';', ':', '[', ']');

    private static string CleanDetails(string line)
    {
        var colon = line.IndexOf(':');
        return colon >= 0 && colon < line.Length - 1 ? line[(colon + 1)..].Trim() : line.Trim();
    }
}
