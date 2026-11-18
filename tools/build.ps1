[CmdletBinding()]
param([string]$GamePath = 'F:\SteamLibrary\steamapps\common\Stardew Valley')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Run tools/bootstrap.ps1 first.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
foreach ($project in @('StardewCompanion.Mod','StardewCompanion.Diagnostics','StardewCompanion.Desktop')) {
    & $dotnetPath build (Join-Path $repoRoot "src\$project\$project.csproj") -c Release "-p:GamePath=$GamePath"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
& $dotnetPath build (Join-Path $repoRoot 'tools\Fixture\Fixture.csproj') -c Release "-p:GamePath=$GamePath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed: SoloFiveDayFixture' }
$modTarget = Join-Path $repoRoot '.local\ai-mods\StardewCompanion.Bridge'
New-Item -ItemType Directory -Path $modTarget -Force | Out-Null
$modBuild = Join-Path $repoRoot 'src\StardewCompanion.Mod\bin\Release\net6.0'
foreach ($file in @('StardewCompanion.Mod.dll','StardewCompanion.Protocol.dll','StardewCompanion.Fishing.dll','StardewMiningAgent.dll','manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $modBuild $file) -Destination $modTarget -Force
}
Write-Host "M0 mod staged in $modTarget"
