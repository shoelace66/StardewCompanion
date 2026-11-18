[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunRoot,[switch]$Continue)
$ErrorActionPreference='Stop'
$session=Get-Content -LiteralPath (Join-Path $RunRoot 'session.json') -Raw|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$session.token}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
$runId=Split-Path -Leaf $RunRoot
$statusPath=Join-Path $RunRoot 'status.json';$journalPath=Join-Path $RunRoot 'script.jsonl'
$days=[Collections.Generic.List[object]]::new();$tasks=[Collections.Generic.List[object]]::new()
$chest=$null;$allowed=@();$plot=$null
$unavailable=[Collections.Generic.HashSet[string]]::new()
function Request($path,$body=$null){
 if($null -eq $body){return Invoke-RestMethod "$base/$path" -Headers $headers -TimeoutSec 10}
 return Invoke-RestMethod "$base/$path" -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 12))) -TimeoutSec 10
}
function State {Request 'state'}
function View {((State).WorldView|ConvertFrom-Json)}
function Record($kind,$data){[IO.File]::AppendAllText($journalPath,((@{at=[DateTimeOffset]::Now.ToString('o');kind=$kind;data=$data}|ConvertTo-Json -Depth 18 -Compress)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))}
function Status($phase,$data){@{at=[DateTimeOffset]::Now.ToString('o');runId=$runId;phase=$phase;details=$data}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath $statusPath -Encoding UTF8;Write-Output $phase}
function RunTask([string]$skill,$arguments,[int]$timeout=120,[switch]$AllowPartial,[switch]$OptionalResource){
 $state=State
 if($skill -ne 'respond_dialogue' -and $state.Menu -eq 'DialogueBox'){
  $dialogue=$state.MenuContext|ConvertFrom-Json
  if($dialogue.question){throw ('Unanswered game question requires an explicit choice: '+($dialogue.choices|ConvertTo-Json -Compress))}
  $null=RunTask 'respond_dialogue' @{responseKey=''}
  $state=State
 }
 $id=[Guid]::NewGuid().ToString('N');Record 'dispatch' @{day=$state.GameDay;time=$state.TimeOfDay;taskId=$id;skill=$skill;arguments=$arguments}
 $null=Request 'task' @{skill=$skill;arguments=$arguments;taskId=$id};$deadline=[DateTime]::UtcNow.AddSeconds($timeout);$result=$null
 do{$all=Request 'results';$result=$all|Where-Object {$_.TaskId -eq $id -and $_.Status -in @('completed','partial','failed','cancelled','rejected')}|Select-Object -Last 1;if($result){break};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
 if(-not $result){throw "Task timed out: $skill ($id)"};Record 'result' $result;$tasks.Add($result)
 $unreachable=$OptionalResource -and $result.Status -eq 'failed' -and $result.Reason -in @('path_unreachable','opening_target_unreachable','opening_drop_unreachable','opening_drop_pickup_timeout','blocked_after_3_replans')
 if($result.Status -ne 'completed' -and -not($AllowPartial -and $result.Status -eq 'partial') -and -not $unreachable){throw ($skill+': '+$result.Status+' '+$result.Reason)}
 $freshDeadline=[DateTime]::UtcNow.AddSeconds(10)
 do{Start-Sleep -Milliseconds 100;$fresh=State}while($fresh.Revision -le $result.ObservationRevision -and [DateTime]::UtcNow -lt $freshDeadline)
 Write-Output ($skill+': '+$result.Status+' count='+$result.Completed+' reason='+$result.Reason)
 return $result
}
function Farm {if((State).Location -ne 'Farm'){$null=RunTask 'go_to' @{location='Farm';x=64;y=15}}}
function InPlot($soil){$soil.x -ge $plot.x -and $soil.x -lt ($plot.x+$plot.width) -and $soil.y -ge $plot.y -and $soil.y -lt ($plot.y+$plot.height)}
function PlotArgs {@{location='Farm';x=$plot.x;y=$plot.y;width=$plot.width;height=$plot.height}}
function Deposit {
 if(-not $script:chest){return}
 $view=View
 $groups=$view.inventory|Where-Object {$_.id -and -not $_.tool -and $_.id -ne '(O)472' -and $script:allowed -contains $_.id}|Group-Object id
 foreach($group in $groups){$count=($group.Group|Measure-Object count -Sum).Sum;if($count -gt 0){$null=RunTask 'deposit_items' @{chestId=$script:chest.id;location='Farm';x=$script:chest.x;y=$script:chest.y;itemId=$group.Name;count=[int]$count;allowedIds=$script:allowed}}}
}
function TryChest {
 if($script:chest){return}
 for($attempt=0;$attempt -lt 12;$attempt++){
  $state=State;$view=View;$wood=($view.inventory|Where-Object {$_.id -eq '(O)388'}|Measure-Object count -Sum).Sum
  if($wood -ge 50){break}
  if($state.Stamina -lt 100 -or $state.TimeOfDay -ge 1800){break}
  $planResult=RunTask 'inspect_opening' @{};$plan=$planResult.Details|ConvertFrom-Json
  $target=$plan.woodTargets|Where-Object {-not $script:unavailable.Contains(($_.x.ToString()+','+$_.y))}|Select-Object -First 1;if(-not $target){break}
  $clear=RunTask 'clear_area' @{location='Farm';x=$target.x;y=$target.y;width=1;height=1;maxTargets=1;includeTrees=$true} -AllowPartial -OptionalResource
  if($clear.Status -ne 'completed'){$null=$script:unavailable.Add(($target.x.ToString()+','+$target.y));Record 'optional_resource_skipped' @{target=$target;reason=$clear.Reason}}
 }
 $view=View;$wood=($view.inventory|Where-Object {$_.id -eq '(O)388'}|Measure-Object count -Sum).Sum
 if($wood -lt 50){Record 'chest_deferred' @{wood=$wood;stamina=(State).Stamina};return}
 $null=RunTask 'craft_chest' @{}
 $plan=(RunTask 'inspect_opening' @{}).Details|ConvertFrom-Json
 $null=RunTask 'place_chest' @{x=$plan.chestSpot.x;y=$plan.chestSpot.y}
 $bound=RunTask 'bind_chest' @{x=$plan.chestSpot.x;y=$plan.chestSpot.y};$script:chest=$bound.Details|ConvertFrom-Json
 $script:allowed=@('(O)24','(O)16','(O)18','(O)20','(O)22','(O)388','(O)390','(O)771','(O)92','(O)309','(O)310','(O)311','(O)770','(O)382','(O)330')
 Record 'work_chest' $script:chest
 Deposit
}
try{
 Status 'waiting_for_new_solo_world' @{}
 $deadline=[DateTime]::UtcNow.AddSeconds(120)
 do{$state=State;if($state.WorldReady){break};Start-Sleep -Milliseconds 400}while([DateTime]::UtcNow -lt $deadline)
 if(-not $state.WorldReady -or -not $state.IsHost -or $state.Multiplayer -or $state.ControlMode -ne 'solo_test' -or $state.TestRunId -ne $runId){throw 'Wrong or missing isolated solo test world'}
 $startDay=0
 if($Continue){
  if($state.GameDay -lt 1 -or $state.GameDay -gt 4){throw 'Continuation requires an existing opening day 2 through 5'}
  $previous=Get-Content -LiteralPath $journalPath|ForEach-Object {$_|ConvertFrom-Json}
  $plot=($previous|Where-Object kind -eq 'starter_plot'|Select-Object -Last 1).data
  if(-not $plot){throw 'No original plot checkpoint found'}
  $startDay=[int]$state.GameDay
  for($prior=1;$prior -le $startDay;$prior++){
   $verified=$previous|Where-Object {$_.kind -eq 'day_work_verified' -and $_.data.day -eq $prior}|Select-Object -Last 1
   $saved=$previous|Where-Object {$_.kind -eq 'night_saved' -and $_.data.day -eq $prior}|Select-Object -Last 1
   if(-not $verified -or -not $saved){throw 'Missing verified prior day or native night save'}
   $days.Add($verified.data)
  }
  $bound=$previous|Where-Object kind -eq 'work_chest'|Select-Object -Last 1
  if($bound){$script:chest=$bound.data;$script:allowed=@('(O)24','(O)16','(O)18','(O)20','(O)22','(O)388','(O)390','(O)771','(O)92','(O)309','(O)310','(O)311','(O)770','(O)382','(O)330')}
  Record 'continued_after_fix' @{gameDay=$startDay;priorVerifiedDays=$days.Count}
 }elseif($state.GameDay -ne 0){throw 'This runner starts only on spring day 1 of a new farm'}
 $null=Request 'resume' @{}
 if(-not $Continue){$starter=RunTask 'claim_starter_gift' @{};if($starter.Completed -ne 15){throw 'The normal 15 starter seeds were not received'}}
 for($day=$startDay;$day -lt 5;$day++){
  $state=State;if($state.GameDay -ne $day){throw 'Unexpected game date; refusing to skip or replay an opening day'}
  Status ('day_'+($day+1)+'_working') @{day=$day;stamina=$state.Stamina;money=(View).money}
  Farm
  if($null -eq $plot){$plan=(RunTask 'inspect_opening' @{}).Details|ConvertFrom-Json;$plot=$plan.plot;Record 'starter_plot' $plot}
  if($day -eq 0){
   $null=RunTask 'clear_area' (PlotArgs)
   $tilled=RunTask 'till_area' (PlotArgs);if($tilled.Completed -ne 15){throw 'Starter plot did not produce 15 normally tilled tiles'}
   $planted=RunTask 'plant_area' (PlotArgs);if($planted.Completed -ne 15){throw 'Not all starter seeds were planted normally'}
  }
  if($day -eq 4){$harvest=RunTask 'harvest_area' (PlotArgs);if($harvest.Completed -lt 1){throw 'No first harvest on day 5'}}
  $water=RunTask 'water_area' (PlotArgs) -AllowPartial
  $view=View;$crops=@($view.soil|Where-Object {(InPlot $_) -and $_.crop -and -not $_.dead})
  if(@($crops|Where-Object {$_.needsWater}).Count -gt 0){throw 'Living starter crops remain unwatered at the end of the daily work'}
  if($day -lt 4 -and $crops.Count -ne 15){throw 'Starter crop count changed before the first harvest'}
  TryChest
  if((State).Stamina -gt 65 -and (State).TimeOfDay -lt 1500){
   $null=RunTask 'go_to' @{location='BusStop';x=11;y=23}
   $forage=RunTask 'forage_area' @{location='BusStop';x=0;y=0;width=45;height=35} -AllowPartial
   Farm
  }
  Deposit
  $state=State;$view=View
  $summary=@{day=($day+1);gameDay=$state.GameDay;time=$state.TimeOfDay;stamina=$state.Stamina;money=$view.money;rain=$view.rain;liveCrops=$crops;inventory=$view.inventory;chest=$script:chest}
  $days.Add($summary);Record 'day_work_verified' $summary
  Status ('day_'+($day+1)+'_sleeping') @{days=$days.ToArray()}
  $null=RunTask 'sleep' @{} 180
  $deadline=[DateTime]::UtcNow.AddSeconds(90)
  do{$state=State;if($state.GameDay -eq ($day+1) -and $state.Menu -eq '' -and $state.WorldReady -and $state.ActiveTaskId -eq ''){break};Start-Sleep -Milliseconds 250}while([DateTime]::UtcNow -lt $deadline)
  if($state.GameDay -ne ($day+1) -or $state.Menu -ne ''){throw 'Normal night save or morning menu did not finish'}
  $world=Get-Content -LiteralPath (Join-Path $RunRoot 'world.json') -Raw|ConvertFrom-Json
  $saveFile=Join-Path $world.folder (Split-Path -Leaf $world.folder)
  if(-not(Test-Path -LiteralPath $saveFile)){throw 'Native save file missing after the night'}
  $saveInfo=Get-Item -LiteralPath $saveFile
  Record 'night_saved' @{day=($day+1);nextGameDay=$state.GameDay;saveFile=$saveFile;lastWrite=$saveInfo.LastWriteTimeUtc.ToString('o');bytes=$saveInfo.Length}
 }
 $null=Request 'pause' @{}
 $modelEvents=Request 'agent/events?after=0&limit=1000'
 $modelReplies=@($modelEvents.events|Where-Object {$_.Kind -eq 'model_reply'}).Count
 if($modelReplies -ne 0){throw 'Scripted trial unexpectedly invoked the model'}
 Status 'completed_five_days' @{days=$days.ToArray();finalState=(State);tasks=$tasks.ToArray();modelReplies=$modelReplies}
}catch{
 try{$null=Request 'pause' @{}}catch{}
 Record 'stopped' @{reason=$_.Exception.Message}
 Status 'stopped' @{reason=$_.Exception.Message;days=$days.ToArray();tasks=$tasks.ToArray()}
 throw
}
