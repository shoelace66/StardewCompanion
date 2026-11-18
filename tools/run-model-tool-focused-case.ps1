[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$RunRoot,
 [Parameter(Mandatory=$true)][string]$CaseId,
 [Parameter(Mandatory=$true)][string]$Instruction,
 [string]$RequestFile='',
 [int]$TimeoutSeconds=150,
 [int]$Round=2
)
$ErrorActionPreference='Stop'
$session=[IO.File]::ReadAllText((Join-Path $RunRoot 'session.json'))|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$session.token}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$state=Invoke-RestMethod ($base+'/state') -Headers $headers -TimeoutSec 10
if(-not $state.WorldReady -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer -or $state.TestRunId -ne (Split-Path -Leaf $RunRoot) -or $state.PlayerName -ne 'SC5Host'){throw 'Isolated test identity mismatch'}
$null=Invoke-RestMethod ($base+'/agent/pause') -Headers $headers -Method Post -ContentType 'application/json' -Body '{}'
if($RequestFile){& (Join-Path $PSScriptRoot 'prepare-model-tool-scene.ps1') -RunRoot $RunRoot -RequestFile $RequestFile}
if($CaseId -eq 'T014-F01'){
 & (Join-Path $PSScriptRoot 'run-missing-goal-case.ps1') -RunRoot $RunRoot -Round $Round
 return
}
# Explicitly replace a stale working objective between independent acceptance cases.
# This uses the ordinary player-goal interface, never dispatches a game task,
# and is not a fix for Q007. The original stale-objective evidence is retained.
$focusedInstruction=$Instruction+"`n若程序要求保存季计划，请完整填写必填字段但保持简短，各列表只写本次验收所需内容；不要复制历史用例或长篇回顾。计划保存后继续实际调用本次被测工具。"
if($CaseId -eq 'T010-F01' -or $CaseId -like '*mine_travel*'){$focusedInstruction+="`n若目标技能未出现在当前Schema中，先调用list_capabilities核验完整目录；确认未开放后直接解释原因并等待下一条验收消息。"}
$body=@{objective="[TOOL-CASE:$CaseId] $focusedInstruction";completionCondition='Only the current acceptance case terminal result, then wait';priorities=@('Use actual model-selected tools','Verify actual receipts');restrictions=@('No other game objectives');autonomy='Only this acceptance case';disabledSkills=@()}|ConvertTo-Json -Depth 5
$goal=Invoke-RestMethod ($base+'/agent/goal') -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 10
if($goal.objective -cne "[TOOL-CASE:$CaseId] $focusedInstruction"){throw 'Focused goal verification failed'}
& (Join-Path $PSScriptRoot 'run-model-tool-case.ps1') -RunRoot $RunRoot -CaseId $CaseId -Instruction $focusedInstruction -TimeoutSeconds $TimeoutSeconds -Round $Round
