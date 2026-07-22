using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ForgeManager.Services;

public static class NetworkService
{
    public static IReadOnlyList<IPEndPoint> GetUdpListeners(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Where(endpoint => endpoint.Port == port)
                .DistinctBy(endpoint => endpoint.ToString())
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public static bool HasUdpListener(int port) => GetUdpListeners(port).Count > 0;

    public static string GetUdpListenerStatus(int port) =>
        FormatUdpListenerStatus(GetUdpListeners(port));

    public static string FormatUdpListenerStatus(IReadOnlyList<IPEndPoint> listeners) =>
        listeners.Count == 0
            ? "Not listening"
            : string.Join(", ", listeners.Select(endpoint => endpoint.Address + ":" + endpoint.Port));

    public static string GetLanIpv4Address()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                       .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))?
                       .ToString()
                   ?? "Unavailable";
        }
        catch
        {
            return "Unavailable";
        }
    }

    public static string ResolveLocalJoinAddress(
        int port,
        string? configuredBindAddress,
        string? preferredAddress,
        bool autoDetect,
        IReadOnlyList<IPEndPoint>? listeners = null,
        string? lanAddress = null)
    {
        if (!autoDetect && TryNormalizeIpv4(preferredAddress, out var manual))
            return manual;

        if (TryNormalizeIpv4(configuredBindAddress, out var configured) &&
            configured is not "0.0.0.0")
        {
            return configured;
        }

        listeners ??= GetUdpListeners(port);

        if (listeners.Any(endpoint => endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)))
            return IPAddress.Loopback.ToString();

        var loopback = listeners.FirstOrDefault(endpoint => IPAddress.IsLoopback(endpoint.Address));
        if (loopback is not null)
            return IPAddress.Loopback.ToString();

        var lan = string.IsNullOrWhiteSpace(lanAddress) ? GetLanIpv4Address() : lanAddress;
        var matchingLan = listeners.FirstOrDefault(endpoint =>
            endpoint.Address.AddressFamily == AddressFamily.InterNetwork &&
            endpoint.Address.ToString().Equals(lan, StringComparison.OrdinalIgnoreCase));
        if (matchingLan is not null)
            return matchingLan.Address.ToString();

        var firstIpv4 = listeners.FirstOrDefault(endpoint => endpoint.Address.AddressFamily == AddressFamily.InterNetwork);
        if (firstIpv4 is not null)
            return firstIpv4.Address.ToString();

        if (TryNormalizeIpv4(preferredAddress, out var preferred))
            return preferred;

        return lan != "Unavailable" ? lan : IPAddress.Loopback.ToString();
    }

    public static string GetLocalConnectionStatus(
        int port,
        string address,
        IReadOnlyList<IPEndPoint>? listeners = null)
    {
        listeners ??= GetUdpListeners(port);
        if (listeners.Count == 0)
            return "Server is not listening on the configured UDP port.";

        if (!IPAddress.TryParse(address, out var target))
            return "Join target is not a valid IP address.";

        var reachable = listeners.Any(endpoint =>
            endpoint.Address.Equals(IPAddress.Any) ||
            endpoint.Address.Equals(IPAddress.IPv6Any) ||
            endpoint.Address.Equals(target) ||
            IPAddress.IsLoopback(endpoint.Address) && IPAddress.IsLoopback(target));

        return reachable
            ? $"Local socket is available at {address}:{port}."
            : $"Server is listening, but not on {address}. Auto-detect should use the bound adapter address.";
    }

    public static async Task<int> AddFirewallRuleAsync(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"advfirewall firewall add rule name=\"ForgeManager Reforger UDP {port}\" dir=in action=allow protocol=UDP localport={port} profile=any"
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start netsh.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static void LaunchClient(string clientExecutablePath, string address, int port)
    {
        if (!File.Exists(clientExecutablePath))
            throw new FileNotFoundException("ArmaReforgerSteam.exe was not found. Set the client path in Settings.", clientExecutablePath);
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("The local join address is not a valid IPv4 address.");
        if (port != 2001)
            throw new InvalidOperationException("The command-line local client launcher uses Reforger's default UDP port 2001. Use the in-game IP Connect screen for a custom port.");

        var fullPath = Path.GetFullPath(clientExecutablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("-client");
        startInfo.ArgumentList.Add(address);
        Process.Start(startInfo);
    }

    private static bool TryNormalizeIpv4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        normalized = address.ToString();
        return true;
    }
}
