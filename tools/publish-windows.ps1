[CmdletBinding()]
param([string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$dotnet=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$publishRoot=Join-Path $repoRoot '.local\release-preview'
$artifacts=Join-Path $repoRoot 'artifacts'
$app=Join-Path $publishRoot 'StardewCompanion'
$archive=Join-Path $artifacts 'StardewCompanion-preview-windows-x64.zip'
$workspace=([System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')+'\')
$approvedOutput=([System.IO.Path]::GetFullPath((Join-Path $repoRoot '.local')).TrimEnd('\')+'\')
if(-not $approvedOutput.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase) -or -not ([System.IO.Path]::GetFullPath($publishRoot)).StartsWith($approvedOutput,[StringComparison]::OrdinalIgnoreCase)) {throw 'Preview output path escaped the ignored local workspace.'}
if(Get-Process -Name 'StardewModdingAPI' -ErrorAction SilentlyContinue){throw '请先正常退出 AI 游戏实例。'}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-checks.ps1') -GamePath $GamePath
if($LASTEXITCODE -ne 0){throw '统一检查未通过，停止生成发布包。'}
if(Test-Path -LiteralPath $publishRoot){Remove-Item -LiteralPath $publishRoot -Recurse -Force}
New-Item -ItemType Directory -Path (Join-Path $app 'Mods\StardewCompanion.Bridge') -Force|Out-Null
& $dotnet publish (Join-Path $repoRoot 'src\StardewCompanion.Desktop\StardewCompanion.Desktop.csproj') -c Release -r win-x64 --self-contained true "-p:GamePath=$GamePath" -o $app
if($LASTEXITCODE -ne 0){throw 'Windows x64 desktop publish failed.'}
$mod=Join-Path $repoRoot 'src\StardewCompanion.Mod\bin\Release\net6.0'
foreach($file in @('StardewCompanion.Mod.dll','StardewCompanion.Protocol.dll','StardewCompanion.Fishing.dll','StardewMiningAgent.dll','manifest.json')){
    Copy-Item -LiteralPath (Join-Path $mod $file) -Destination (Join-Path $app 'Mods\StardewCompanion.Bridge') -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\玩家快速开始.md') -Destination $app -Force
@{
    endpoint='https://api.xiaomimimo.com/v1/chat/completions'
    model='mimo-v2.6-pro'
    apiKey='在 Companion 设置中填写；密钥会按当前 Windows 用户加密保存在本机。'
    gamePath=$GamePath
}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $app 'model-settings.example.json') -Encoding UTF8
$hashes=Get-ChildItem -LiteralPath $app -File -Recurse|Sort-Object FullName|ForEach-Object{
    $relative=$_.FullName.Substring($app.Length+1).Replace('\','/')
    [pscustomobject]@{path=$relative;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
}
$hashes|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $app 'SHA256SUMS.json') -Encoding UTF8
New-Item -ItemType Directory -Path $artifacts -Force|Out-Null
if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive -Force}
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256|Select-Object Algorithm,Hash,Path|ConvertTo-Json|Set-Content -LiteralPath ($archive+'.sha256.json') -Encoding UTF8
Write-Host "Preview package: $archive"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-package.ps1') -Archive $archive
if($LASTEXITCODE -ne 0){throw 'Packaged file and archive hash verification failed.'}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-preview.ps1') -Directory $app
if($LASTEXITCODE -ne 0){throw 'Self-contained desktop startup verification failed.'}
Write-Host '此包标记为预览版，游戏内 M0/M1/M2/M3 全部验收后再作为正式发布。'
