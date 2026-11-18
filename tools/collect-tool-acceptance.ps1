[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RunRoot,
    [Parameter(Mandatory=$true)][string]$CaseId,
    [Parameter(Mandatory=$true)][long]$After,
    [Parameter(Mandatory=$true)][long]$Through,
    [string]$ManifestPath='',
    [ValidateRange(1,1000)][int]$Round=1,
    [switch]$SerializedCaseWindow
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(-not $ManifestPath){$ManifestPath=Join-Path $repo 'docs/DeepSeek逐工具实机验收-2026-11-10/manifest.json'}
$ManifestPath=[IO.Path]::GetFullPath($ManifestPath)
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
if($CaseId -notmatch '^T\d{3}-[SF]\d{2}$'){throw 'Invalid acceptance case ID'}
if($After -lt 0 -or $Through -le $After){throw 'A committed event sequence window is required'}
$manifest=Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$case=@($manifest.cases|Where-Object {$_.id -ceq $CaseId})
if($case.Count -ne 1){throw 'Case ID is not unique in the selected manifest'}
$case=$case[0]
$trial=Get-Content -LiteralPath (Join-Path $RunRoot 'trial.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($trial.runId -cne (Split-Path -Leaf $RunRoot)){throw 'Run metadata and directory identity differ'}
$journal=Join-Path $RunRoot 'decisions.jsonl'
$events=[Collections.Generic.List[object]]::new();$incompleteLines=0
foreach($line in [IO.File]::ReadLines($journal)){
    $sequencePrefix=[regex]::Match($line,'^\s*\{\s*"Sequence"\s*:\s*(\d+)')
    if($sequencePrefix.Success){
        $lineSequence=[long]$sequencePrefix.Groups[1].Value
        if($lineSequence -le $After -or $lineSequence -gt $Through){continue}
    }
    try{$entry=$line|ConvertFrom-Json}catch{$incompleteLines++;continue}
    if($entry.Sequence -gt $After -and $entry.Sequence -le $Through){$events.Add($entry)}
}
if($events.Count -eq 0){throw 'No committed events exist in the selected window'}
$marker='[TOOL-CASE:'+$CaseId+']'
$captures=@($events|Where-Object {
    $_.Kind -eq 'model_prompt_capture' -and $_.Data.prompt.model -ceq 'deepseek-chat' -and
    ($_.Data.prompt.messages|ConvertTo-Json -Depth 100 -Compress).Contains($marker)
})
$caseWindowAttribution=$null
if($SerializedCaseWindow){
    $messages=@($events|Where-Object Kind -eq 'player_message')
    $scopes=@($events|Where-Object {$_.Scope}|ForEach-Object Scope|Sort-Object -Unique)
    if($messages.Count -ne 1 -or -not $messages[0].Data.text.Contains($marker) -or $scopes.Count -ne 1){throw 'Serialized case evidence requires one marked player message and one game scope'}
    $captures=@($events|Where-Object {$_.Kind -eq 'model_prompt_capture' -and $_.Sequence -gt $messages[0].Sequence -and $_.Data.prompt.model -ceq 'deepseek-chat'})
    $caseWindowAttribution=@{playerMessageSequence=$messages[0].Sequence;scope=$scopes[0];method='exclusive serialized case window; requests may omit the marker after the message is consumed';requiresManualArgumentsReview=$true}
}
$causalMaintenance=@()
if($case.tool -ceq 'update_season_progress'){
    $markedDecisions=@($captures|ForEach-Object {$_.DecisionId}|Sort-Object -Unique)
    $predecessors=@($events|Where-Object {$_.Kind -eq 'task_dispatch' -and $_.Data.source -ceq 'model' -and $_.Data.skill -in @('sleep','end_day') -and $_.DecisionId -in $markedDecisions})
    foreach($capture in @($events|Where-Object {$_.Kind -eq 'model_prompt_capture' -and $_.Data.kind -ceq 'sleep_maintenance' -and $_.Data.prompt.model -ceq 'deepseek-chat'})){
        $changes=@($events|Where-Object {$_.Kind -eq 'day_changed' -and $_.Sequence -lt $capture.Sequence -and $_.Scope -ceq $capture.Scope -and $_.Session -ceq $capture.Session -and $_.Data.day -eq $capture.Data.gameDay})
        $linked=@($predecessors|Where-Object {$prior=$_;@($changes|Where-Object {$_.Sequence -gt $prior.Sequence -and $_.Scope -ceq $prior.Scope -and $_.Session -ceq $prior.Session}).Count -gt 0})
        if($linked.Count -gt 0 -and $capture.Sequence -notin @($captures.Sequence)){
            $captures+=$capture
            $causalMaintenance+=@{captureSequence=$capture.Sequence;predecessorDispatches=@($linked.Sequence);dayChangedSequences=@($changes.Sequence);association='marked model sleep dispatch -> native day change -> dedicated DeepSeek maintenance request'}
        }
    }
}
$decisions=@($captures|ForEach-Object {$_.DecisionId}|Sort-Object -Unique)
$calls=[Collections.Generic.List[object]]::new()
foreach($entry in @($events|Where-Object {$_.Kind -eq 'model_reply' -and $_.DecisionId -in $decisions})){
    foreach($call in @($entry.Data.calls)){
        if($null -eq $call){continue}
        $name=[string]$call.function.name
        try{$arguments=$call.function.arguments|ConvertFrom-Json}catch{$arguments=$null}
        $matches=$name -ceq $case.tool
        if($name.StartsWith('group_') -and $null -ne $arguments){$matches=$matches -or @($arguments.steps|Where-Object {$_.skill -ceq $case.tool}).Count -gt 0}
        if($matches){$calls.Add([ordered]@{sequence=$entry.Sequence;decisionId=$entry.DecisionId;callId=$call.id;function=$name;arguments=$arguments;rawArguments=$call.function.arguments})}
    }
}
$dispatches=@($events|Where-Object {$_.Kind -eq 'task_dispatch' -and $_.DecisionId -in $decisions -and $_.Data.source -ceq 'model'})
$nonModelDispatches=@($events|Where-Object {$_.Kind -eq 'task_dispatch' -and $_.Data.source -cne 'model'})
$taskIds=@($dispatches|ForEach-Object {$_.Data.taskId})
$receipts=@($events|Where-Object {
    $_.Kind -eq 'task_result' -and $_.DecisionId -in $decisions -and
    ($_.Data.TaskId -in $taskIds -or $_.Data.Skill -ceq $case.tool) -and
    $_.Data.Status -in @('completed','partial','failed','rejected','cancelled')
})
$toolResults=@($events|Where-Object {$_.Kind -in @('tool_result','tool_rejected','plan_finished','continuation_planned','continuation_unavailable') -and $_.DecisionId -in $decisions})
$stateInputs=@($events|Where-Object {$_.Kind -eq 'decision_context' -and $_.DecisionId -in $decisions})
$controls=@($events|Where-Object {$_.Kind -eq 'control'})
$status=if($nonModelDispatches.Count -gt 0){'non_model_dispatch_in_case_window'}elseif($captures.Count -eq 0){'missing_marked_deepseek_request'}elseif($calls.Count -eq 0){'no_matching_logged_model_call'}else{'requires_manual_receipt_and_state_review'}
# The collector records evidence only. It cannot establish semantic correctness or a pass.
$evidence=[ordered]@{
    caseId=$CaseId;tool=$case.tool;round=$Round;requirement=$case.requirement;kind=$case.kind
    runId=$trial.runId;runRoot=$RunRoot;afterExclusive=$After;throughInclusive=$Through
    collectedAt=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('o')
    journalSha256=(Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash
    incompleteLinesSkipped=$incompleteLines;coverageStatus=$status;verdict='unreviewed';passed=$false
    requests=$captures;caseWindowAttribution=$caseWindowAttribution;causalMaintenance=$causalMaintenance;matchingCalls=$calls;modelDispatches=$dispatches;nonModelDispatches=$nonModelDispatches;terminalReceipts=$receipts
    toolResults=$toolResults;decisionContexts=$stateInputs;operatorControls=$controls
    reviewRequired=@('确认独立测试环境与装载版本','核对DeepSeek实际调用及参数','核对原版前后状态与费用、物品和进度','区分任务accepted与终态','核对失败原因与恢复条件','确认无操作者直接派发被测工具','人工填写问题或验收结论；不得仅凭调用出现标记通过')
}
$output=Join-Path (Split-Path -Parent $ManifestPath) ('evidence/round-'+$Round)
New-Item -ItemType Directory -Path $output -Force|Out-Null
$path=Join-Path $output ($CaseId+'-'+[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+'.json')
[IO.File]::WriteAllText($path,($evidence|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{caseId=$CaseId;tool=$case.tool;deepseekRequests=$captures.Count;matchingCalls=$calls.Count;terminalReceipts=$receipts.Count;verdict='unreviewed';path=$path}|ConvertTo-Json
