[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[Parameter(Mandatory=$true)][ValidateSet('hold','release')][string]$Mode)
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
if(-not(Test-Path -LiteralPath (Join-Path $RunRoot 'clock-control-enabled.json'))){throw 'This run has not verified the clock-capable isolated fixture'}
$session=[IO.File]::ReadAllText((Join-Path $RunRoot 'session.json'))|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$session.token}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$state=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 5
if(-not $state.WorldReady -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer -or $state.TestRunId -ne (Split-Path -Leaf $RunRoot) -or $state.PlayerName -ne 'SC5Host'){throw 'Isolated fixture identity mismatch'}
$id=[guid]::NewGuid().ToString('N')
$control=Join-Path $RunRoot 'lab-control.json'
[IO.File]::WriteAllText(($control+'.tmp'),(@{id=$id;scene=('clock_'+$Mode)}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$written=$false
for($attempt=0;$attempt -lt 15 -and -not $written;$attempt++){
 try{Move-Item -LiteralPath ($control+'.tmp') -Destination $control -Force -ErrorAction Stop;$written=$true}
 catch{if($attempt -eq 14){throw};Start-Sleep -Milliseconds 100}
}
$deadline=[DateTime]::UtcNow.AddSeconds(8)
do{
 Start-Sleep -Milliseconds 150
 try{$result=[IO.File]::ReadAllText(($control+'.result'))|ConvertFrom-Json}catch{$result=$null}
 if($result.id -eq $id){break}
}while([DateTime]::UtcNow -lt $deadline)
if($result.id -ne $id -or $result.status -ne 'ready' -or -not $result.clockControlSupported -or $result.clockHeld -ne ($Mode -eq 'hold')){throw 'Clock fixture acknowledgement mismatch'}
$result|ConvertTo-Json
