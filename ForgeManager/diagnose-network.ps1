param(
    [int]$Port = 2001,
    [string]$OutputPath = ".\network-diagnostic.txt"
)

$ErrorActionPreference = "Continue"
$lines = [System.Collections.Generic.List[string]]::new()

function Add-Section([string]$Title) {
    $lines.Add("")
    $lines.Add("==== $Title ====")
}

$lines.Add("ForgeManager / Arma Reforger network diagnostic")
$lines.Add("Generated: $(Get-Date -Format o)")
$lines.Add("Computer: $env:COMPUTERNAME")
$lines.Add("Port: UDP $Port")

Add-Section "UDP endpoint"
$endpoint = Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
if ($endpoint) {
    $lines.Add(($endpoint | Format-Table -AutoSize | Out-String).TrimEnd())
} else {
    $lines.Add("No UDP listener was found on port $Port.")
}

Add-Section "Server process"
$processes = Get-CimInstance Win32_Process -Filter "Name='ArmaReforgerServer.exe'" -ErrorAction SilentlyContinue |
    Select-Object ProcessId, ExecutablePath, CommandLine
if ($processes) {
    $lines.Add(($processes | Format-List | Out-String).TrimEnd())
} else {
    $lines.Add("ArmaReforgerServer.exe is not running.")
}

Add-Section "IPv4 adapters"
$ip = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
    Where-Object { $_.IPv4Address } |
    Select-Object InterfaceAlias, InterfaceDescription, @{n='IPv4';e={$_.IPv4Address.IPAddress}}, @{n='Gateway';e={$_.IPv4DefaultGateway.NextHop}}
$lines.Add(($ip | Format-Table -AutoSize | Out-String).TrimEnd())

Add-Section "Matching firewall rules"
$rules = Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'Arma|Reforger|ForgeManager' } |
    Select-Object DisplayName, Enabled, Direction, Action, Profile
if ($rules) {
    $lines.Add(($rules | Format-Table -AutoSize | Out-String).TrimEnd())
} else {
    $lines.Add("No matching firewall rules were found.")
}

Add-Section "Interpretation"
$lines.Add("Public-list visibility proves backend registration, not local/public UDP reachability.")
$lines.Add("If no NETWORK lines appear in the server log during a join attempt, the client traffic did not reach the server socket.")
$lines.Add("If NETWORK lines appear and then the player is removed, inspect addon/version/session errors instead of the router.")

$fullPath = [System.IO.Path]::GetFullPath($OutputPath)
$lines | Set-Content -LiteralPath $fullPath -Encoding UTF8
Write-Host "Diagnostic saved to $fullPath" -ForegroundColor Green
