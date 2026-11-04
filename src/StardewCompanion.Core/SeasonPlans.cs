using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed record SeasonPlanRecord(int Year,string Season,string Goal,string[] Constraints,string[] SuccessCriteria,string[] Progress,string CreatedAt);

public sealed partial class CompanionRuntime
{
    private JsonNode? GetSeasonPlan()
    {
        var raw=_memory.Get(_scope,"season_plan")??"null";
        if(JsonNode.Parse(raw) is not JsonObject stored)return null;
        var canonical=new JsonObject();
        foreach(var key in new[]{"year","season","goal","constraints","successCriteria","progress","createdAt"})
            canonical[key]=DecisionContextCompression.Get(stored,key)?.DeepClone();
        string Repair(string value){
            if(!value.Any(c=>c is >= '\u0080' and <= '\u009f')||value.Any(c=>c>'\u00ff'))return value;
            try{var decoded=new System.Text.UTF8Encoding(false,true).GetString(System.Text.Encoding.Latin1.GetBytes(value));
                return decoded.Any(c=>c is >= '\u4e00' and <= '\u9fff')?decoded:value;
            }catch(System.Text.DecoderFallbackException){return value;}
        }
        foreach(var key in canonical.Select(p=>p.Key).ToArray()){
            if(canonical[key] is JsonValue value&&value.GetValueKind()==JsonValueKind.String)canonical[key]=Repair(value.GetValue<string>());
            else if(canonical[key] is JsonArray array)for(var i=0;i<array.Count;i++)if(array[i] is JsonValue item&&item.GetValueKind()==JsonValueKind.String)array[i]=Repair(item.GetValue<string>());
        }
        var normalized=canonical.ToJsonString();
        if(raw!=normalized){
            _memory.Set(_scope,"season_plan_before_normalization:"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),raw);
            _memory.Set(_scope,"season_plan",normalized);Trace("season_plan_normalized",new{source="stored_plan",originalPreserved=true});
        }
        return canonical;
    }

    private void RecordSuccessfulDayResult(TaskResult result)
    {
        var key="verified_day_results:"+_session+":"+_day;
        var results=JsonNode.Parse(_memory.Get(_scope,key)??"[]")!.AsArray();
        var receipt=JsonSerializer.SerializeToNode(SeasonProgressEntry(result));
        if(results.Any(r=>r?["details"]?["taskId"]?.ToString()==result.TaskId))return;
        results.Add(receipt);_memory.Set(_scope,key,results.ToJsonString());
    }

    private static string CalendarSeason(GameObservation state)
    {
        try{return DecisionContextCompression.Text(JsonNode.Parse(state.WorldView)?["decisionFacts"]?["calendar"],"season");}
        catch{return "";}
    }

    private object SeasonProgressEntry(TaskResult result)=>new{
        day=_day,skill=result.Skill,completed=result.Completed,reason=result.Reason,
        details=DecisionContextCompression.Receipt(JsonSerializer.SerializeToNode(result))
    };

    private object UpdateSeasonProgress(JsonObject args)
    {
        var plan=GetSeasonPlan() as JsonObject??throw new InvalidOperationException("season_plan_missing");
        var dayRecord=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}")!;
        var endedSeason=DecisionContextCompression.Text(dayRecord,"endedSeason");
        if(endedSeason==""||endedSeason!=DecisionContextCompression.Text(plan,"season"))throw new InvalidOperationException("progress_must_match_ended_season");
        if(args["progress"] is not JsonArray entries||entries.Any(e=>e?.GetValueKind()!=JsonValueKind.String))throw new InvalidOperationException("progress_must_be_string_array");
        var old=plan["progress"] as JsonArray??new JsonArray();
        var values=old.Select(e=>e?.ToString()??"").Concat(entries.Select(e=>e!.GetValue<string>()))
            .Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).TakeLast(64).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray();
        plan["progress"]=new JsonArray(values);
        _memory.Set(_scope,"season_plan",plan.ToJsonString());
        _memory.Set(_scope,"season_progress_updated_day",DecisionContextCompression.Text(dayRecord,"endedDay"));
        Trace("season_progress_updated",new{season=DecisionContextCompression.Text(plan,"season"),year=DecisionContextCompression.Get(plan,"year"),added=entries.Count});
        return new{status="updated",season=DecisionContextCompression.Text(plan,"season"),progressCount=values.Length,fixedFieldsUnchanged=true};
    }

    private object CreateSeasonPlan(JsonObject args,GameObservation state)
    {
        var current=GetSeasonPlan() as JsonObject;
        var maintenance=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}")!;
        var targetSeason=CalendarSeason(state);
        var requested=args["season"]?.GetValue<string>()??"";
        if(requested!=targetSeason||targetSeason=="")throw new InvalidOperationException("new_plan_must_match_observed_current_season");
        if(current!=null&&DecisionContextCompression.Text(current,"season")==targetSeason)throw new InvalidOperationException("season_plan_fields_are_locked_until_next_season");
        if(current!=null&&DecisionContextCompression.Text(maintenance,"endedSeason")!=DecisionContextCompression.Text(current,"season"))
            throw new InvalidOperationException("previous_season_must_be_reviewed_before_replacement");
        if(current!=null&&_memory.Get(_scope,"season_progress_updated_day")!=DecisionContextCompression.Text(maintenance,"endedDay"))
            throw new InvalidOperationException("update_previous_season_progress_before_creating_next_plan");
        var review=args["previousSeasonReview"]?.GetValue<string>()??"";
        if(review.Length is <1 or >4000)throw new InvalidOperationException("previous_season_review_required");
        string RequiredText(string key,int max){var value=args[key]?.GetValue<string>()??"";if(value.Length is <1||value.Length>max)throw new InvalidOperationException(key+"_invalid_length");return value;}
        string[] RequiredArray(string key){if(args[key] is not JsonArray a||a.Any(v=>v?.GetValueKind()!=JsonValueKind.String))throw new InvalidOperationException(key+"_must_be_string_array");return a.Select(v=>v!.GetValue<string>()).ToArray();}
        var progress=args["progress"] is JsonArray p&&p.All(v=>v?.GetValueKind()==JsonValueKind.String)?p.Select(v=>v!.GetValue<string>()).ToArray():Array.Empty<string>();
        var year=Math.Max(1,(state.GameDay-1)/112+1);
        if(current!=null){
            var previous=JsonSerializer.Deserialize<SeasonPlanRecord>(current.ToJsonString(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            var reviews=JsonNode.Parse(_memory.Get(_scope,"season_plan_history")??"[]") as JsonArray??new JsonArray();
            reviews.Add(new JsonObject{["plan"]=JsonSerializer.SerializeToNode(previous),["review"]=review});
            _memory.Set(_scope,"season_plan_history",reviews.ToJsonString());
        }
        var record=new SeasonPlanRecord(year,targetSeason,RequiredText("goal",1000),RequiredArray("constraints"),RequiredArray("successCriteria"),progress,DateTimeOffset.UtcNow.ToString("O"));
        _memory.Set(_scope,"season_plan",JsonSerializer.Serialize(record,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        Trace("season_plan_created",new{record.Year,record.Season,record.Goal,record.Constraints,record.SuccessCriteria});
        return new{status="created",record,fieldsLocked=true};
    }
}
