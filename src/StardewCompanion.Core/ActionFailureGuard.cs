using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private sealed record ActionAttempt(string Signature,string Scope,string Session);
    private sealed record ActionFailure(string State,string Reason,int? RetryAtTimeOfDayHHMM);
    private readonly object _failureGate=new();
    private readonly Dictionary<string,ActionAttempt> _actionAttempts=new();
    private readonly Dictionary<string,ActionFailure> _actionFailures=new();
    private static bool IsNoProgressResult(TaskResult result)=>result.Status=="completed"&&result.Completed==0
        &&result.Reason=="already_satisfied_no_action";

    private static JsonNode? Canonical(JsonNode? node)=>node switch{
        JsonObject obj=>new JsonObject(obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>KeyValuePair.Create(p.Key,Canonical(p.Value)))),
        JsonArray array=>new JsonArray(array.Select(Canonical).ToArray()),
        _=>node?.DeepClone()
    };
    private static string ActionSignature(string skill,JsonObject args)
    {
        var normalized=(JsonObject)args.DeepClone();
        // planId is only a submission correlation token. A model must not bypass
        // unchanged-target failure protection by assigning the same failed
        // wrapped steps a fresh id on every decision.
        if(skill.StartsWith("group_",StringComparison.Ordinal))normalized.Remove("planId");
        return skill+":"+Canonical(normalized)!.ToJsonString();
    }
    private static bool IsShopDayClosure(string? reason)=>reason is "shop_closed_for_day" or "shop_closed_for_today" or "shop_closed_for_festival";
    private static JsonObject ShopAccess(GameObservation? state)
    {
        var calendar=JsonNode.Parse(state?.WorldView??"{}")?["decisionFacts"]?["calendar"];
        return new JsonObject{["pierreWednesdayUnlocked"]=calendar?["pierreWednesdayUnlocked"]?.DeepClone(),
            ["storesClosedForFestival"]=calendar?["storesClosedForFestival"]?.DeepClone()};
    }
    private static int? ShopRetryTime(JsonNode? node)
    {
        if(node is JsonObject obj){
            if(obj["retryAtTimeOfDayHHMM"] is JsonValue value&&value.TryGetValue<int>(out var time))return time;
            foreach(var pair in obj)if(ShopRetryTime(pair.Value) is {} found)return found;
        }else if(node is JsonArray array){foreach(var item in array)if(ShopRetryTime(item) is {} found)return found;}
        return null;
    }
    private static string FailureState(GameObservation state,string reason,int? retryAtTimeOfDayHHMM=null)
    {
        var world=JsonNode.Parse(state.WorldView) as JsonObject??new();
        if(IsShopDayClosure(reason))return new JsonObject{["day"]=state.GameDay,["menu"]=state.Menu,["shopAccess"]=ShopAccess(state)}.ToJsonString();
        // Prerequisite route failures (counter/route/entrance unreachable) mask the
        // real target and must not be re-dispatched merely because movement altered
        // stamina, tile, or inventory. Key them on stable shop-availability facts so
        // an unchanged failed intent stays blocked across movement.
        if(reason.StartsWith("shop_counter_unreachable",StringComparison.Ordinal)
            ||reason.StartsWith("shop_route_unavailable",StringComparison.Ordinal)
            ||reason.StartsWith("shop_entrance_unreachable",StringComparison.Ordinal))
            return Canonical(new JsonObject{["day"]=state.GameDay,["menu"]=state.Menu,
                ["shopAccess"]=ShopAccess(state),["npcs"]=world["npcs"]?.DeepClone()})!.ToJsonString();
        // Clock and player position alone do not resolve a failed target. Timed
        // availability failures may retry when the clock advances; day closures may not.
        var facts=new JsonObject{["day"]=state.GameDay,["location"]=state.Location,["menu"]=state.Menu,
            ["stamina"]=state.Stamina,["health"]=state.Health};
        foreach(var key in new[]{"inventory","inventoryState","soil","forage","naturalObstacles","chests","water","money","toolUpgrade","buildings","animals","knownRecipes","homeUpgradeLevel"})facts[key]=world[key]?.DeepClone();
        if(reason.StartsWith("navigation_",StringComparison.Ordinal)||reason.StartsWith("named_",StringComparison.Ordinal))facts["npcs"]=world["npcs"]?.DeepClone();
        if(reason.Contains("npc")||reason.Contains("animal")||reason.Contains("pet_")){
            facts["npcs"]=world["npcs"]?.DeepClone();facts["pets"]=world["pets"]?.DeepClone();facts["time"]=state.TimeOfDay;
        }
        if(reason is "shop_owner_unavailable" or "shop_interaction_failed" or "shop_unavailable_native_condition"
            ||reason.StartsWith("shop_counter_unreachable",StringComparison.Ordinal)){
            facts["npcs"]=world["npcs"]?.DeepClone();
            // A changed player tile does not prove that an unreachable native
            // counter became reachable; otherwise the same failed continuation
            // can immediately re-submit its prerequisite and target action.
            if(!reason.StartsWith("shop_counter_unreachable",StringComparison.Ordinal))
                facts["tile"]=System.Text.Json.JsonSerializer.SerializeToNode(state.Tile);
        }
        if(reason=="shop_not_open_yet"&&retryAtTimeOfDayHHMM is {} retryAt)facts["retryWindowReached"]=state.TimeOfDay>=retryAt;
        else if(reason=="shop_unavailable_native_condition")facts["time"]=state.TimeOfDay;
        else if(!IsShopDayClosure(reason)&&(reason.Contains("shop_closed")||reason.Contains("not_open")||reason.Contains("not_ready")))facts["time"]=state.TimeOfDay;
        return Canonical(facts)!.ToJsonString();
    }
    private void BeginAction(string taskId,string skill,JsonObject arguments,GameObservation state)
    {
        var signature=ActionSignature(skill,arguments);
        lock(_failureGate){
            if(_actionFailures.TryGetValue(signature,out var failure)&&failure.State==FailureState(state,failure.Reason,failure.RetryAtTimeOfDayHHMM))
                throw new InvalidOperationException("unchanged_target_previously_failed:"+failure.Reason+":目标及相关状态未变化，先解决阻塞或选择其他工作");
            _actionAttempts[taskId]=new(signature,_scope,_session);
        }
    }
    private void FinishAction(TaskResult result)
    {
        lock(_failureGate){
            if(!_actionAttempts.Remove(result.TaskId,out var attempt)||attempt.Scope!=_scope||attempt.Session!=_session)return;
            if(result.Status=="completed"&&!IsNoProgressResult(result))_actionFailures.Remove(attempt.Signature);
            else if((result.Status is "failed" or "partial" or "rejected"||IsNoProgressResult(result))&&_bridge.Observation is {} state){
                _lastFailureReason=result.Reason;
                int? retryAt=null;
                if(result.Reason=="shop_not_open_yet"){
                    try{retryAt=ShopRetryTime(JsonNode.Parse(result.Details));}catch(System.Text.Json.JsonException){ }
                }
                _actionFailures[attempt.Signature]=new(FailureState(state,result.Reason,retryAt),result.Reason,retryAt);
                while(_actionFailures.Count>128)_actionFailures.Remove(_actionFailures.Keys.First());
            }
        }
    }
    private void ForgetAction(string taskId){lock(_failureGate)_actionAttempts.Remove(taskId);}
    private void ResetActionFailures(){lock(_failureGate){_actionAttempts.Clear();_actionFailures.Clear();_lastFailureReason="";}}
}
