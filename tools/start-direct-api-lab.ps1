[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$Port,
    [string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley',
    [string]$RunId='',
    [string]$NoModelHostPath=''
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue){throw "Test port $Port is occupied"}
if(-not $RunId){$RunId='SC112D_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+([guid]::NewGuid().ToString('N').Substring(0,6))}
$probe=[System.Reflection.Assembly]::LoadFrom((Join-Path $repo 'src/StardewCompanion.Protocol/bin/Release/netstandard2.0/StardewCompanion.Protocol.dll'))
if(-not $probe.GetType('StardewCompanion.Protocol.ControlModePolicy').GetMethod('IsSoloTestRunId').Invoke($null,@($RunId))){throw "Invalid isolated run id: $RunId"}
$run=Join-Path $repo ('.local/solo5/'+$RunId)
if(Test-Path -LiteralPath $run){throw "Run directory already exists: $run"}
$null=New-Item -ItemType Directory -Path $run
$privateProfile=Join-Path $run 'profile'
$privateRoaming=Join-Path $privateProfile 'AppData/Roaming'
$privateLocal=Join-Path $privateProfile 'AppData/Local'
New-Item -ItemType Directory -Path $privateRoaming,$privateLocal -Force|Out-Null
$mods=Join-Path $run 'Mods'
& (Join-Path $PSScriptRoot 'stage-solo-mods.ps1') -ModsPath $mods
$sessionFile=Join-Path $run 'session.json'
$settings=Join-Path $repo '.local/model.credentials.json'
if(-not(Test-Path -LiteralPath $settings -PathType Leaf)){throw 'Local model settings file required by ModelSettings.Load is missing'}
$hostDll=if($NoModelHostPath){[IO.Path]::GetFullPath($NoModelHostPath)}else{Join-Path $repo '.local/no-model-host/bin/Release/net10.0/NoModelHost.dll'}
if(-not(Test-Path -LiteralPath $hostDll -PathType Leaf)){throw 'Build the local no-model host before launching this test lab'}
$hostInfo=[Diagnostics.ProcessStartInfo]::new()
$hostInfo.FileName=Join-Path $repo '.tools/dotnet/dotnet.exe';$hostInfo.Arguments='"'+$hostDll+'"'
$hostInfo.UseShellExecute=$false;$hostInfo.CreateNoWindow=$true;$hostInfo.WindowStyle='Hidden';$hostInfo.WorkingDirectory=$repo
$hostInfo.EnvironmentVariables['SC_AGENT_DATA']=$run
$hostInfo.EnvironmentVariables['SC_SESSION_FILE']=$sessionFile
$hostInfo.EnvironmentVariables['SC_MODEL_SETTINGS']=$settings
$hostInfo.EnvironmentVariables['SC_ACCEPTANCE_PORT']=[string]$Port
$service=$hostProcess=[Diagnostics.Process]::Start($hostInfo)
$deadline=[DateTime]::UtcNow.AddSeconds(20)
do{Start-Sleep -Milliseconds 200;if($service.HasExited){throw 'No-model host exited during startup'}}while(-not(Test-Path $sessionFile) -and [DateTime]::UtcNow -lt $deadline)
if(-not(Test-Path $sessionFile)){throw 'No-model host did not write session file'}
$session=Get-Content -LiteralPath $sessionFile -Raw|ConvertFrom-Json
$control=Join-Path $run 'lab-control.json'
$id=[guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($control,(@{id=$id;scene='clock_hold'}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$game=[Diagnostics.ProcessStartInfo]::new()
$game.FileName=Join-Path $GamePath 'StardewModdingAPI.exe';$game.Arguments='--mods-path "'+$mods+'" --no-terminal'
$game.WorkingDirectory=$GamePath;$game.UseShellExecute=$false;$game.WindowStyle='Normal'
foreach($key in @($game.EnvironmentVariables.Keys)){if($key.StartsWith('SC_')){$game.EnvironmentVariables.Remove($key)}}
$gameEnvironment=@{
    SC_AI_CLIENT='1';SC_BRIDGE_ENDPOINT=$session.endpoint;SC_BRIDGE_TOKEN=$session.token
    SC_SOLO_TEST='1';SC_SOLO_TEST_NAME=$RunId;SC_M0_FIXTURE='solo5'
    SC_SOLO_TEST_EVIDENCE=(Join-Path $run 'world.json');SC_BUSINESS_LAB='1'
    SC_BUSINESS_LAB_CONTROL=$control;USERPROFILE=$privateProfile;APPDATA=$privateRoaming;LOCALAPPDATA=$privateLocal
}
foreach($entry in $gameEnvironment.GetEnumerator()){$game.EnvironmentVariables[$entry.Key]=$entry.Value}
$client=[Diagnostics.Process]::Start($game)
$metadata=@{runId=$RunId;runRoot=$run;servicePid=$service.Id;gamePid=$client.Id;sessionFile=$sessionFile;port=$Port;privateProfile=$privateProfile;privateAppData=$privateRoaming}
$metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'launch.json') -Encoding UTF8
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$headers=@{Authorization='Bearer '+$session.token}
$deadline=[DateTime]::UtcNow.AddSeconds(150);$state=$null
do{
    Start-Sleep -Milliseconds 500
    if($client.HasExited){throw 'Isolated game exited before becoming ready'}
    try{$state=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 3}catch{$state=$null}
}while((!$state -or !$state.WorldReady -or $state.TestRunId -ne $RunId) -and [DateTime]::UtcNow -lt $deadline)
if(!$state -or !$state.WorldReady -or $state.TestRunId -ne $RunId -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer){throw 'Isolated single-player test game did not become ready'}
$health=Invoke-RestMethod ($base+'/health') -Headers $headers -TimeoutSec 5
if($health.modelEnabled){throw 'Refusing test dispatch: no-model host unexpectedly enabled planning'}
$deadline=[DateTime]::UtcNow.AddSeconds(30);$fixture=$null
do{
    Start-Sleep -Milliseconds 300
    if(Test-Path ($control+'.result')){try{$fixture=Get-Content ($control+'.result') -Raw|ConvertFrom-Json}catch{}}
}while((!$fixture -or $fixture.id -ne $id) -and [DateTime]::UtcNow -lt $deadline)
if(!$fixture -or $fixture.id -ne $id -or !$fixture.clockHeld){throw 'Isolated fixture clock hold was not verified'}
[pscustomobject]@{runId=$RunId;runRoot=$run;port=$Port;gamePid=$client.Id;servicePid=$service.Id;modelEnabled=$false;clockHeld=$true;saveId=$state.SaveId;modSha256=(Get-FileHash -LiteralPath (Join-Path $mods 'StardewCompanion.Bridge/StardewCompanion.Mod.dll')).Hash}|ConvertTo-Json


