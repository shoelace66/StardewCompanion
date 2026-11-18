[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
if(-not (Test-Path (Join-Path $RunRoot 'native-facts-enabled.json'))){throw 'Read-only native facts support has not been verified for this run'}
$s=[IO.File]::ReadAllText((Join-Path $RunRoot 'session.json'))|ConvertFrom-Json
$base=($s.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$v=Invoke-RestMethod ($base+'/state') -Headers @{Authorization='Bearer '+$s.token} -TimeoutSec 5
if(-not $v.WorldReady -or $v.ControlMode -ne 'solo_test' -or $v.Multiplayer -or $v.PlayerName -ne 'SC5Host' -or $v.TestRunId -ne (Split-Path -Leaf $RunRoot)){throw 'Native facts isolated identity mismatch'}
$id=[guid]::NewGuid().ToString('N');$control=Join-Path $RunRoot 'lab-control.json'
$temporary=$control+'.'+$id+'.tmp'
[IO.File]::WriteAllText($temporary,(@{id=$id;scene='snapshot'}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
$written=$false
for($attempt=0;$attempt -lt 15;$attempt++){
 try{Move-Item -LiteralPath $temporary -Destination $control -Force;$written=$true;break}catch{Start-Sleep -Milliseconds 100}
}
if(-not $written){throw 'Could not submit read-only native snapshot'}
$deadline=[DateTime]::UtcNow.AddSeconds(15)
do{
 Start-Sleep -Milliseconds 100
 try{$result=[IO.File]::ReadAllText(($control+'.result'))|ConvertFrom-Json}catch{continue}
 if($result.id -eq $id){
  if($result.status -ne 'ready' -or -not $result.fixtureOnly -or $result.runId -ne $v.TestRunId -or -not $result.nativeFacts){throw 'Invalid native facts response'}
  [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($result|ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))
  return
 }
}while([DateTime]::UtcNow -lt $deadline)
throw 'Native facts snapshot acknowledgement timed out'
