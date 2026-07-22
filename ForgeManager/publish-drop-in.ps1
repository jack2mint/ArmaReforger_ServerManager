param(
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\ForgeManager\ForgeManager.csproj"
$serverRootFull = [System.IO.Path]::GetFullPath($ServerRoot)
$serverExe = Join-Path $serverRootFull "ArmaReforgerServer.exe"
$config = Join-Path $serverRootFull "configs\config.json"

if (-not (Test-Path $serverExe)) {
    throw "ArmaReforgerServer.exe was not found in $serverRootFull"
}
if (-not (Test-Path $config)) {
    throw "configs\config.json was not found in $serverRootFull"
}

$tempOut = Join-Path $root "publish\drop-in"
if (Test-Path $tempOut) { Remove-Item $tempOut -Recurse -Force }

dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $tempOut

Copy-Item (Join-Path $tempOut "ForgeManager.exe") $serverRootFull -Force
Write-Host "Installed drop-in manager to $serverRootFull" -ForegroundColor Green
Write-Host "It will auto-load $config" -ForegroundColor Green
