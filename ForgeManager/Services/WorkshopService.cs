using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ForgeManager.Models;

namespace ForgeManager.Services;

public sealed class WorkshopService : IDisposable
{
    private static readonly Regex WorkshopLinkRegex = new(
        """href\s*=\s*["'](?:https://reforger\.armaplatform\.com)?/workshop/(?<id>[0-9A-Fa-f]{16})(?:-[^"'/?#]*)?["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OgImageRegex = new(
        """<meta[^>]+property\s*=\s*["']og:image["'][^>]+content\s*=\s*["'](?<url>[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex H1Regex = new(
        @"<h1[^>]*>(?<value>.*?)</h1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex TotalResultsRegex = new(
        @"Showing\s+[\d,]+\s+to\s+[\d,]+\s+of\s+(?<count>[\d,]+)\s+results",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;

    public WorkshopService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeManager/0.2.1 (+Official Workshop browser)");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<WorkshopMod> FetchAsync(
        string modId,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var normalizedId = NormalizeModId(modId);
        var url = $"https://reforger.armaplatform.com/workshop/{normalizedId}";
        if (!Regex.IsMatch(normalizedId, "^[0-9A-F]{16}$", RegexOptions.CultureInvariant))
            return Unavailable(normalizedId, url, "Invalid Workshop addon ID.");

        if (!forceRefresh &&
            _cache.TryGetValue(normalizedId, out var cached) &&
            DateTimeOffset.UtcNow - cached.StoredAt < CacheDuration)
        {
            return cached.Mod.Clone();
        }

        WorkshopMod result;
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                result = Unavailable(normalizedId, url, $"Workshop returned HTTP {(int)response.StatusCode}.");
            }
            else
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                result = ParseWorkshopPage(normalizedId, url, html);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            result = Unavailable(normalizedId, url, ex.Message);
        }

        _cache[normalizedId] = new CacheEntry(result.Clone(), DateTimeOffset.UtcNow);
        return result;
    }

    public async Task<IReadOnlyList<WorkshopMod>> FetchTreeAsync(
        IEnumerable<string> rootModIds,
        int maxDepth = 4,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var roots = rootModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeModId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new Dictionary<string, WorkshopMod>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, int Depth, bool Root)>();
        foreach (var root in roots)
            queue.Enqueue((root, 0, true));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = queue.Dequeue();
            if (results.TryGetValue(item.Id, out var existing))
            {
                if (item.Root)
                {
                    existing.IsRoot = true;
                    existing.IsConfigured = true;
                }
                continue;
            }

            var mod = await FetchAsync(item.Id, cancellationToken, forceRefresh);
            mod.IsRoot = item.Root;
            mod.IsConfigured = item.Root;
            results[item.Id] = mod;

            if (item.Depth >= maxDepth || !mod.Available)
                continue;

            foreach (var dependencyId in mod.DependencyIds)
                queue.Enqueue((dependencyId, item.Depth + 1, false));
        }

        return results.Values
            .OrderByDescending(mod => mod.IsRoot)
            .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<WorkshopBrowsePage> FetchBrowsePageAsync(
        string? search,
        int page,
        IEnumerable<string> configuredIds,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var query = new StringBuilder($"page={page}");
        if (!string.IsNullOrWhiteSpace(search))
            query.Append("&search=").Append(Uri.EscapeDataString(search.Trim()));

        var url = $"https://reforger.armaplatform.com/workshop?{query}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var ids = WorkshopLinkRegex.Matches(html)
            .Cast<Match>()
            .Select(match => match.Groups["id"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        var configured = configuredIds
            .Select(NormalizeModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var mod = await FetchAsync(id, cancellationToken);
                mod.IsConfigured = configured.Contains(mod.ModId);
                return mod;
            }
            finally
            {
                gate.Release();
            }
        });

        var mods = await Task.WhenAll(tasks);
        var totalMatch = TotalResultsRegex.Match(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));
        var totalResults = totalMatch.Success &&
                           int.TryParse(totalMatch.Groups["count"].Value.Replace(",", string.Empty), out var parsedTotal)
            ? parsedTotal
            : 0;

