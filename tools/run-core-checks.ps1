[CmdletBinding()]
param(
    [string]$DotnetPath,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotnetPath) {
    $DotnetPath = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $DotnetPath)) {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath -ErrorAction Stop).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath

# These projects use fake models and bridges. No game files, credentials or shared
# diagnostic service are required; the game-dependent checks remain in run-checks.
$projects = @(
    'tools\ToolChecks\ToolChecks.csproj',
    'tools\CoreChecks\CoreChecks.csproj',
    'src\StardewMiningAgent.Verification\StardewMiningAgent.Verification.csproj'
)
Push-Location $repoRoot
try {
    if (-not $NoBuild) {
        foreach ($project in $projects) {
            & $DotnetPath build $project -c Release
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
        }
    }
    $report = Join-Path $repoRoot '.local\checks\tool-contracts.json'
    & $DotnetPath run --project $projects[0] -c Release --no-build -- $report
    if ($LASTEXITCODE -ne 0) { throw 'Tool contract checks failed.' }
    & $DotnetPath run --project $projects[1] -c Release --no-build -- $repoRoot
    if ($LASTEXITCODE -ne 0) { throw 'Agent, permissions, memory and runtime checks failed.' }
    & $DotnetPath run --project $projects[2] -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Mining algorithm checks failed.' }
    Write-Host 'Core and tool checks passed. Live multiplayer/gameplay validation is separate.' -ForegroundColor Green
}
finally {
    Pop-Location
}
