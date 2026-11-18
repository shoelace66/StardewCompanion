[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$start.Arguments='"'+(Join-Path $repoRoot 'src\StardewCompanion.Desktop\bin\Release\net10.0-windows\StardewCompanion.dll')+'" --data "'+(Join-Path $repoRoot '.local')+'" --workspace "'+$repoRoot+'"'
$start.WorkingDirectory=$repoRoot
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle='Normal'
$process=[Diagnostics.Process]::Start($start)
[pscustomobject]@{ProcessId=$process.Id;Role='Desktop'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $repoRoot '.local\desktop-process.json') -Encoding UTF8
Write-Host "Desktop PID $($process.Id)"
