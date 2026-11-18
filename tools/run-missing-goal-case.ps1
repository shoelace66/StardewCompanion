[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[int]$Round=2)
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$s=Get-Content (Join-Path $RunRoot 'session.json') -Raw|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$s.token}
$base=($s.endpoint -replace '^ws:','http:') -replace '/bridge$',''
function ReadState($path){Invoke-RestMethod ($base+'/'+$path) -Headers $headers -TimeoutSec 10}
$state=ReadState 'state';$agent=ReadState 'agent/state';$goal=ReadState 'agent/goal'
if(-not $state.WorldReady -or $state.Multiplayer -or $state.ControlMode -ne 'solo_test' -or $state.TestRunId -ne (Split-Path -Leaf $RunRoot) -or $state.PlayerName -ne 'SC5Host'){throw 'Isolated identity required'}
if($agent.running -or $agent.thinking -or $state.ActiveTaskId){throw 'Paused idle agent required'}
if(-not [string]::IsNullOrWhiteSpace($goal.objective)){throw 'T014-F01 requires a clean scope launched with -NoPlayerGoal; no existing goal will be cleared'}
$directory=Join-Path $RunRoot ('acceptance/round-'+$Round+'/T014-F01-'+[DateTimeOffset]::Now.ToUnixTimeMilliseconds())
$null=New-Item -ItemType Directory -Path $directory
$state|ConvertTo-Json -Depth 40|Set-Content (Join-Path $directory 'before.json') -Encoding UTF8
$cursor=(ReadState 'agent/events?limit=1').latest
$body=@{text='[TOOL-CASE:T014-F01] Verify the missing saved player-goal boundary. Do not infer or save a goal from this test message, and do not execute game actions.'}|ConvertTo-Json
$null=Invoke-RestMethod ($base+'/agent/message') -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
$httpStatus=0
try{$null=Invoke-RestMethod ($base+'/agent/resume') -Headers $headers -Method Post -ContentType 'application/json' -Body '{}'}
catch{if($_.Exception.Response){$httpStatus=[int]$_.Exception.Response.StatusCode}else{throw}}
try{
 $after=ReadState 'state';$afterAgent=ReadState 'agent/state';$afterGoal=ReadState 'agent/goal'
 $events=ReadState ('agent/events?after='+$cursor+'&limit=1000')
 $events|ConvertTo-Json -Depth 65|Set-Content (Join-Path $directory 'events.json') -Encoding UTF8
 $after|ConvertTo-Json -Depth 40|Set-Content (Join-Path $directory 'after.json') -Encoding UTF8
 $record=[ordered]@{caseId='T014-F01';tool='create_season_plan';verdict='unreviewed';resumeHttpStatus=$httpStatus;goalBefore=$goal;goalAfter=$afterGoal;agentRunningAfter=$afterAgent.running;activeTaskAfter=$after.ActiveTaskId;modelRequests=@($events.events|Where-Object kind -eq 'model_request');dispatches=@($events.events|Where-Object kind -eq 'task_dispatch');directory=$directory;boundary='missing_saved_player_goal';manualReviewRequired=$true}
 $record|ConvertTo-Json -Depth 40|Set-Content (Join-Path $directory 'result.json') -Encoding UTF8
 $repo=Split-Path -Parent $PSScriptRoot
 $evidenceDirectory=Join-Path $repo ('docs/DeepSeek逐工具实机验收-2026-11-10/evidence/round-'+$Round)
 $null=New-Item -ItemType Directory -Path $evidenceDirectory -Force
 $evidencePath=Join-Path $evidenceDirectory ('T014-F01-'+[DateTimeOffset]::Now.ToUnixTimeMilliseconds()+'.json')
 $evidence=[ordered]@{caseId=$record.caseId;tool=$record.tool;round=$Round;kind='missing_goal_boundary';runId=$state.TestRunId;runRoot=$RunRoot;collectedAt=[DateTimeOffset]::Now.ToString('o');verdict='unreviewed';passed=$false;resumeHttpStatus=$httpStatus;goalBefore=$goal;goalAfter=$afterGoal;agentRunningAfter=$afterAgent.running;activeTaskAfter=$after.ActiveTaskId;modelRequests=$record.modelRequests;matchingCalls=@();modelDispatches=$record.dispatches;nonModelDispatches=@();terminalReceipts=@();sourceRecord=(Join-Path $directory 'result.json');manualReviewRequired=$true}
 $evidence|ConvertTo-Json -Depth 40|Set-Content $evidencePath -Encoding UTF8
 [pscustomobject]@{caseId=$record.caseId;resumeHttpStatus=$httpStatus;modelRequests=$record.modelRequests.Count;dispatches=$record.dispatches.Count;verdict='unreviewed';directory=$directory;evidencePath=$evidencePath}|ConvertTo-Json
}finally{
 # If the guard is broken, stop immediately and preserve the failure evidence.
 $null=Invoke-RestMethod ($base+'/agent/pause') -Headers $headers -Method Post -ContentType 'application/json' -Body '{}'
}
