[CmdletBinding()]
param([string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley',[switch]$Autonomous,[switch]$BusinessLab,[switch]$DesktopFrontend,[ValidateRange(1,112)][int]$Days=5,[string]$SettingsFile='',[string]$InitialInstructionFile='')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(Get-NetTCPConnection -LocalPort 27841 -State Listen -ErrorAction SilentlyContinue){throw '27841 is occupied; stop the previous diagnostic run first'}
$runId='SC'+$Days+'D_'+(Get-Date -Format 'yyyyMMdd_HHmmss')
if(-not $Autonomous -and -not $BusinessLab -and $Days -ne 5){throw 'Scripted runner supports five days only; use -Autonomous for a configured duration'}
if($Autonomous){
    if([string]::IsNullOrWhiteSpace($InitialInstructionFile)){throw 'Autonomous tests require -InitialInstructionFile so the intended goal is explicit before startup'}
    $InitialInstructionFile=[IO.Path]::GetFullPath($InitialInstructionFile)
    if(-not(Test-Path -LiteralPath $InitialInstructionFile -PathType Leaf)){throw 'Initial instruction file was not found'}
}
if(-not $SettingsFile){
    if($Autonomous){
        $settingsRoot=Join-Path $repoRoot '.local/solo5'
        foreach($directory in (Get-ChildItem -LiteralPath $settingsRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)){
            $candidate=Join-Path $directory.FullName 'model.credentials.json'
            if(-not(Test-Path -LiteralPath $candidate)){continue}
            try{$configured=Get-Content -LiteralPath $candidate -Raw|ConvertFrom-Json;if($configured.model -eq 'deepseek-chat' -and -not [string]::IsNullOrWhiteSpace([string]$configured.ApiKeyDpapi)){$SettingsFile=$candidate;break}}catch{}
        }
        if(-not $SettingsFile){throw 'No configured DeepSeek profile is available; specify -SettingsFile'}
    }else{$SettingsFile=Join-Path $repoRoot '.local/model.credentials.json'}
}
if($Autonomous){
    if(-not(Test-Path -LiteralPath $SettingsFile)){throw 'Model settings are missing'}
    $configured=Get-Content -LiteralPath $SettingsFile -Raw|ConvertFrom-Json
    if($configured.model -ne 'deepseek-chat' -or [string]::IsNullOrWhiteSpace([string]$configured.ApiKeyDpapi)){throw 'Autonomous spring tests require a configured DeepSeek chat profile'}
}
if($DesktopFrontend -and -not(Test-Path -LiteralPath $SettingsFile -PathType Leaf)){throw 'Model settings are missing'}
$buildScript=Join-Path $PSScriptRoot 'build.ps1'
& $buildScript -GamePath $GamePath
if($LASTEXITCODE -ne 0){throw 'Build failed; isolated test was not started'}
$runRoot=Join-Path $repoRoot ('.local/solo5/'+$runId)
$mods=Join-Path $runRoot 'Mods'
$bridgeMod=Join-Path $mods 'StardewCompanion.Bridge'
& (Join-Path $PSScriptRoot 'stage-solo-mods.ps1') -ModsPath $mods
$runSettings=Join-Path $runRoot 'model.credentials.json'
if(Test-Path -LiteralPath $SettingsFile -PathType Leaf){Copy-Item -LiteralPath $SettingsFile -Destination $runSettings}
$sessionPath=Join-Path $runRoot 'session.json'
$dotnet=Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if($DesktopFrontend){
    $frontend=[Diagnostics.ProcessStartInfo]::new();$frontend.FileName=$dotnet
    $frontend.Arguments='"'+(Join-Path $repoRoot 'src/StardewCompanion.Desktop/bin/Release/net10.0-windows/StardewCompanion.dll')+'" --data "'+$runRoot+'"'+$(if($Autonomous){' --trial-days '+$Days}else{''})
    $frontend.UseShellExecute=$false;$frontend.CreateNoWindow=$false;$frontend.WindowStyle='Normal';$frontend.WorkingDirectory=$repoRoot
    $service=[Diagnostics.Process]::Start($frontend)
}else{
    $backend=[Diagnostics.ProcessStartInfo]::new();$backend.FileName=$dotnet
    $backend.Arguments='"'+(Join-Path $repoRoot 'src/StardewCompanion.Diagnostics/bin/Release/net10.0/StardewCompanion.Diagnostics.dll')+'" --agent --scripted'
    $backend.UseShellExecute=$false;$backend.CreateNoWindow=$true;$backend.WorkingDirectory=$repoRoot
    if($Autonomous){$backend.Arguments='"'+(Join-Path $repoRoot 'src/StardewCompanion.Diagnostics/bin/Release/net10.0/StardewCompanion.Diagnostics.dll')+'" --agent --trial-days '+$Days}
    $backend.EnvironmentVariables['SC_SESSION_FILE']=$sessionPath;$backend.EnvironmentVariables['SC_AGENT_DATA']=$runRoot
    $backend.EnvironmentVariables['SC_MODEL_SETTINGS']=$runSettings
    $service=[Diagnostics.Process]::Start($backend)
}
$deadline=[DateTime]::UtcNow.AddSeconds(15)
do{if(Test-Path -LiteralPath $sessionPath){break};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
if(-not(Test-Path -LiteralPath $sessionPath)){throw 'Solo test backend did not start'}
$session=Get-Content -LiteralPath $sessionPath -Raw|ConvertFrom-Json
$preferences=Join-Path $env:APPDATA 'StardewValley/startup_preferences'
if($DesktopFrontend -and (Test-Path -LiteralPath $preferences)){
    Copy-Item -LiteralPath $preferences -Destination (Join-Path $runRoot 'startup_preferences.original')
    [xml]$settings=Get-Content -LiteralPath $preferences -Raw
    foreach($name in @('windowedBorderlessFullscreen','fullscreen')){
        $node=$settings.SelectSingleNode('//'+$name);if($node){$node.InnerText='false'}
    }
    $settings.Save($preferences)
}
$game=[Diagnostics.ProcessStartInfo]::new();$game.FileName=Join-Path $GamePath 'StardewModdingAPI.exe'
$game.Arguments='--mods-path "'+$mods+'" --no-terminal';$game.WorkingDirectory=$GamePath;$game.UseShellExecute=$false;$game.WindowStyle='Normal'
foreach($key in @($game.EnvironmentVariables.Keys)){if($key.StartsWith('SC_')){$game.EnvironmentVariables.Remove($key)}}
$game.EnvironmentVariables['SC_AI_CLIENT']='1';$game.EnvironmentVariables['SC_BRIDGE_ENDPOINT']=$session.endpoint;$game.EnvironmentVariables['SC_BRIDGE_TOKEN']=$session.token
$game.EnvironmentVariables['SC_SOLO_TEST']='1';$game.EnvironmentVariables['SC_SOLO_TEST_NAME']=$runId;$game.EnvironmentVariables['SC_M0_FIXTURE']='solo5'
$game.EnvironmentVariables['SC_SOLO_TEST_EVIDENCE']=Join-Path $runRoot 'world.json'
if($BusinessLab){$game.EnvironmentVariables['SC_BUSINESS_LAB']='1';$game.EnvironmentVariables['SC_BUSINESS_LAB_CONTROL']=Join-Path $runRoot 'lab-control.json'}
$evidence=Join-Path $repoRoot '.local/business-validation.json'
if(Test-Path -LiteralPath $evidence){$game.EnvironmentVariables['SC_BUSINESS_EVIDENCE']=$evidence}
$client=[Diagnostics.Process]::Start($game)
@{runId=$runId;runRoot=$runRoot;servicePid=$service.Id;gamePid=$client.Id;sessionFile=$sessionPath;trialDays=$Days}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $repoRoot '.local/solo5-current.json') -Encoding UTF8
@{runId=$runId;trialDays=$Days;completedAtGameDay=$Days;mode=$(if($Autonomous){'deepseek_autonomous'}else{'scripted'});createdAt=[DateTimeOffset]::Now.ToString('o');modSha256=(Get-FileHash (Join-Path $bridgeMod 'StardewCompanion.Mod.dll')).Hash;coreSha256=(Get-FileHash (Join-Path $repoRoot 'src/StardewCompanion.Diagnostics/bin/Release/net10.0/StardewCompanion.Core.dll')).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runRoot 'trial.json') -Encoding UTF8
Write-Output "New solo test $runId; game PID $($client.Id); evidence $runRoot"
if($BusinessLab){exit 0}
elseif($Autonomous){& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'watch-solo-autonomy.ps1') -RunRoot $runRoot -Days $Days -InitialInstructionFile $InitialInstructionFile}
else{& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-solo-five-days.ps1') -RunRoot $runRoot}
exit $LASTEXITCODE
