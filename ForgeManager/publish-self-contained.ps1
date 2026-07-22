$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\ForgeManager\ForgeManager.csproj"
$out = Join-Path $root "publish\win-x64"

dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $out

Write-Host "Published to $out" -ForegroundColor Green
