[CmdletBinding()]
param(
    [switch]$StaticOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "ForgeManager.sln"
$project = Join-Path $root "src\ForgeManager\ForgeManager.csproj"
$xaml = Join-Path $root "src\ForgeManager\MainWindow.xaml"
$codeBehind = Join-Path $root "src\ForgeManager\MainWindow.xaml.cs"
$sampleConfig = Join-Path $root "configs\config.minimal.json"
$artifactRoot = Join-Path $root "artifacts\verification"
$publishOut = Join-Path $artifactRoot "publish-win-x64"

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

Write-Step "Checking required files"
foreach ($path in @($solution, $project, $xaml, $codeBehind, $sampleConfig)) {
    Assert-True (Test-Path -LiteralPath $path) "Required file is missing: $path"
}

Write-Step "Parsing project, XAML, and JSON"
[xml](Get-Content -LiteralPath $project -Raw) | Out-Null
[xml](Get-Content -LiteralPath $xaml -Raw) | Out-Null
Get-Content -LiteralPath $sampleConfig -Raw | ConvertFrom-Json | Out-Null

$xamlText = Get-Content -LiteralPath $xaml -Raw
$codeText = Get-Content -LiteralPath $codeBehind -Raw

$nameMatches = [regex]::Matches($xamlText, 'x:Name="(?<name>[A-Za-z_][A-Za-z0-9_]*)"')
$duplicateNames = $nameMatches |
    ForEach-Object { $_.Groups['name'].Value } |
    Group-Object |
    Where-Object Count -gt 1
if ($duplicateNames) { throw ("Duplicate x:Name values: " + (($duplicateNames.Name) -join ', ')) }

$eventPattern = '(?<![A-Za-z0-9_:])(?:Click|Loaded|Closing|SelectionChanged|TextChanged|KeyDown|Checked|Unchecked|ValueChanged|MouseDoubleClick|MouseLeftButtonDown|SizeChanged|StateChanged|PreviewKeyDown)="(?<handler>[A-Za-z_][A-Za-z0-9_]*)"'
$handlers = [regex]::Matches($xamlText, $eventPattern) |
    ForEach-Object { $_.Groups['handler'].Value } |
    Sort-Object -Unique
$missingHandlers = foreach ($handler in $handlers) {
    if ($codeText -notmatch ("\b" + [regex]::Escape($handler) + "\s*\(")) { $handler }
}
if ($missingHandlers) { throw ("Missing XAML event handlers: " + ($missingHandlers -join ', ')) }

Assert-True ($codeText -notmatch '_logView\.DeferRefresh\(') `
    "Do not defer the log CollectionView while the bound ObservableCollection is being changed."

Write-Step "Checking responsive UI contract"
foreach ($requiredName in @(
    'MainTabs', 'DashboardKpiGrid', 'DashboardDetailsGrid', 'ActiveConfigCard',
    'DiagnosticsCard', 'HeaderHeroPanel', 'CurrentSectionTitle', 'WindowFrame')) {
    Assert-True ($xamlText -match ('x:Name="' + [regex]::Escape($requiredName) + '"')) `
        "Responsive UI control is missing: $requiredName"
}
foreach ($requiredStyle in @('MainNavigationTabControl', 'MainNavigationTabItem', 'ModernCard', 'FieldCard')) {
    Assert-True ($xamlText -match ('StaticResource ' + [regex]::Escape($requiredStyle))) `
        "Responsive UI style is not used: $requiredStyle"
}
Assert-True ($xamlText -match 'WindowChrome') "Borderless resize support is missing."
Assert-True ($codeText -match 'ApplyResponsiveLayout\(') "Responsive layout controller is missing."
Assert-True ($codeText -match 'MainWindow_PreviewKeyDown\(') "Keyboard navigation support is missing."
Assert-True ($xamlText -notmatch '(?i)(Background|Foreground|BorderBrush)="(?:White|#FFF(?:FFF)?|#FFFFFFFF)"') `
    "A pure-white UI surface was found. ForgeManager uses dark ForgeStudio surfaces only."

if ($StaticOnly) {
    Write-Step "Static verification complete"
    Write-Host "XAML/XML/JSON parsing: PASS" -ForegroundColor Green
    Write-Host "Named controls and event handlers: PASS" -ForegroundColor Green
    Write-Host "Responsive UI contract: PASS" -ForegroundColor Green
    return
}

Write-Step "Checking .NET SDK"
$dotnet = Get-Command dotnet -ErrorAction Stop
$versionText = (& $dotnet.Source --version).Trim()
$version = [version]($versionText.Split('-')[0])
Assert-True ($version.Major -ge 8) "ForgeManager requires .NET SDK 8 or newer. Found $versionText."
Write-Host "Using .NET SDK $versionText"

$allTextFiles = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.FullName -ne $PSCommandPath -and $_.Extension -in @('.cs', '.xaml', '.csproj', '.sln', '.ps1', '.md', '.txt', '.json') }
$staleBranding = $allTextFiles | Select-String -Pattern '\b(ServerManager|ForgeServerManager|ForgeStudio Server)\b'
if ($staleBranding) { throw "Stale pre-ForgeManager branding was found." }

Write-Step "Cleaning verification artifacts"
if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

Write-Step "Restoring dependencies and win-x64 publish assets"
& $dotnet.Source restore $project -r win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

Write-Step "Building Debug with warnings as errors"
& $dotnet.Source build $solution -c Debug --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Debug build failed with exit code $LASTEXITCODE." }

Write-Step "Building Release with warnings as errors"
& $dotnet.Source build $solution -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

Write-Step "Publishing self-contained single-file win-x64 build"
& $dotnet.Source publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:TreatWarningsAsErrors=true `
    -o $publishOut
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$publishedExe = Join-Path $publishOut "ForgeManager.exe"
$publishedSample = Join-Path $publishOut "configs\config.minimal.json"
Assert-True (Test-Path -LiteralPath $publishedExe) "Published ForgeManager.exe is missing."
Assert-True (Test-Path -LiteralPath $publishedSample) "Published configs\config.minimal.json is missing."

$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe)
Assert-True ($fileInfo.FileVersion -eq '0.2.1.0') "Unexpected published file version: $($fileInfo.FileVersion)"
Assert-True ($fileInfo.ProductName -eq 'ForgeManager') "Unexpected product name: $($fileInfo.ProductName)"

Write-Step "Verification complete"
Write-Host "Debug build: PASS" -ForegroundColor Green
Write-Host "Release build: PASS" -ForegroundColor Green
Write-Host "Single-file publish: PASS" -ForegroundColor Green
Write-Host "Published output: $publishOut" -ForegroundColor Green

Write-Host "Verification artifacts were retained for inspection. Use the published output above for testing." -ForegroundColor DarkGray
