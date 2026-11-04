[CmdletBinding()]
param([string]$RunRoot='', [string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley',[switch]$RestartGoal,[switch]$ResetAgentMemory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$currentPath=Join-Path $repo '.local/solo5-current.json'
$current=Get-Content -LiteralPath $currentPath -Raw|ConvertFrom-Json
if(-not $RunRoot){$RunRoot=$current.runRoot}
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$runId=Split-Path -Leaf $RunRoot
$world=Get-Content -LiteralPath (Join-Path $RunRoot 'world.json') -Raw|ConvertFrom-Json
$trial=Get-Content -LiteralPath (Join-Path $RunRoot 'trial.json') -Raw|ConvertFrom-Json
if($world.runId -ne $runId -or $current.runId -ne $runId){throw 'Resume identity does not match the recorded run'}
$save=[IO.Path]::GetFullPath($world.folder)
$savesRoot=[IO.Path]::GetFullPath((Join-Path $env:APPDATA 'StardewValley/Saves'))
if(-not $save.StartsWith($savesRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Save folder is outside the native saves directory'}
$folder=Split-Path -Leaf $save
$saveFile=Join-Path $save $folder
$saveXml=[xml](Get-Content -LiteralPath $saveFile -Raw)
if($saveXml.SaveGame.uniqueIDForThisGame -ne $world.saveId -or $saveXml.SaveGame.player.name -ne 'SC5Host' -or $saveXml.SaveGame.player.farmName -ne $runId){throw 'Existing save identity does not match the isolated test'}
if(Get-Process -Name StardewModdingAPI,'Stardew Valley' -ErrorAction SilentlyContinue){throw 'A game is still open; resume requires its native save to be closed first'}
Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*StardewCompanion.dll*--monitor*' -and $_.CommandLine.Contains($RunRoot)} | ForEach-Object {
 $monitor=Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
 if($monitor){$null=$monitor.CloseMainWindow();if(-not $monitor.WaitForExit(3000)){Stop-Process -Id $monitor.Id -Force}}
}
$session=Get-Content -LiteralPath (Join-Path $RunRoot 'session.json') -Raw|ConvertFrom-Json
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$service=Get-CimInstance Win32_Process -Filter ('ProcessId='+$current.servicePid)
if($service){
 if($service.Name -ne 'dotnet.exe' -or $service.CommandLine -notlike '*StardewCompanion.Diagnostics.dll*--agent*'){throw 'Stored backend PID belongs to a different process'}
 $null=Invoke-RestMethod ($base+'/agent/pause') -Headers @{Authorization='Bearer '+$session.token} -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 10
 Stop-Process -Id $service.ProcessId -Force
}
Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*-File*watch-solo-autonomy.ps1*' -and $_.CommandLine.Contains($RunRoot)} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
$backup=Join-Path $repo ('.local/solo-resume-backups/'+$runId+'/'+$stamp)
New-Item -ItemType Directory -Path $backup -Force|Out-Null
Copy-Item -LiteralPath $save -Destination (Join-Path $backup 'native-save') -Recurse
Copy-Item -LiteralPath $RunRoot -Destination (Join-Path $backup 'agent-data') -Recurse
if($ResetAgentMemory){
 $memoryArchive=Join-Path $backup 'retired-memory'
 New-Item -ItemType Directory -Path $memoryArchive -Force|Out-Null
 foreach($suffix in @('memory.db','memory.db-wal','memory.db-shm')){
  $source=Join-Path $RunRoot $suffix
  if(Test-Path -LiteralPath $source){Move-Item -LiteralPath $source -Destination $memoryArchive}
 }
}
$dotnet=Join-Path $repo '.tools/dotnet/dotnet.exe'
foreach($project in @('src/StardewCompanion.Diagnostics/StardewCompanion.Diagnostics.csproj','src/StardewCompanion.Mod/StardewCompanion.Mod.csproj','tools/Fixture/Fixture.csproj','src/StardewCompanion.Desktop/StardewCompanion.Desktop.csproj')){
 & $dotnet build (Join-Path $repo $project) -c Release --no-restore "-p:GamePath=$GamePath"
 if($LASTEXITCODE -ne 0){throw ('Resume build failed: '+$project)}
}
$mods=Join-Path $RunRoot 'Mods'
foreach($file in @('StardewCompanion.Mod.dll','StardewCompanion.Protocol.dll','manifest.json')){Copy-Item -LiteralPath (Join-Path $repo ('src/StardewCompanion.Mod/bin/Release/net6.0/'+$file)) -Destination (Join-Path $mods 'StardewCompanion.Bridge') -Force}
foreach($file in @('Fixture.dll','StardewCompanion.Protocol.dll','manifest.json')){Copy-Item -LiteralPath (Join-Path $repo ('tools/Fixture/bin/Release/net6.0/'+$file)) -Destination (Join-Path $mods 'SoloFiveDayFixture') -Force}
$sessionPath=Join-Path $RunRoot 'session.json'
$backend=[Diagnostics.ProcessStartInfo]::new()
$backend.FileName=$dotnet;$backend.Arguments='"'+(Join-Path $repo 'src/StardewCompanion.Diagnostics/bin/Release/net10.0/StardewCompanion.Diagnostics.dll')+'" --agent --trial-days '+$trial.trialDays
$backend.UseShellExecute=$false;$backend.CreateNoWindow=$true;$backend.WindowStyle='Hidden';$backend.WorkingDirectory=$repo
$backend.EnvironmentVariables['SC_SESSION_FILE']=$sessionPath;$backend.EnvironmentVariables['SC_AGENT_DATA']=$RunRoot
$backend.EnvironmentVariables['SC_MODEL_SETTINGS']=Join-Path $repo '.local/model.credentials.json'
$newService=[Diagnostics.Process]::Start($backend)
$deadline=[DateTime]::UtcNow.AddSeconds(20)
do{
 Start-Sleep -Milliseconds 200
 if($newService.HasExited){throw 'Updated backend exited before accepting connections'}
 try{$null=Invoke-RestMethod ($base+'/agent/state') -Headers @{Authorization='Bearer '+$session.token} -TimeoutSec 1;$ready=$true}catch{}
}while(-not $ready -and [DateTime]::UtcNow -lt $deadline)
if(-not $ready){throw 'Updated backend did not accept authenticated agent requests'}
$session=Get-Content -LiteralPath $sessionPath -Raw|ConvertFrom-Json
$game=[Diagnostics.ProcessStartInfo]::new()
$game.FileName=Join-Path $GamePath 'StardewModdingAPI.exe';$game.Arguments='--mods-path "'+$mods+'" --no-terminal'
$game.UseShellExecute=$false;$game.WorkingDirectory=$GamePath;$game.WindowStyle='Normal'
foreach($key in @($game.EnvironmentVariables.Keys)){if($key.StartsWith('SC_')){$game.EnvironmentVariables.Remove($key)}}
$game.EnvironmentVariables['SC_AI_CLIENT']='1';$game.EnvironmentVariables['SC_BRIDGE_ENDPOINT']=$session.endpoint;$game.EnvironmentVariables['SC_BRIDGE_TOKEN']=$session.token
$game.EnvironmentVariables['SC_SOLO_TEST']='1';$game.EnvironmentVariables['SC_SOLO_TEST_NAME']=$runId;$game.EnvironmentVariables['SC_M0_FIXTURE']='solo5'
$game.EnvironmentVariables['SC_SOLO_RESUME_SAVE']=$folder;$game.EnvironmentVariables['SC_SOLO_TEST_EVIDENCE']=Join-Path $RunRoot 'world.json'
$evidence=Join-Path $repo '.local/business-validation.json'
if(Test-Path -LiteralPath $evidence){$game.EnvironmentVariables['SC_BUSINESS_EVIDENCE']=$evidence}
$client=[Diagnostics.Process]::Start($game)
$watch=[Diagnostics.ProcessStartInfo]::new();$watch.FileName='powershell.exe'
$watch.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'watch-solo-autonomy.ps1')+'" -RunRoot "'+$RunRoot+'" -Days '+$trial.trialDays+' -Resume'+$(if($RestartGoal){' -RestartGoal'}else{''})
$watch.UseShellExecute=$false;$watch.CreateNoWindow=$true;$watch.WindowStyle='Hidden';$watch.WorkingDirectory=$repo
$watcher=[Diagnostics.Process]::Start($watch)
@{runId=$runId;runRoot=$RunRoot;servicePid=$newService.Id;gamePid=$client.Id;watcherPid=$watcher.Id;sessionFile=$sessionPath;trialDays=$trial.trialDays;resumedAt=[DateTimeOffset]::Now.ToString('o');backup=$backup}|ConvertTo-Json|Set-Content -LiteralPath $currentPath -Encoding UTF8
$segment=@{resumedAt=[DateTimeOffset]::Now.ToString('o');saveId=$world.saveId;runId=$runId;saveFolder=$folder;nativeDay=$saveXml.SaveGame.dayOfMonth;targetDays=$trial.trialDays;backup=$backup;startup='native existing save; persisted original objective; no additional gameplay instructions';
 modSha256=(Get-FileHash -LiteralPath (Join-Path $mods 'StardewCompanion.Bridge/StardewCompanion.Mod.dll')).Hash;coreSha256=(Get-FileHash -LiteralPath (Join-Path $repo 'src/StardewCompanion.Diagnostics/bin/Release/net10.0/StardewCompanion.Core.dll')).Hash}
[IO.File]::AppendAllText((Join-Path $RunRoot 'resumptions.jsonl'),(($segment|ConvertTo-Json -Compress)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'start-decision-monitor.ps1') -SessionFile $sessionPath
Write-Output ('Resuming '+$runId+' from native spring day '+$saveXml.SaveGame.dayOfMonth+'; backup '+$backup)
