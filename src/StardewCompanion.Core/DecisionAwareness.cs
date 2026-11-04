using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private readonly object _awarenessGate=new();
    private string _awarenessScope="";
    private JsonObject _awareness=new();
    private void LoadAwareness()
    {
        if(_awarenessScope==_scope)return;
        _awarenessScope=_scope;
        _awareness=JsonNode.Parse(_memory.Get(_scope,"decision_awareness")??"null") as JsonObject??new JsonObject{
            ["revision"]=0L,["actionFeedback"]=new JsonObject{["revision"]=0L,["deliveredRevision"]=0L,["blockers"]=new JsonArray()},
            ["pendingEvents"]=new JsonArray(),["deliveredEvents"]=new JsonArray(),["seenEventIds"]=new JsonArray()};
    }
    private void SaveAwareness()=>_memory.Set(_scope,"decision_awareness",_awareness.ToJsonString());
    private void RetireWorkingBlockers()
    {
        lock(_awarenessGate){LoadAwareness();
            var feedback=_awareness["actionFeedback"]!.AsObject();feedback["blockers"]=new JsonArray();feedback["latest"]=null;
            var revision=feedback["revision"]!.GetValue<long>()+1;feedback["revision"]=revision;feedback["deliveredRevision"]=revision;
            _awareness["revision"]=_awareness["revision"]!.GetValue<long>()+1;SaveAwareness();
        }
    }
    public JsonObject AwarenessSnapshot()
    {
        lock(_awarenessGate){LoadAwareness();var copy=(JsonObject)_awareness.DeepClone();copy.Remove("seenEventIds");
            if(copy["actionFeedback"]?["blockers"] is JsonArray blockers)foreach(var b in blockers.OfType<JsonObject>())b.Remove("stateKey");return copy;}
    }
    private static string StateKey(GameObservation? state)
    {
        if(state==null)return "";
        var w=JsonNode.Parse(state.WorldView);
        var json=JsonSerializer.Serialize(new{state.Location,state.Tile,inventory=w?["inventory"],soil=w?["soil"],forage=w?["forage"],obstacles=w?["naturalObstacles"]});
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }
    private void RecordActionFeedback(object value)
    {
        lock(_awarenessGate){
            if(_scope=="")return;LoadAwareness();
            var node=JsonSerializer.SerializeToNode(value)!.AsObject();
            node["session"]=_session;node["gameDay"]=_day;node["time"]=_bridge.Observation?.TimeOfDay;
            if(node["results"] is JsonArray results)foreach(var entry in results)
                if(entry is JsonObject e&&e["receipt"] is {} receipt)e["receipt"]=DecisionContextCompression.Receipt(receipt);
            var feedback=_awareness["actionFeedback"]!.AsObject();
            feedback["latest"]=node.DeepClone();feedback["revision"]=feedback["revision"]!.GetValue<long>()+1;
            var receiptNode=node["result"]??(node["results"] as JsonArray)?.LastOrDefault()?["receipt"];
            var reason=DecisionContextCompression.Text(node,"reason");
            if(reason=="")reason=DecisionContextCompression.Text(receiptNode,"reason");
            var status=DecisionContextCompression.Text(node,"status");
            if(status=="")status=DecisionContextCompression.Text(receiptNode,"status");
            if(status is "failed" or "partial" or "rejected" or "stopped" or "duplicate_not_executed"){
                var target=DecisionContextCompression.Get(receiptNode,"requestedTarget")??(node["unexecuted"] as JsonArray)?.FirstOrDefault()?["Arguments"];
                var skill=DecisionContextCompression.Text(receiptNode,"skill");
                if(skill=="")skill=(node["unexecuted"] as JsonArray)?.FirstOrDefault()?["Skill"]?.ToString()??"";
                if(reason.StartsWith("unchanged_target_previously_failed")&&node["originalFailure"] is {} original)reason=original.ToString();
                var key=skill+":"+target?.ToJsonString()+":"+(reason.Contains("inventory_full")?"inventory_full":reason);
                var blockers=feedback["blockers"]!.AsArray();
                var existing=blockers.OfType<JsonObject>().FirstOrDefault(b=>b["key"]?.ToString()==key);
                if(existing!=null){
                    var taskId=DecisionContextCompression.Text(receiptNode,"taskId");
                    if(taskId==""||existing["taskId"]?.ToString()!=taskId)existing["count"]=existing["count"]!.GetValue<int>()+1;
                    existing["taskId"]=taskId;existing["latestReason"]=reason;
                }
                else blockers.Add(new JsonObject{["key"]=key,["reason"]=reason,["skill"]=skill,["target"]=target?.DeepClone(),
                    ["blockedDay"]=_day,["evidence"]=DecisionContextCompression.Get(receiptNode,"businessDetails")?.DeepClone(),
                    ["taskId"]=DecisionContextCompression.Text(receiptNode,"taskId"),["count"]=1,["location"]=_bridge.Observation?.Location,["stateKey"]=StateKey(_bridge.Observation),
                    ["meaning"]=reason.Contains("inventory_full")?"目标物品未能采入背包；先释放容量，计划未执行步骤仍未完成":"本次动作未完全成功；检查原始原因及未执行步骤"});
            }
            _awareness["revision"]=_awareness["revision"]!.GetValue<long>()+1;SaveAwareness();
        }
    }
    private void ObserveAwareness(GameObservation state)
    {
        string[] ack;
        lock(_awarenessGate){
            LoadAwareness();var changed=false;
            var w=JsonNode.Parse(state.WorldView) as JsonObject??new();
            var events=w["gameEvents"] as JsonArray??new();
            var seen=_awareness["seenEventIds"]!.AsArray();var known=seen.Select(e=>e!.ToString()).ToHashSet();
            ack=events.Select(e=>e?["id"]?.ToString()??"").Where(id=>id!="").ToArray();
            foreach(var e in events){var id=e?["id"]?.ToString();if(id==null||!known.Add(id))continue;
                seen.Add(id);_awareness["pendingEvents"]!.AsArray().Add(e!.DeepClone());changed=true;Trace("game_event_received",e!);}
            var blockers=_awareness["actionFeedback"]!["blockers"]!.AsArray();
            foreach(var b in blockers.OfType<JsonObject>().ToArray()){
                var inventoryFailure=b["reason"]!.ToString().Contains("inventory_full");
                var resolved=false;
                if(inventoryFailure){
                    resolved=(w["inventoryState"]?["freeSlots"]?.GetValue<int>()??0)>0;
                    if(b["location"]?.ToString()==state.Location&&b["target"] is JsonObject t&&w["forage"] is JsonArray forage){
                        var x=t["x"]?.GetValue<int>();var y=t["y"]?.GetValue<int>();
                        if(x.HasValue&&y.HasValue&&b["skill"]?.ToString()=="forage_area"){
                            var width=t["width"]?.GetValue<int>()??1;var height=t["height"]?.GetValue<int>()??1;
                            var targets=forage.Where(f=>(f?["x"]?.GetValue<int>()??-1)>=x&&(f?["x"]?.GetValue<int>()??-1)<x+width
                                &&(f?["y"]?.GetValue<int>()??-1)>=y&&(f?["y"]?.GetValue<int>()??-1)<y+height).ToArray();
                            resolved|=targets.Length==0||targets.All(f=>f?["canAccept"]?.GetValue<bool>()==true);
                        }
                    }
                }else if(b["reason"]?.ToString()=="shop_closed_for_day")resolved=b["blockedDay"]?.GetValue<int>()!=state.GameDay;
                else resolved=b["stateKey"]?.ToString()!=StateKey(state);
                if(resolved){blockers.Remove(b);changed=true;Trace("blocker_resolved",b);}
            }
            if(changed){_triggerKind="important_event";_awareness["revision"]=_awareness["revision"]!.GetValue<long>()+1;SaveAwareness();WakeDecision();}
        }
        // Ack intake only after durable storage; delivery to the LLM is tracked separately.
        if(ack.Length>0)_=_bridge.AcknowledgeEvents(ack);
    }
    private void ConfirmAwarenessDelivery(JsonObject snapshot)
    {
        lock(_awarenessGate){
            if(_awarenessScope!=_scope)return;
            _awareness["actionFeedback"]!["deliveredRevision"]=snapshot["actionFeedback"]?["revision"]?.DeepClone();
            var pending=_awareness["pendingEvents"]!.AsArray();var delivered=_awareness["deliveredEvents"]!.AsArray();
            var ids=(snapshot["pendingEvents"] as JsonArray??new()).Select(e=>e?["id"]?.ToString()).ToHashSet();
            foreach(var e in pending.ToArray())if(ids.Contains(e?["id"]?.ToString())){pending.Remove(e);delivered.Add(e);}
            while(delivered.Count>64)delivered.RemoveAt(0);SaveAwareness();
        }
    }
}
