[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$EvidencePath,
 [Parameter(Mandatory=$true)][ValidateSet('passed','failed','pending')][string]$Verdict,
 [Parameter(Mandatory=$true)][string]$Note,
 [string]$IssueId=''
)
$ErrorActionPreference='Stop'
$EvidencePath=[IO.Path]::GetFullPath($EvidencePath)
$evidence=[IO.File]::ReadAllText($EvidencePath)|ConvertFrom-Json
$expectedUnavailable=$evidence.tool -in @('complete_public_project','mine_travel')
$catalogEvidence=@($evidence.toolResults|Where-Object {$_.Kind -eq 'tool_result' -and $_.Data.name -eq 'list_capabilities' -and $_.Data.result.status -eq 'expanded' -and $_.Data.result.scope.allowedSkillsThisTurn -notcontains $evidence.tool -and $_.Data.result.scope.availableTools -notcontains $evidence.tool})
$unavailableVerified=$expectedUnavailable -and $catalogEvidence.Count -gt 0 -and $evidence.requests.Count -gt 0 -and $evidence.matchingCalls.Count -eq 0 -and $evidence.modelDispatches.Count -eq 0
$missingGoalVerified=$evidence.caseId -eq 'T014-F01' -and $evidence.kind -eq 'missing_goal_boundary' -and $evidence.resumeHttpStatus -eq 409 -and [string]::IsNullOrWhiteSpace([string]$evidence.goalBefore.objective) -and [string]::IsNullOrWhiteSpace([string]$evidence.goalAfter.objective) -and $evidence.goalBefore.status -eq 'unset' -and $evidence.goalAfter.status -eq 'unset' -and $evidence.agentRunningAfter -eq $false -and [string]::IsNullOrWhiteSpace([string]$evidence.activeTaskAfter) -and @($evidence.modelRequests).Count -eq 0 -and @($evidence.modelDispatches).Count -eq 0 -and @($evidence.nonModelDispatches).Count -eq 0
if($Verdict -eq 'passed' -and (($evidence.matchingCalls.Count -lt 1 -and -not $unavailableVerified -and -not $missingGoalVerified) -or $evidence.nonModelDispatches.Count -gt 0)){throw 'A model call, explicit unavailable-capability catalog evidence, or verified missing-goal rejection without operator dispatch is required for a pass'}
$root=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $EvidencePath))
$manifestPath=Join-Path $root 'manifest.json'
$manifest=[IO.File]::ReadAllText($manifestPath)|ConvertFrom-Json
$cases=@($manifest.cases|Where-Object id -eq $evidence.caseId)
if($cases.Count -ne 1){throw 'Case identity mismatch'}
$case=$cases[0]
$case.status=$Verdict
$case.modelEvidence=$EvidencePath;$case.receiptEvidence=$EvidencePath;$case.stateEvidence=$Note
if($IssueId){$case.issueId=$IssueId}
$review=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');caseId=$case.id;verdict=$Verdict;note=$Note;issueId=$IssueId;evidence=$EvidencePath;sha256=(Get-FileHash -LiteralPath $EvidencePath).Hash;manualSemanticReview=$true;unavailableCapabilityVerified=$unavailableVerified;missingGoalBoundaryVerified=$missingGoalVerified}
[IO.File]::AppendAllText((Join-Path $root 'reviews.jsonl'),($review|ConvertTo-Json -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
[pscustomobject]$review|ConvertTo-Json
