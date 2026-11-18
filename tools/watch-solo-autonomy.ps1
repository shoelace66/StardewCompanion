[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[ValidateRange(1,112)][int]$Days=5,[switch]$Resume,[switch]$RestartGoal,[string]$InitialInstructionFile='')
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$session=Get-Content -LiteralPath (Join-Path $RunRoot 'session.json') -Raw|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$session.token}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
function Request($path,$body=$null){
 if($null -eq $body){return Invoke-RestMethod "$base/$path" -Headers $headers -TimeoutSec 10}
 return Invoke-RestMethod "$base/$path" -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 8))) -TimeoutSec 10
}
function Status($phase,$state){@{at=[DateTimeOffset]::Now.ToString('o');phase=$phase;gameDay=$state.GameDay;time=$state.TimeOfDay;runId=(Split-Path -Leaf $RunRoot);trialDays=$Days;mode='deepseek_autonomous';intervention='one startup instruction; no scripted gameplay; player may use monitor controls'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $RunRoot 'autonomy-status.json') -Encoding UTF8}
$deadline=[DateTime]::UtcNow.AddSeconds(180)
do{$state=Request 'state';if($state.WorldReady){break};Start-Sleep -Milliseconds 500}while([DateTime]::UtcNow -lt $deadline)
if(-not $state.WorldReady -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer -or (-not $Resume -and $state.GameDay -ne 0) -or $state.TestRunId -ne (Split-Path -Leaf $RunRoot)){throw 'Expected the explicitly selected isolated solo farm'}
$agent=Request 'agent/state';if(-not $agent.PlanningEnabled){throw 'Model planning is disabled'}
# Test goals come only from the explicitly supplied instruction file; there is no built-in fallback prompt.
$instruction=$null
$instructionPath=Join-Path $RunRoot 'initial-instruction.txt'
$instructionChanged=(-not $Resume) -or $RestartGoal
if($instructionChanged){
 if([string]::IsNullOrWhiteSpace($InitialInstructionFile)){throw 'A new or replacement goal requires -InitialInstructionFile'}
 $InitialInstructionFile=[IO.Path]::GetFullPath($InitialInstructionFile)
 if(-not(Test-Path -LiteralPath $InitialInstructionFile -PathType Leaf)){throw 'Initial instruction file was not found'}
 $instruction=Get-Content -LiteralPath $InitialInstructionFile -Raw -Encoding UTF8
 if([string]::IsNullOrWhiteSpace($instruction)-or$instruction.Length -gt 16000){throw 'Initial instruction must contain 1..16000 characters'}
}
if($Resume){
 if(-not(Test-Path -LiteralPath $instructionPath)){throw 'Existing original instruction is required to resume'}
 if($RestartGoal){
  [IO.File]::WriteAllText($instructionPath,$instruction,[Text.UTF8Encoding]::new($false))
  $null=Request 'agent/goal' @{objective=$instruction;completionCondition=('执行至第'+$Days+'夜保存，并在第'+($Days+1)+'天早晨暂停');priorities=@('依照完整春季目标安排收益、创造物和村民好感','先完成玩家明确指定的春1起步要求','依据季计划、当前真实状态与回执选择下一步');restrictions=@();autonomy='游戏内在普通经营功能范围内自主执行；玩家观察，不手动补做';disabledSkills=@()}
 }
}else{
 if(Test-Path -LiteralPath $instructionPath){throw 'This run already received its initial instruction; refusing to submit it twice'}
 [IO.File]::WriteAllText($instructionPath,$instruction,[Text.UTF8Encoding]::new($false))
 $null=Request 'agent/goal' @{objective=$instruction;completionCondition=('执行至第'+$Days+'夜保存，并在第'+($Days+1)+'天早晨暂停');priorities=@('依照完整春季目标安排收益、创造物和村民好感','先完成玩家明确指定的春1起步要求','依据季计划、当前真实状态与回执选择下一步');restrictions=@();autonomy='游戏内在普通经营功能范围内自主执行；玩家观察，不手动补做';disabledSkills=@()}
}
if($instructionChanged){$saved=Request 'agent/goal';if($saved.objective -cne $instruction){throw 'Goal verification failed: saved objective differs from supplied instruction'}}
# Only startup control requests. No /task, strategy feedback, or recovery commands.
$null=Request 'agent/resume' @{}
Status 'running' $state
$previous='';$disconnectedAt=$null
while($true){
 Start-Sleep -Seconds 5
 try{$state=Request 'state';$disconnectedAt=$null}catch{
  Status 'connection_lost_no_intervention' $state
  if($null -eq $disconnectedAt){$disconnectedAt=[DateTime]::UtcNow}
  if(([DateTime]::UtcNow-$disconnectedAt).TotalMinutes -ge 5){exit 2}
  continue
 }
 if(-not $state.WorldReady){Status 'waiting_for_world' $state;continue}
 $world=$state.WorldView|ConvertFrom-Json
 $key="$($state.GameDay):$($state.TimeOfDay):$($state.Paused):$($state.Menu)"
 if($key -ne $previous){
  $snapshot=@{at=[DateTimeOffset]::Now.ToString('o');runId=$state.TestRunId;revision=$state.Revision;gameDay=$state.GameDay;time=$state.TimeOfDay;location=$state.Location;tile=$state.Tile;stamina=$state.Stamina;health=$state.Health;paused=$state.Paused;activeTaskId=$state.ActiveTaskId;menu=$state.Menu;menuContext=$state.MenuContext;money=$world.money;inventory=$world.inventory;soil=$world.soil;chests=$world.chests}
  [IO.File]::AppendAllText((Join-Path $RunRoot 'observation-snapshots.jsonl'),(($snapshot|ConvertTo-Json -Depth 15 -Compress)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
  $previous=$key
 }
 Status 'running' $state
 if($state.GameDay -ge $Days -and $state.Menu -eq '' -and $state.ActiveTaskId -eq '' -and $state.Paused){Status 'completed_native_days' $state;exit 0}
}
