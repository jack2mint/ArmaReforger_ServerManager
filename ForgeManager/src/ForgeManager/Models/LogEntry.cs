using System.Text.RegularExpressions;

namespace ForgeManager.Models;

public sealed class LogEntry
{
    private static readonly Regex CategoryRegex = new(
        @"^\s*(?<category>[A-Z][A-Z0-9_ ]{1,20}?)\s*(?:\((?<marker>[EW])\))?\s*:\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorKeywordRegex = new(
        @"\b(error|failed|failure|fatal|exception|unable|refused|invalid)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WarningKeywordRegex = new(
        @"\b(warn|warning|obsolete|deprecated)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Category { get; init; } = "OTHER";
    public string Severity { get; init; } = "INFO";
    public string Message { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;

    public static LogEntry Parse(string line, bool errorStream = false)
    {
        var clean = line.TrimEnd();
        var match = CategoryRegex.Match(clean);
        var category = match.Success
            ? Regex.Replace(match.Groups["category"].Value.Trim(), @"\s+", " ")
            : "OTHER";
        var message = match.Success ? match.Groups["message"].Value.Trim() : clean.Trim();
        var marker = match.Success ? match.Groups["marker"].Value : string.Empty;

        // Reforger writes a mixture of normal, warning, and error output to stderr. Explicit
        // (E)/(W) markers and message content are more reliable than the stream alone.
        var severity = marker switch
        {
            "E" => "ERROR",
            "W" => "WARNING",
            _ when ErrorKeywordRegex.IsMatch(message) => "ERROR",
            _ when WarningKeywordRegex.IsMatch(message) => "WARNING",
            _ when errorStream && !match.Success => "ERROR",
            _ => "INFO"
        };

        return new LogEntry
        {
            Category = string.IsNullOrWhiteSpace(category) ? "OTHER" : category,
            Severity = severity,
            Message = message,
            Raw = clean
        };
    }
}
