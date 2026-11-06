[CmdletBinding()]
param([string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$dotnet=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$app=Join-Path $repoRoot '.local\release-preview\StardewCompanion'
$appFull=[IO.Path]::GetFullPath($app)
$localFull=[IO.Path]::GetFullPath((Join-Path $repoRoot '.local'))
if(-not $appFull.StartsWith($localFull.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Publish directory must remain inside .local.'}
if(-not (Test-Path -LiteralPath $dotnet)){throw 'Run tools/bootstrap.ps1 first.'}
if(Test-Path -LiteralPath $appFull){Remove-Item -LiteralPath $appFull -Recurse -Force}
& $dotnet build (Join-Path $repoRoot 'src\StardewCompanion.Mod\StardewCompanion.Mod.csproj') -c Release "-p:GamePath=$GamePath"
if($LASTEXITCODE -ne 0){throw 'Mod build failed.'}
& $dotnet publish (Join-Path $repoRoot 'src\StardewCompanion.Desktop\StardewCompanion.Desktop.csproj') -c Release -r win-x64 --self-contained true "-p:GamePath=$GamePath" -o $app
if($LASTEXITCODE -ne 0){throw 'Desktop publish failed.'}
$mod=Join-Path $repoRoot 'src\StardewCompanion.Mod\bin\Release\net6.0'
$target=Join-Path $app 'Mods\StardewCompanion.Bridge'
New-Item -ItemType Directory -Path $target -Force|Out-Null
foreach($file in @('StardewCompanion.Mod.dll','StardewCompanion.Protocol.dll','manifest.json')){Copy-Item -LiteralPath (Join-Path $mod $file) -Destination (Join-Path $target $file) -Force}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\玩家快速开始.md') -Destination $app -Force
@{endpoint='https://api.xiaomimimo.com/v1/chat/completions';model='mimo-v2.6-pro';apiKey='在程序设置中填写';gamePath=$GamePath}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $app 'model-settings.example.json') -Encoding UTF8
$hashes=Get-ChildItem -LiteralPath $app -File -Recurse|Sort-Object FullName|ForEach-Object{[pscustomobject]@{path=$_.FullName.Substring($app.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}}
$hashes|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $app 'SHA256SUMS.json') -Encoding UTF8
$artifacts=Join-Path $repoRoot 'artifacts';New-Item -ItemType Directory -Path $artifacts -Force|Out-Null
$archive=Join-Path $artifacts 'StardewCompanion-preview-windows-x64.zip'
if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive -Force}
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256|Select-Object Algorithm,Hash,Path|ConvertTo-Json|Set-Content -LiteralPath ($archive+'.sha256.json') -Encoding UTF8
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-desktop-shortcut.ps1') -ApplicationDirectory $app
Write-Output "EXE: $(Join-Path $app 'StardewCompanion.exe')"
Write-Output "Archive: $archive"
Get-FileHash -LiteralPath $archive -Algorithm SHA256|Select-Object Algorithm,Hash
