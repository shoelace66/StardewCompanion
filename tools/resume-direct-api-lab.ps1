[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RunRoot,
    [string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley'
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$runId=Split-Path -Leaf $RunRoot
$launchPath=Join-Path $RunRoot 'launch.json'
$worldPath=Join-Path $RunRoot 'world.json'
$sessionPath=Join-Path $RunRoot 'session.json'
foreach($path in @($launchPath,$worldPath,$sessionPath)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Required run file missing: $path"}}
$launch=Get-Content -LiteralPath $launchPath -Raw|ConvertFrom-Json
$world=Get-Content -LiteralPath $worldPath -Raw|ConvertFrom-Json
$session=Get-Content -LiteralPath $sessionPath -Raw|ConvertFrom-Json
if($launch.runId -ne $runId -or $world.runId -ne $runId){throw 'Run identity mismatch'}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$headers=@{Authorization='Bearer '+$session.token}
$service=$null
try{$health=Invoke-RestMethod ($base+'/health') -Headers $headers -TimeoutSec 5}catch{
    $hostDll=Join-Path $repo '.local/no-model-host/bin/Release/net10.0/NoModelHost.dll'
    $settings=Join-Path $repo '.local/model.credentials.json'
    if(-not(Test-Path -LiteralPath $hostDll) -or -not(Test-Path -LiteralPath $settings)){throw 'No-model host prerequisites are missing'}
    $hostInfo=[Diagnostics.ProcessStartInfo]::new()
    $hostInfo.FileName=Join-Path $repo '.tools/dotnet/dotnet.exe';$hostInfo.Arguments='"'+$hostDll+'"'
    $hostInfo.UseShellExecute=$false;$hostInfo.CreateNoWindow=$true;$hostInfo.WindowStyle='Hidden';$hostInfo.WorkingDirectory=$repo
    $hostInfo.EnvironmentVariables['SC_AGENT_DATA']=$RunRoot
    $hostInfo.EnvironmentVariables['SC_SESSION_FILE']=$sessionPath
    $hostInfo.EnvironmentVariables['SC_MODEL_SETTINGS']=$settings
    $hostInfo.EnvironmentVariables['SC_ACCEPTANCE_PORT']=[string]$launch.port
    $service=[Diagnostics.Process]::Start($hostInfo)
    $deadline=[DateTime]::UtcNow.AddSeconds(25)
    do{Start-Sleep -Milliseconds 300;try{$health=Invoke-RestMethod ($base+'/health') -Headers $headers -TimeoutSec 2}catch{$health=$null}}while(!$health -and [DateTime]::UtcNow -lt $deadline)
    if(!$health){throw 'No-model host failed to restart'}
    $launch|Add-Member -Force -NotePropertyName servicePid -NotePropertyValue $service.Id
    $launch|ConvertTo-Json|Set-Content -LiteralPath $launchPath -Encoding utf8
}
if($health.modelEnabled){throw 'Refusing to resume: model planning must remain disabled'}
$allGames=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'StardewModdingAPI.exe' -and $_.CommandLine -match 'StardewModdingAPI.exe'})
$outside=@($allGames|Where-Object {$_.CommandLine -notlike '*--mods-path*\.local\solo5\*'})
if($outside.Count){throw 'An unisolated Stardew process is already open'}
if(@($allGames|Where-Object {$_.CommandLine -like ('*'+$RunRoot+'*')}).Count){throw 'This isolated run already has a game process'}
if($allGames.Count -ge 4){throw 'Four isolated test game processes are already open'}
$save=[IO.Path]::GetFullPath($world.folder)
$privateProfile=if($launch.privateProfile){[IO.Path]::GetFullPath($launch.privateProfile)}else{Join-Path $RunRoot 'profile'}
$privateRoaming=if($launch.privateAppData){[IO.Path]::GetFullPath($launch.privateAppData)}else{Join-Path $privateProfile 'AppData/Roaming'}
if(-not $save.StartsWith(($privateRoaming.TrimEnd('\')+'\StardewValley\Saves\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Isolated save path is outside the run profile'}
$saveName=Split-Path -Leaf $save
$saveFile=Join-Path $save $saveName
if(-not(Test-Path -LiteralPath $saveFile -PathType Leaf)){throw 'Isolated save file is missing'}
$saveXml=[xml](Get-Content -LiteralPath $saveFile -Raw)
if($saveXml.SaveGame.player.name -ne 'SC5Host' -or $saveXml.SaveGame.player.farmName -ne $runId){throw 'Isolated save identity mismatch'}
$mods=Join-Path $RunRoot 'Mods'
& (Join-Path $PSScriptRoot 'stage-solo-mods.ps1') -ModsPath $mods
$privateLocal=Join-Path $privateProfile 'AppData/Local'
$control=Join-Path $RunRoot 'lab-control.json'
$game=[Diagnostics.ProcessStartInfo]::new()
$game.FileName=Join-Path $GamePath 'StardewModdingAPI.exe';$game.Arguments='--mods-path "'+$mods+'" --no-terminal'
$game.WorkingDirectory=$GamePath;$game.UseShellExecute=$false;$game.WindowStyle='Normal'
foreach($key in @($game.EnvironmentVariables.Keys)){if($key.StartsWith('SC_')){$game.EnvironmentVariables.Remove($key)}}
$environment=@{
    SC_AI_CLIENT='1';SC_BRIDGE_ENDPOINT=$session.endpoint;SC_BRIDGE_TOKEN=$session.token
    SC_SOLO_TEST='1';SC_SOLO_TEST_NAME=$runId;SC_M0_FIXTURE='solo5'
    SC_SOLO_RESUME_SAVE=$saveName;SC_SOLO_TEST_EVIDENCE=$worldPath
    SC_BUSINESS_LAB='1';SC_BUSINESS_LAB_CONTROL=$control
    USERPROFILE=$privateProfile;APPDATA=$privateRoaming;LOCALAPPDATA=$privateLocal
}
foreach($entry in $environment.GetEnumerator()){$game.EnvironmentVariables[$entry.Key]=$entry.Value}
$client=[Diagnostics.Process]::Start($game)
$deadline=[DateTime]::UtcNow.AddSeconds(150);$state=$null
do{
    Start-Sleep -Milliseconds 500
    if($client.HasExited){throw 'Isolated game exited before reconnecting'}
    try{$state=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 3}catch{$state=$null}
}while((!$state -or !$state.WorldReady -or $state.TestRunId -ne $runId) -and [DateTime]::UtcNow -lt $deadline)
if(!$state -or !$state.WorldReady -or $state.TestRunId -ne $runId -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer){throw 'Isolated game did not reconnect in solo test mode'}
$health=Invoke-RestMethod ($base+'/health') -Headers $headers -TimeoutSec 5
if($health.modelEnabled){throw 'Model planning enabled during resumed game'}
$record=[pscustomobject]@{resumedAt=[DateTimeOffset]::Now.ToString('o');runId=$runId;port=$launch.port;gamePid=$client.Id;modelEnabled=$false;saveId=$state.SaveId;location=$state.Location;modSha256=(Get-FileHash -LiteralPath (Join-Path $mods 'StardewCompanion.Bridge/StardewCompanion.Mod.dll')).Hash}
[IO.File]::AppendAllText((Join-Path $RunRoot 'direct-resumptions.jsonl'),(($record|ConvertTo-Json -Compress)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
$record|ConvertTo-Json