        return new WorkshopBrowsePage
        {
            Page = page,
            TotalResults = totalResults,
            Mods = mods
        };
    }

    public static string NormalizeModId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var match = Regex.Match(value, @"(?i)(?<id>[0-9A-F]{16})", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["id"].Value.ToUpperInvariant() : value.Trim().ToUpperInvariant();
    }

    private static WorkshopMod ParseWorkshopPage(string normalizedId, string url, string html)
    {
        var plainLines = ToPlainLines(html);
        var name = Decode(H1Regex.Match(html).Groups["value"].Value);
        if (string.IsNullOrWhiteSpace(name))
            name = ValueAfter(plainLines, "ID") is not null ? "Workshop addon" : "Unknown addon";

        var dependenciesSection = SliceAfter(html, "Dependencies");
        var dependencyIds = WorkshopLinkRegex.Matches(dependenciesSection)
            .Cast<Match>()
            .Select(match => match.Groups["id"].Value.ToUpperInvariant())
            .Where(id => !id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Uri? thumbnail = null;
        var imageMatch = OgImageRegex.Match(html);
        if (imageMatch.Success && Uri.TryCreate(WebUtility.HtmlDecode(imageMatch.Groups["url"].Value), UriKind.Absolute, out var parsed))
            thumbnail = parsed;

        return new WorkshopMod
        {
            ModId = normalizedId,
            Name = name,
            Author = ValueAfterPrefix(plainLines, "by ") ?? string.Empty,
            Version = ValueAfter(plainLines, "Version") ?? string.Empty,
            GameVersion = ValueAfter(plainLines, "Game Version") ?? string.Empty,
            Size = ValueAfter(plainLines, "Version size") ?? string.Empty,
            Downloads = PrefixValue("Downloads", ValueAfter(plainLines, "Downloads")),
            Subscribers = PrefixValue("Subscribers", ValueAfter(plainLines, "Subscribers")),
            Rating = PrefixValue("Rating", ValueAfter(plainLines, "Rating")),
            LastModified = ValueAfter(plainLines, "Last Modified") ?? string.Empty,
            Summary = ValueAfter(plainLines, "Summary") ?? string.Empty,
            WorkshopUrl = url,
            ThumbnailUri = thumbnail,
            DependencyIds = dependencyIds,
            Available = plainLines.Any(line => line.Equals(normalizedId, StringComparison.OrdinalIgnoreCase)) ||
                        html.Contains(normalizedId, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string PrefixValue(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label}: {value}";

    private static WorkshopMod Unavailable(string id, string url, string error) => new()
    {
        ModId = id,
        Name = "Unavailable addon",
        WorkshopUrl = url,
        Available = false,
        Error = error
    };

    private static string[] ToPlainLines(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var withLines = Regex.Replace(withoutScripts, @"</?(?:div|p|h\d|li|br|section|article|dt|dd)[^>]*>", "\n",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var text = Regex.Replace(withLines, "<[^>]+>", string.Empty, RegexOptions.CultureInvariant);
        return WebUtility.HtmlDecode(text)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string? ValueAfter(IReadOnlyList<string> lines, string label)
    {
        for (var index = 0; index < lines.Count - 1; index++)
        {
            if (lines[index].Equals(label, StringComparison.OrdinalIgnoreCase))
                return lines[index + 1];
        }

        return null;
    }

    private static string? ValueAfterPrefix(IEnumerable<string> lines, string prefix) =>
        lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();

    private static string SliceAfter(string html, string heading)
    {
        var index = html.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return string.Empty;

        var slice = html[index..];
        var footerIndex = slice.IndexOf("<footer", StringComparison.OrdinalIgnoreCase);
        return footerIndex > 0 ? slice[..footerIndex] : slice;
    }

    private static string Decode(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]+>", string.Empty)).Trim();

    public void Dispose() => _httpClient.Dispose();

    private sealed record CacheEntry(WorkshopMod Mod, DateTimeOffset StoredAt);
}
