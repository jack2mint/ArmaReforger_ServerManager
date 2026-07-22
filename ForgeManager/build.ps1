$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "ForgeManager.sln"

dotnet restore $solution
dotnet build $solution -c Release --no-restore

Write-Host "Build complete." -ForegroundColor Green
