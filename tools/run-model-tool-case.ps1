[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$RunRoot,
 [Parameter(Mandatory=$true)][string]$CaseId,
 [string]$Instruction='',
 [int]$TimeoutSeconds=150,
 [int]$Round=1
)
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$repo=Split-Path -Parent $PSScriptRoot
$manifestPath=Join-Path $repo 'docs/DeepSeek逐工具实机验收-2026-11-10/manifest.json'
$manifest=[IO.File]::ReadAllText($manifestPath)|ConvertFrom-Json
$case=@($manifest.cases|Where-Object id -eq $CaseId)
if($case.Count -ne 1){throw 'Unknown or duplicate case'}
$case=$case[0]
$session=[IO.File]::ReadAllText((Join-Path $RunRoot 'session.json'))|ConvertFrom-Json
$headers=@{Authorization='Bearer '+$session.token}
$base=($session.endpoint -replace '^ws:','http:') -replace '/bridge$',''
function Request($path,$body=$null){
 if($null -eq $body){return Invoke-RestMethod ($base+'/'+$path) -Headers $headers -TimeoutSec 10}
 $json=$body|ConvertTo-Json -Depth 12 -Compress
 return Invoke-RestMethod ($base+'/'+$path) -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 10
}
$state=Request 'state'
if(-not $state.WorldReady -or $state.ControlMode -ne 'solo_test' -or $state.Multiplayer -or $state.TestRunId -ne (Split-Path -Leaf $RunRoot) -or $state.PlayerName -ne 'SC5Host'){throw 'Isolated test identity mismatch'}
$null=Request 'agent/pause' @{}
$clockControlled=Test-Path -LiteralPath (Join-Path $RunRoot 'clock-control-enabled.json')
if($clockControlled){$null=& (Join-Path $PSScriptRoot 'set-tool-lab-clock.ps1') -RunRoot $RunRoot -Mode hold}
$directory=Join-Path $RunRoot ('acceptance/round-'+$Round+'/'+$CaseId+'-'+[DateTimeOffset]::Now.ToUnixTimeMilliseconds())
$null=New-Item -ItemType Directory -Path $directory
foreach($fixtureFile in @('lab-control.json','lab-control.json.result','last-fixture-scene.json','last-fixture-scene.json.result')){
 $source=Join-Path $RunRoot $fixtureFile
 if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $directory ('precondition-'+$fixtureFile))}
}
$memoryFixture=Get-ChildItem -LiteralPath $RunRoot -Filter 'memory-precondition-*.json' -File -ErrorAction SilentlyContinue|Sort-Object LastWriteTime -Descending|Select-Object -First 1
if($memoryFixture){Copy-Item -LiteralPath $memoryFixture.FullName -Destination (Join-Path $directory 'precondition-memory-authorization.json')}
$state|ConvertTo-Json -Depth 35|Set-Content (Join-Path $directory 'before.json') -Encoding UTF8
if(Test-Path (Join-Path $RunRoot 'native-facts-enabled.json')){& (Join-Path $PSScriptRoot 'capture-model-tool-native-facts.ps1') -RunRoot $RunRoot -OutputPath (Join-Path $directory 'before.native.json')}
$after=(Request 'agent/events?limit=1').latest
$cursor=$after
$prompt=[string]$case.prompt
if($Instruction){$prompt+="`n"+$Instruction}
[IO.File]::WriteAllText((Join-Path $directory 'instruction.txt'),$prompt,[Text.UTF8Encoding]::new($false))
$null=Request 'agent/message' @{text=$prompt}
$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$terminal=$null
$targetWrappers=[Collections.Generic.HashSet[string]]::new()
$groupPlanIds=[Collections.Generic.HashSet[string]]::new()
$targetPlanIds=[Collections.Generic.HashSet[string]]::new()
$waitInstalled=$false
$lastModelReplyHadCalls=$false
$unavailableCatalogVerified=$false
try{
 if($clockControlled){$null=& (Join-Path $PSScriptRoot 'set-tool-lab-clock.ps1') -RunRoot $RunRoot -Mode release}
 $null=Request 'agent/resume' @{}
 do{
  Start-Sleep -Milliseconds 400
  $page=Request ('agent/events?after='+$cursor+'&limit=1000')
  foreach($event in $page.events){
   [IO.File]::AppendAllText((Join-Path $directory 'events.jsonl'),($event|ConvertTo-Json -Depth 65 -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
   $cursor=[Math]::Max($cursor,$event.Sequence)
   $data=$event.Data
   # A model/validation failure ends collection, but never implies a tool passed.
   if($event.Kind -eq 'model_error'){$terminal=$event}
   if($event.Kind -eq 'model_reply'){
    $lastModelReplyHadCalls=@($data.calls).Count -gt 0
    foreach($call in $data.calls){
     if($call.function.name -like 'group_*'){
      try{
       $arguments=$call.function.arguments|ConvertFrom-Json
       if($call.function.name -eq $case.tool -and $arguments.planId){$null=$groupPlanIds.Add([string]$arguments.planId)}
       if(@($arguments.steps|Where-Object skill -eq $case.tool).Count -gt 0){$null=$targetWrappers.Add([string]$call.function.name);if($arguments.planId){$null=$targetPlanIds.Add([string]$arguments.planId)}}
      }catch{ }
     }
    }
   }
   if($event.Kind -eq 'tool_result' -and $case.tool -in @('complete_public_project','mine_travel') -and $data.name -eq 'list_capabilities' -and $data.result.status -eq 'expanded' -and $data.result.scope.allowedSkillsThisTurn -notcontains $case.tool -and $data.result.scope.availableTools -notcontains $case.tool){$unavailableCatalogVerified=$true}
   # For an intentionally unimplemented capability, finish only after the
   # directory excludes it and the model has produced a no-action response.
   if($case.tool -in @('complete_public_project','mine_travel') -and $event.Kind -eq 'decision_finished' -and $targetWrappers.Count -eq 0 -and $unavailableCatalogVerified -and -not $lastModelReplyHadCalls){$terminal=$event}
   if($event.Kind -eq 'tool_result' -and $data.name -eq 'wait_for_change' -and $data.result.status -eq 'waiting'){$waitInstalled=$true}
   if($case.tool -eq 'wait_for_change' -and $waitInstalled -and $event.Kind -eq 'decision_context' -and $data.trigger.kind -in @('wait_condition_changed','wait_timeout')){$terminal=$event}
   if($case.tool -like 'group_*' -and $event.Kind -eq 'plan_finished' -and $groupPlanIds.Contains([string]$data.planId)){$terminal=$event}
   if($event.Kind -eq 'plan_finished' -and $data.status -ne 'completed' -and $targetPlanIds.Contains([string]$data.planId)){$terminal=$event}
   if(($event.Kind -eq 'tool_result' -and $data.name -eq $case.tool -and $data.result.Status -notin @('accepted','running','waiting')) -or
      ($event.Kind -eq 'task_result' -and $data.Skill -eq $case.tool -and $data.Status -in @('completed','partial','failed','rejected','cancelled')) -or
      ($event.Kind -eq 'tool_rejected' -and ($data.name -eq $case.tool -or $targetWrappers.Contains([string]$data.name)))){$terminal=$event}
  }
 }while($null -eq $terminal -and [DateTime]::UtcNow -lt $deadline)
}finally{
 $null=Request 'agent/pause' @{}
 if($clockControlled){$null=& (Join-Path $PSScriptRoot 'set-tool-lab-clock.ps1') -RunRoot $RunRoot -Mode hold}
 (Request 'state')|ConvertTo-Json -Depth 35|Set-Content (Join-Path $directory 'after.json') -Encoding UTF8
 if(Test-Path (Join-Path $RunRoot 'native-facts-enabled.json')){
  try{& (Join-Path $PSScriptRoot 'capture-model-tool-native-facts.ps1') -RunRoot $RunRoot -OutputPath (Join-Path $directory 'after.native.json')}
  catch{[IO.File]::WriteAllText((Join-Path $directory 'native-facts-error.txt'),$_.Exception.Message,[Text.UTF8Encoding]::new($false))}
 }
 $summary=@{caseId=$CaseId;tool=$case.tool;after=$after;through=$cursor;terminal=$terminal;timedOut=($null -eq $terminal);verdict='unreviewed';directory=$directory}
 $summary|ConvertTo-Json -Depth 30|Set-Content (Join-Path $directory 'result.json') -Encoding UTF8
}
[pscustomobject]$summary|Select-Object caseId,tool,after,through,timedOut,verdict,directory|ConvertTo-Json
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'collect-tool-acceptance.ps1') -RunRoot $RunRoot -CaseId $CaseId -After $after -Through $cursor -Round $Round -SerializedCaseWindow
