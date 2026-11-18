[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[Parameter(Mandatory=$true)][string]$RequestFile,[switch]$AllowBusyScene)
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$request=[IO.File]::ReadAllText([IO.Path]::GetFullPath($RequestFile))|ConvertFrom-Json
if(-not $request.scene){throw 'A named isolated fixture scene is required'}
$session=[IO.File]::ReadAllText((Join-Path $RunRoot 'session.json'))|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$session.token}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$state=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 5
if(-not $state.WorldReady -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer -or $state.TestRunId -ne (Split-Path -Leaf $RunRoot) -or $state.PlayerName -ne 'SC5Host'){throw 'Isolated fixture identity mismatch'}
$null=Invoke-RestMethod ($base+'/agent/pause') -Headers $headers -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 10
if(-not $AllowBusyScene){
 $freshBefore=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 5
 if($freshBefore.Menu -ne ''){throw 'Finish the current native menu before replacing the scene; forced closing can orphan story state'}
 $probe=Join-Path $PSScriptRoot 'AcceptanceNativeProbe/bin/Release/net10.0/AcceptanceNativeProbe.dll'
 if((Test-Path (Join-Path $RunRoot 'clock-control-enabled.json')) -and (Test-Path $probe)){
  $diagnostic=& (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools/dotnet/dotnet.exe') $probe $RunRoot
  if($LASTEXITCODE -ne 0){throw 'Native precondition probe failed; no scene was written'}
  $native=$diagnostic|ConvertFrom-Json
  if($native.eventUp -or -not $native.canMove){throw 'Finish native story/animation before replacing the scene; no scene was written'}
 }
}
$id=[guid]::NewGuid().ToString('N')
$request|Add-Member -NotePropertyName id -NotePropertyValue $id -Force
$control=Join-Path $RunRoot 'lab-control.json'
$temporary=$control+'.tmp'
[IO.File]::WriteAllText($temporary,($request|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
$written=$false
for($attempt=0;$attempt -lt 15 -and -not $written;$attempt++){
 try{Move-Item -LiteralPath $temporary -Destination $control -Force -ErrorAction Stop;$written=$true}
 catch{if($attempt -eq 14){throw};Start-Sleep -Milliseconds 100}
}
$deadline=[DateTime]::UtcNow.AddSeconds(25)
do{
 Start-Sleep -Milliseconds 250
 $result=$null
 if(Test-Path ($control+'.result')){try{$result=[IO.File]::ReadAllText($control+'.result')|ConvertFrom-Json}catch{}}
 if($result.id -eq $id){break}
}while([DateTime]::UtcNow -lt $deadline)
if($result.id -ne $id -or $result.status -ne 'ready'){throw ('Fixture did not become ready: '+$result.reason)}
Copy-Item -LiteralPath $control -Destination (Join-Path $RunRoot 'last-fixture-scene.json') -Force
Copy-Item -LiteralPath ($control+'.result') -Destination (Join-Path $RunRoot 'last-fixture-scene.json.result') -Force
$target='Farm';if($request.location){$target=$request.location}
do{
 Start-Sleep -Milliseconds 250
 $fresh=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 5
 if($fresh.Revision -gt $state.Revision -and $fresh.Location -eq $target){break}
}while([DateTime]::UtcNow -lt $deadline)
if($fresh.Location -ne $target){throw 'Native warp has not settled; no model case was submitted'}
$moveDeadline=[DateTime]::UtcNow.AddSeconds(15)
do{
 try{$nativeDetails=[string]$fresh.DiagnosticDetails|ConvertFrom-Json}catch{$nativeDetails=$null}
 if($nativeDetails -and $nativeDetails.canMove){break}
 Start-Sleep -Milliseconds 250
 $fresh=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 5
}while([DateTime]::UtcNow -lt $moveDeadline)
if(-not $nativeDetails -or -not $nativeDetails.canMove){throw 'Native scene transition is still locked; no model case was submitted'}
[pscustomobject]@{id=$id;scene=$request.scene;fixtureOnly=$true;location=$fresh.Location;revision=$fresh.Revision;runId=$fresh.TestRunId}|ConvertTo-Json
