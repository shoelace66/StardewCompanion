using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed record SeasonPlanRecord(int Year,string Season,string Goal,string[] Constraints,string[] SuccessCriteria,string[] Progress,string CreatedAt)
{
    public int GoalVersion {get;init;}
    public string[] Milestones {get;init;}=Array.Empty<string>();
    public string[] ResourceStrategy {get;init;}=Array.Empty<string>();
}

public sealed partial class CompanionRuntime
{
    private JsonNode? GetSeasonPlan()
    {
        if(JsonNode.Parse(_memory.Get(_scope,"season_plan")??"null") is not JsonObject stored
            ||stored["year"] is not JsonValue year||!year.TryGetValue<int>(out _)
            ||stored["season"] is not JsonValue season||!season.TryGetValue<string>(out _)
            ||stored["goalVersion"] is not JsonValue version||!version.TryGetValue<int>(out _)
            ||stored["milestones"] is not JsonArray||stored["resourceStrategy"] is not JsonArray)return null;
        return stored;
    }

    private void RecordSuccessfulDayResult(TaskResult result)
    {
        var executionDay=ReceiptExecutionDay(result);
        var key="verified_day_results:"+_session+":"+executionDay;
        var results=JsonNode.Parse(_memory.Get(_scope,key)??"[]")!.AsArray();
        var receipt=JsonSerializer.SerializeToNode(SeasonProgressEntry(result,executionDay));
        if(results.Any(r=>r?["taskId"]?.ToString()==result.TaskId||r?["details"]?["taskId"]?.ToString()==result.TaskId))return;
        results.Add(receipt);_memory.Set(_scope,key,results.ToJsonString());
        var pending=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}") as JsonObject;
        if(pending?["endedDay"]?.GetValue<int>()==executionDay){
            pending["successfulResults"]=results.DeepClone();_memory.Set(_scope,"season_maintenance",pending.ToJsonString());
            if(_memory.Get(_scope,"season_progress_updated_day")==executionDay.ToString())_memory.Set(_scope,"season_progress_updated_day","");
        }
    }

    private static string CalendarSeason(GameObservation state)
    {
        try{return DecisionContextCompression.Text(JsonNode.Parse(state.WorldView)?["decisionFacts"]?["calendar"],"season");}
        catch{return "";}
    }

    private int ReceiptExecutionDay(TaskResult result)
    {
        int? before=null,after=null;
        static void Find(ref int? before,ref int? after,JsonNode? node){
            if(node is JsonObject obj){
                if(obj["gameDayBefore"] is JsonValue b&&b.TryGetValue<int>(out var bv))before??=bv;
                if(obj["gameDayAfter"] is JsonValue a&&a.TryGetValue<int>(out var av))after??=av;
                foreach(var field in obj)Find(ref before,ref after,field.Value);
            }else if(node is JsonArray items){foreach(var item in items)Find(ref before,ref after,item);}
        }
        try{Find(ref before,ref after,JsonNode.Parse(result.Details));}catch(JsonException){ }
        if(before.HasValue)return before.Value;
        // A day-spanning task (end_day/sleep) whose pre-task snapshot was missing
        // must archive to the pre-roll day. gameDayAfter is already the post-sleep
        // day; falling back to the live _day here would shift the receipt (and the
        // season maintenance date) forward one day and misalign verified results.
        if(after.HasValue&&after.Value==_day&&after.Value>0)return after.Value-1;
        return _day;
    }
    private bool SeasonMaintenancePending(){
        var record=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}");
        return GetSeasonPlan()!=null
            &&record?["endedDay"] is JsonValue endedDay&&endedDay.TryGetValue<int>(out var day)&&day>=0&&day<_day
            &&!string.IsNullOrWhiteSpace(DecisionContextCompression.Text(record,"endedSeason"))
            &&_memory.Get(_scope,"season_progress_updated_day")!=day.ToString();
    }
    private object SeasonProgressEntry(TaskResult result,int day)=>new{
        day,taskId=result.TaskId,skill=result.Skill,completed=result.Completed,reason=result.Reason,
        details=DecisionContextCompression.Receipt(JsonSerializer.SerializeToNode(result))
    };

    private object UpdateSeasonProgress(JsonObject args)
    {
        var plan=GetSeasonPlan() as JsonObject??throw new InvalidOperationException("season_plan_missing");
        var dayRecord=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}")!;
        var endedSeason=DecisionContextCompression.Text(dayRecord,"endedSeason");
        var endedDay=dayRecord["endedDay"]?.GetValue<int>()??-1;
        if(endedDay<0||endedDay>=_day||endedSeason==""||endedSeason!=DecisionContextCompression.Text(plan,"season"))throw new InvalidOperationException("progress_must_match_ended_season");
        if(plan["year"]?.GetValue<int>()!=endedDay/112+1)throw new InvalidOperationException("progress_must_match_ended_year");
        if(args["progress"] is not JsonArray entries||entries.Any(e=>e?.GetValueKind()!=JsonValueKind.String))throw new InvalidOperationException("progress_must_be_string_array");
        if(entries.Count>0&&dayRecord["successfulResults"] is not JsonArray {Count:>0})throw new InvalidOperationException("progress_requires_verified_success_receipts");
        if(entries.Count==0&&dayRecord["successfulResults"] is JsonArray {Count:>0})throw new InvalidOperationException("verified_success_receipts_require_progress");
        var old=plan["progress"] as JsonArray??new JsonArray();
        var values=old.Select(e=>e?.ToString()??"").Concat(entries.Select(e=>e!.GetValue<string>()))
            .Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).TakeLast(64).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray();
        plan["progress"]=new JsonArray(values);
        _memory.Set(_scope,"season_plan",plan.ToJsonString());
        _memory.Set(_scope,"season_progress_updated_day",DecisionContextCompression.Text(dayRecord,"endedDay"));
        Trace("season_progress_updated",new{season=DecisionContextCompression.Text(plan,"season"),year=DecisionContextCompression.Get(plan,"year"),added=entries.Count});
        var sourceTaskIds=(dayRecord["successfulResults"] as JsonArray??new()).Select(r=>r?["taskId"]?.ToString()??r?["details"]?["taskId"]?.ToString()).Where(id=>!string.IsNullOrEmpty(id)).Distinct().ToArray();
        _memory.Set(_scope,"season_progress_evidence:"+endedDay,JsonSerializer.Serialize(new{endedDay,endedSeason,sourceTaskIds,progress=entries}));
        return new{status="updated",season=DecisionContextCompression.Text(plan,"season"),progressCount=values.Length,sourceTaskIds,fixedFieldsUnchanged=true};
    }

    private object CreateSeasonPlan(JsonObject args,GameObservation state)
    {
        var current=GetSeasonPlan() as JsonObject;
        var maintenance=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}")!;
        var targetSeason=CalendarSeason(state);
        var requested=args["season"]?.GetValue<string>()??"";
        if(requested!=targetSeason||targetSeason=="")throw new InvalidOperationException("new_plan_must_match_observed_current_season");
        if(!HasPlayerGoal)throw new InvalidOperationException("player_goal_required");
        if(SeasonPlanCurrent(state))throw new InvalidOperationException("season_plan_fields_are_locked_until_next_season_or_goal_change");
        var seasonChanged=current!=null&&(DecisionContextCompression.Text(current,"season")!=targetSeason||current["year"]?.GetValue<int>()!=state.GameDay/112+1);
        if(seasonChanged&&DecisionContextCompression.Text(maintenance,"endedSeason")!=DecisionContextCompression.Text(current,"season"))
            throw new InvalidOperationException("previous_season_must_be_reviewed_before_replacement");
        if(seasonChanged&&_memory.Get(_scope,"season_progress_updated_day")!=DecisionContextCompression.Text(maintenance,"endedDay"))
            throw new InvalidOperationException("update_previous_season_progress_before_creating_next_plan");
        var review=args["previousSeasonReview"]?.GetValue<string>()??"";
        if(string.IsNullOrWhiteSpace(review)||review.Length>4000)throw new InvalidOperationException("previous_season_review_required");
        string RequiredText(string key,int max){var value=args[key]?.GetValue<string>()??"";if(string.IsNullOrWhiteSpace(value)||value.Length>max)throw new InvalidOperationException(key+"_invalid_length");return value;}
        string[] RequiredArray(string key){if(!NonemptyStrings(args[key]))throw new InvalidOperationException(key+"_must_not_be_empty");return args[key]!.AsArray().Select(v=>v!.GetValue<string>()).ToArray();}
        if(args["progress"] is not JsonArray p||p.Any(v=>v?.GetValueKind()!=JsonValueKind.String))throw new InvalidOperationException("progress_must_be_string_array");
        var progress=p.Select(v=>v!.GetValue<string>()).ToArray();
        var year=state.GameDay/112+1;
        var record=new SeasonPlanRecord(year,targetSeason,RequiredText("goal",1000),RequiredArray("constraints"),RequiredArray("successCriteria"),progress,DateTimeOffset.UtcNow.ToString("O")){
            GoalVersion=CurrentGoalVersion,Milestones=RequiredArray("milestones"),ResourceStrategy=RequiredArray("resourceStrategy")};
        if(current!=null){
            var previous=JsonSerializer.Deserialize<SeasonPlanRecord>(current.ToJsonString(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            var reviews=JsonNode.Parse(_memory.Get(_scope,"season_plan_history")??"[]") as JsonArray??new JsonArray();
            reviews.Add(new JsonObject{["plan"]=JsonSerializer.SerializeToNode(previous),["review"]=review});
            _memory.Set(_scope,"season_plan_history",reviews.ToJsonString());
        }
        _memory.Set(_scope,"season_plan",JsonSerializer.Serialize(record,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        Trace("season_plan_created",new{record.Year,record.Season,record.Goal,record.Constraints,record.SuccessCriteria});
        Changed?.Invoke();
        return new{status="created",record,fieldsLocked=true};
    }
}
