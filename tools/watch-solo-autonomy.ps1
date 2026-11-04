[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[ValidateRange(1,112)][int]$Days=5,[switch]$Resume,[switch]$RestartGoal)
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
$instruction="在这个隔离春季存档中自主经营前${Days}天，并只依据当前存档状态和已确认的游戏回执规划。春1初始500g全部优先投资10包土豆种子（QualifiedItemId=(O)475，单价上限50g），及时种下建立早期现金流；(O)472是防风草种子，不是土豆。直接调用purchase_items购买，location填Town、shopId填SeedShop；该技能会自动寻路和开店，不要先用go_to或路线检查手动找店。购买失败时按真实回执调整，不要猜物品ID。持续核算现金流，为春13蛋节购买草莓种子预留金币，不把储备花在低收益事项上。正常领取并利用初始礼包。低体力且距离夜间仍有充足时间时，优先安排附近且当前可观察到的NPC日常交谈以提高好感，同时保护种植关键时限。开荒期间主动评估十字镐升级；资金与材料满足时尽早安排铁匠铺升级，并在完成日取回。若某项行动因当前不可用的工具或材料受阻，记录实际原因，不声称已完成。玩家只观察不手动操作。完成第${Days}夜原版保存并进入第$($Days+1)天早晨后结束测试。"
$instructionPath=Join-Path $RunRoot 'initial-instruction.txt'
if($Resume){
 if(-not(Test-Path -LiteralPath $instructionPath)){throw 'Existing original instruction is required to resume'}
 if($RestartGoal){
  [IO.File]::WriteAllText($instructionPath,$instruction,[Text.UTF8Encoding]::new($false))
  $null=Request 'agent/goal' @{objective=$instruction;completionCondition=('第'+$Days+'夜保存完成，进入第'+($Days+1)+'天早晨');priorities=@('早期投资回报与春13草莓资金储备','低体力时利用剩余白天与附近NPC交谈','开荒期评估并尽早升级十字镐');restrictions=@();autonomy='普通经营功能开放，依照真实资金、材料、时间和体力自主安排';disabledSkills=@()}
 }
}else{
 if(Test-Path -LiteralPath $instructionPath){throw 'This run already received its initial instruction; refusing to submit it twice'}
 [IO.File]::WriteAllText($instructionPath,$instruction,[Text.UTF8Encoding]::new($false))
 $null=Request 'agent/goal' @{objective=$instruction;completionCondition=('第'+$Days+'夜保存完成，进入第'+($Days+1)+'天早晨');priorities=@('早期投资回报与春13草莓资金储备','低体力时利用剩余白天与附近NPC交谈','开荒期评估并尽早升级十字镐');restrictions=@();autonomy='普通经营功能开放，依照真实资金、材料、时间和体力自主安排';disabledSkills=@()}
}
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
