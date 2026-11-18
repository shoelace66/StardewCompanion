[CmdletBinding()]
param(
 [int]$Port=27842,
 [string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley',
 [Parameter(Mandatory=$true)][string]$SettingsFile,
 [Parameter(Mandatory=$true)][string]$InstructionFile,
 [string]$ToolAcceptanceHostPath='',
 [switch]$NoPlayerGoal
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue){throw 'The selected independent test port is occupied'}
$settings=[IO.File]::ReadAllText([IO.Path]::GetFullPath($SettingsFile))|ConvertFrom-Json
if($settings.model -ne 'deepseek-chat' -or -not $settings.ApiKeyDpapi){throw 'Configured encrypted DeepSeek profile required'}
$instruction=[IO.File]::ReadAllText([IO.Path]::GetFullPath($InstructionFile))
if([string]::IsNullOrWhiteSpace($instruction)){throw 'Explicit test instruction required'}
$runId='SC112D_'+(Get-Date -Format yyyyMMdd_HHmmss)
$run=Join-Path $repo ('.local/solo5/'+$runId)
if(Test-Path -LiteralPath $run){throw 'Run directory already exists'}
$null=New-Item -ItemType Directory -Path $run
$privateProfile=Join-Path $run 'profile'
$privateRoaming=Join-Path $privateProfile 'AppData/Roaming'
$privateLocal=Join-Path $privateProfile 'AppData/Local'
New-Item -ItemType Directory -Path $privateRoaming,$privateLocal -Force|Out-Null
$mods=Join-Path $run 'Mods'
& (Join-Path $PSScriptRoot 'stage-solo-mods.ps1') -ModsPath $mods
$runSettings=Join-Path $run 'model.credentials.json'
Copy-Item -LiteralPath $SettingsFile -Destination $runSettings
$sessionFile=Join-Path $run 'session.json'
$hostDll=if($ToolAcceptanceHostPath){[IO.Path]::GetFullPath($ToolAcceptanceHostPath)}else{Join-Path $repo 'tools/ToolAcceptanceHost/bin/Release/net10.0/ToolAcceptanceHost.dll'}
if(-not(Test-Path $hostDll)){throw 'Build ToolAcceptanceHost before launching'}
$backend=[Diagnostics.ProcessStartInfo]::new()
$backend.FileName=Join-Path $repo '.tools/dotnet/dotnet.exe';$backend.Arguments='"'+$hostDll+'"'
$backend.UseShellExecute=$false;$backend.CreateNoWindow=$true;$backend.WindowStyle='Hidden';$backend.WorkingDirectory=$repo
$backend.EnvironmentVariables['SC_AGENT_DATA']=$run;$backend.EnvironmentVariables['SC_SESSION_FILE']=$sessionFile
$backend.EnvironmentVariables['SC_MODEL_SETTINGS']=$runSettings;$backend.EnvironmentVariables['SC_ACCEPTANCE_PORT']=[string]$Port
$service=[Diagnostics.Process]::Start($backend)
$deadline=[DateTime]::UtcNow.AddSeconds(20)
do{
 Start-Sleep -Milliseconds 200
 if($service.HasExited){throw 'Independent test backend exited'}
}while(-not(Test-Path $sessionFile) -and [DateTime]::UtcNow -lt $deadline)
if(-not(Test-Path $sessionFile)){throw 'Independent test session did not appear'}
$session=[IO.File]::ReadAllText($sessionFile)|ConvertFrom-Json
$control=Join-Path $run 'lab-control.json';$id=[guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($control,(@{id=$id;scene='clock_hold'}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$game=[Diagnostics.ProcessStartInfo]::new();$game.FileName=Join-Path $GamePath 'StardewModdingAPI.exe'
$game.Arguments='--mods-path "'+$mods+'" --no-terminal';$game.WorkingDirectory=$GamePath;$game.UseShellExecute=$false;$game.WindowStyle='Normal'
foreach($key in @($game.EnvironmentVariables.Keys)){if($key.StartsWith('SC_')){$game.EnvironmentVariables.Remove($key)}}
$environment=@{SC_AI_CLIENT='1';SC_BRIDGE_ENDPOINT=$session.endpoint;SC_BRIDGE_TOKEN=$session.token;SC_SOLO_TEST='1';SC_SOLO_TEST_NAME=$runId;SC_M0_FIXTURE='solo5';SC_SOLO_TEST_EVIDENCE=(Join-Path $run 'world.json');SC_BUSINESS_LAB='1';SC_BUSINESS_LAB_CONTROL=$control;USERPROFILE=$privateProfile;APPDATA=$privateRoaming;LOCALAPPDATA=$privateLocal}
foreach($key in $environment.Keys){$game.EnvironmentVariables[$key]=$environment[$key]}
$client=[Diagnostics.Process]::Start($game)
$metadata=@{runId=$runId;runRoot=$run;servicePid=$service.Id;gamePid=$client.Id;sessionFile=$sessionFile;trialDays=112;port=$Port;privateProfile=$privateProfile;privateAppData=$privateRoaming}
$metadata|ConvertTo-Json|Set-Content (Join-Path $run 'launch.json') -Encoding UTF8
@{runId=$runId;trialDays=112;mode='deepseek_tool_acceptance';createdAt=[DateTimeOffset]::Now.ToString('o');modSha256=(Get-FileHash (Join-Path $mods 'StardewCompanion.Bridge/StardewCompanion.Mod.dll')).Hash;coreSha256=(Get-FileHash (Join-Path (Split-Path $hostDll) 'StardewCompanion.Core.dll')).Hash;fixtureSha256=(Get-FileHash (Join-Path $mods 'SoloFiveDayFixture/Fixture.dll')).Hash}|ConvertTo-Json|Set-Content (Join-Path $run 'trial.json') -Encoding UTF8
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$','';$headers=@{Authorization='Bearer '+$session.token}
$deadline=[DateTime]::UtcNow.AddSeconds(150)
do{
 Start-Sleep -Milliseconds 500
 if($client.HasExited){throw 'The new independent game exited; existing processes were preserved'}
 try{$state=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 3}catch{$state=$null}
 if($state.WorldReady -and $state.TestRunId -eq $runId){break}
}while([DateTime]::UtcNow -lt $deadline)
if(-not $state.WorldReady -or $state.TestRunId -ne $runId -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer){throw 'Independent isolated game did not become ready in single-player test mode'}
$health=Invoke-RestMethod ($base+'/health') -Headers $headers -TimeoutSec 5
if(-not $health.modelEnabled){throw 'Acceptance host has DeepSeek planning disabled; refusing to start model-tool tests'}
$result=[IO.File]::ReadAllText(($control+'.result'))|ConvertFrom-Json
if($result.id -ne $id -or -not $result.clockControlSupported -or -not $result.clockHeld){throw 'New fixture clock hold not verified'}
@{verifiedAt=[DateTimeOffset]::Now.ToString('o');fixtureSha256=(Get-FileHash (Join-Path $mods 'SoloFiveDayFixture/Fixture.dll')).Hash;initialCommandId=$id}|ConvertTo-Json|Set-Content (Join-Path $run 'clock-control-enabled.json') -Encoding UTF8
if($result.nativeFactsSupported){Copy-Item -LiteralPath (Join-Path $run 'clock-control-enabled.json') -Destination (Join-Path $run 'native-facts-enabled.json')}
$body=@{objective=$instruction;completionCondition='逐工具验收，终态后等待下一用例，全部检查后统一修复';priorities=@('按用例调用','依据真实回执','终态后等待');restrictions=@('禁止矿洞','禁止自主经营');autonomy='仅执行当前验收要求';disabledSkills=@()}|ConvertTo-Json -Depth 5
if(-not $NoPlayerGoal){
 $null=Invoke-RestMethod ($base+'/agent/goal') -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 10
 $saved=Invoke-RestMethod ($base+'/agent/goal') -Headers $headers -TimeoutSec 5
 if($saved.objective -cne $instruction){throw 'Explicit goal verification failed'}
}else{
 $saved=Invoke-RestMethod ($base+'/agent/goal') -Headers $headers -TimeoutSec 5
 if(-not [string]::IsNullOrWhiteSpace($saved.objective)){throw 'Empty-goal test requires a clean scope'}
}
[IO.File]::WriteAllText((Join-Path $run 'initial-instruction.txt'),$instruction,[Text.UTF8Encoding]::new($false))
$metadata|ConvertTo-Json|Set-Content (Join-Path $repo '.local/solo5-current.json') -Encoding UTF8
[pscustomobject]@{runId=$runId;runRoot=$run;port=$Port;gamePid=$client.Id;servicePid=$service.Id;clockHeld=$true;agentResumed=$false;previousProcessesPreserved=$true}|ConvertTo-Json
