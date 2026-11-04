using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed class PlayerGoalUpdate
{
    public string Objective {get;set;}="";
    public string CompletionCondition {get;set;}="";
    public string[] Priorities {get;set;}=Array.Empty<string>();
    public string[] Restrictions {get;set;}=Array.Empty<string>();
    public string Autonomy {get;set;}="除矿洞外的游戏功能均已开放，由模型自主决定工作与休息。";
    public string[] DisabledSkills {get;set;}=new[]{"mine_travel"};
    public int? ExpectedVersion {get;set;}
}

public sealed partial class CompanionRuntime
{
    private static readonly JsonSerializerOptions InputJson=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private readonly object _inputGate=new();
    private bool _expandedTools;
    private string[] _scopedPlanSkills=Array.Empty<string>();
    private string _triggerKind="state_changed";
    private JsonObject _lastInput=new();
    public JsonObject InputPreview {get {lock(_inputGate)return (JsonObject)_lastInput.DeepClone();}}
    public JsonObject PlayerGoal
    {
        get {lock(_inputGate){
            if(_scope=="")return new JsonObject();
            if(JsonNode.Parse(_memory.Get(_scope,"player_goal")??"null") is JsonObject goal)return goal;
            var legacy=JsonNode.Parse(_memory.Get(_scope,"player_instructions")??"[]") as JsonArray??new();
            var migrated=JsonSerializer.SerializeToNode(new PlayerGoalUpdate{Objective=string.Join("\n",legacy.Select(v=>v?.ToString()??""))},InputJson)!.AsObject();
            migrated.Remove("expectedVersion");
            migrated["originalInstructions"]=legacy.DeepClone();migrated["version"]=1;migrated["status"]=legacy.Count>0?"active":"unset";
            _memory.Set(_scope,"player_goal",migrated.ToJsonString());return migrated;
        }}
    }
    public async Task<JsonObject> UpdateGoal(PlayerGoalUpdate update)
    {
        if(_scope=="")throw new InvalidOperationException("请先连接游戏");
        var expectedScope=_scope;var expectedSession=_session;
        if(string.IsNullOrWhiteSpace(update.Objective)||update.Objective.Length>16000)throw new ArgumentException("目标原文长度须为1至16000字符");
        foreach(var list in new[]{update.Priorities,update.Restrictions,update.DisabledSkills})
            if(list==null||list.Length>32||list.Any(s=>s==null||s.Length>2000))throw new ArgumentException("目标列表最多32项，每项最多2000字符");
        if(update.CompletionCondition==null||update.Autonomy==null)throw new ArgumentException("完成条件和授权说明须为文本");
        if(update.CompletionCondition.Length>4000||update.Autonomy.Length>4000)throw new ArgumentException("完成条件和授权说明最多4000字符");
        JsonObject goal;
        lock(_inputGate){
            if(expectedScope!=_scope||expectedSession!=_session)throw new InvalidOperationException("游戏会话已变化，请重新加载目标");
            var old=PlayerGoal;
            if(update.ExpectedVersion.HasValue&&update.ExpectedVersion.Value!=(old["version"]?.GetValue<int>()??0))throw new InvalidOperationException("长期目标已在其他入口更新，请重新加载后保存");
            goal=JsonSerializer.SerializeToNode(update,InputJson)!.AsObject();goal.Remove("expectedVersion");
            goal["version"]=(old["version"]?.GetValue<int>()??0)+1;goal["status"]="active";
            goal["originalInstructions"]=new JsonArray(update.Objective);
            _memory.Set(_scope,"player_goal",goal.ToJsonString());
            // A replacement goal retires an earlier question. This does not grant
            // any permission; action-level policies still require their own authorization.
            PendingQuestion="";_memory.Set(_scope,"pendingQuestion","");
            var work=WorkState();work["currentObjective"]="";work["objectiveBasis"]="";work["next"]=new JsonArray();
            work["goalVersion"]=goal["version"]!.DeepClone();_memory.Set(_scope,"agent_working_state",work.ToJsonString());
            _expandedTools=false;_scopedPlanSkills=Array.Empty<string>();_triggerKind="goal_updated";
        }
        if(expectedScope==_scope&&expectedSession==_session){
            RetireWorkingBlockers();Invalidate();if(_bridge.Observation?.ActiveTaskId is {Length:>0})await _bridge.Control("cancel");
            WakeDecision();Changed?.Invoke();
        }
        Decisions.Write(expectedScope,expectedSession,"","player_goal_updated",goal);
        _memory.ArchiveDiagnostic(expectedScope,expectedSession,"player_goal_updated","",goal.ToJsonString());return goal;
    }
    private JsonObject WorkState()
    {
        if(JsonNode.Parse(_memory.Get(_scope,"agent_working_state")??"null") is JsonObject saved)return saved;
        var latest=Plans.OrderByDescending(p=>p.UpdatedAt).FirstOrDefault();
        var work=new JsonObject{["currentObjective"]=latest?.Goal??"",["objectiveBasis"]=latest?.Basis??"",
            ["next"]=JsonSerializer.SerializeToNode((latest?.Pending??Array.Empty<string>()).Take(8).ToArray()),["confirmedMilestones"]=new JsonArray()};
        _memory.Set(_scope,"agent_working_state",work.ToJsonString());return work;
    }
    private void ReconcileWorkingSession(GameObservation observation)
    {
        lock(_inputGate){var work=WorkState();
            if(work["session"]?.ToString()!=observation.SessionId){
                work["confirmedMilestones"]=new JsonArray();work["session"]=observation.SessionId;
                work["currentObjective"]="";work["objectiveBasis"]="";work["next"]=new JsonArray();work["activePlanId"]="";
                _memory.Set(_scope,"agent_working_state",work.ToJsonString());
            }
            _lastInput=new JsonObject();
        }
    }
    private void RetireWorkingDay()
    {
        lock(_inputGate){var work=WorkState();work["currentObjective"]="";work["objectiveBasis"]="";work["next"]=new JsonArray();work["activePlanId"]="";
            _memory.Set(_scope,"agent_working_state",work.ToJsonString());}
    }
    private void RetireWorkingBatch(string planId)
    {
        lock(_inputGate){var work=WorkState();if(work["activePlanId"]?.ToString()!=planId)return;
            work["next"]=new JsonArray();work["activePlanId"]="";_memory.Set(_scope,"agent_working_state",work.ToJsonString());}
    }
    private void CancelInputState()
    {
        lock(_inputGate){
            var goal=PlayerGoal;goal["status"]="cancelled";goal["version"]=(goal["version"]?.GetValue<int>()??0)+1;
            _memory.Set(_scope,"player_goal",goal.ToJsonString());_memory.Set(_scope,"conversation_outbox","[]");
            var work=WorkState();work["currentObjective"]="";work["objectiveBasis"]="";work["next"]=new JsonArray();
            _memory.Set(_scope,"agent_working_state",work.ToJsonString());_expandedTools=false;_scopedPlanSkills=Array.Empty<string>();
        }
    }
    private void UpdateWorkingPlan(JsonObject args,bool replaceObjective)
    {
        lock(_inputGate){var work=WorkState();
            if(replaceObjective||string.IsNullOrEmpty(work["currentObjective"]?.ToString())){
                work["currentObjective"]=DecisionContextCompression.Text(args,"goal");work["objectiveBasis"]=DecisionContextCompression.Text(args,"basis");
            }
            if(args["pending"] is JsonArray next)work["next"]=new JsonArray(next.Take(8).Select(v=>v?.DeepClone()).ToArray());
            work["activePlanId"]=args["planId"]?.DeepClone();
            work["goalVersion"]=PlayerGoal["version"]?.DeepClone();
            _memory.Set(_scope,"agent_working_state",work.ToJsonString());
        }
    }
    private void RecordWorkingResult(TaskResult result)
    {
        if(result.Status is not ("completed" or "partial"))return;
        if(result.Completed<=0&&result.Reason is not ("destination_observed" or "chest_bound" or "new_day_observed"))return;
        lock(_inputGate){var work=WorkState();var milestones=work["confirmedMilestones"]!.AsArray();
            var receipt=DecisionContextCompression.Receipt(JsonSerializer.SerializeToNode(result));
            var milestone=new JsonObject{["taskId"]=result.TaskId,["skill"]=result.Skill,["status"]=result.Status,["reason"]=result.Reason,
                ["completed"]=result.Completed,["gameDay"]=_day,["observationRevision"]=result.ObservationRevision,
                ["location"]=receipt["location"]?.DeepClone(),["target"]=receipt["requestedTarget"]?.DeepClone(),["verifiedBy"]="native_execution_receipt"};
            var duplicate=milestones.FirstOrDefault(m=>m?["skill"]?.ToString()==result.Skill&&m?["target"]?.ToJsonString()==milestone["target"]?.ToJsonString());
            if(duplicate!=null)milestones.Remove(duplicate);milestones.Add(milestone);while(milestones.Count>16)milestones.RemoveAt(0);
            _memory.Set(_scope,"agent_working_state",work.ToJsonString());
        }
    }
    private string QueueConversation(string message)
    {
        var id=Guid.NewGuid().ToString("N");
        lock(_inputGate){var items=JsonNode.Parse(_memory.Get(_scope,"conversation_outbox")??"[]")!.AsArray();
            items.Add(new JsonObject{["id"]=id,["text"]=message,["at"]=DateTimeOffset.UtcNow.ToString("o"),
                ["answerTo"]=PendingQuestion==""?null:JsonValue.Create(PendingQuestion)});
            _memory.Set(_scope,"conversation_outbox",items.ToJsonString());_triggerKind="player_message";
        }
        return id;
    }
    private JsonObject ConversationSnapshot()
    {
        lock(_inputGate)return new JsonObject{["newMessages"]=JsonNode.Parse(_memory.Get(_scope,"conversation_outbox")??"[]"),
            ["unansweredQuestions"]=PendingQuestion==""?new JsonArray():new JsonArray(PendingQuestion)};
    }
    private QueuedInstruction? LatestPendingConversation()
    {
        lock(_inputGate){var items=JsonNode.Parse(_memory.Get(_scope,"conversation_outbox")??"[]") as JsonArray??new();
            var latest=items.LastOrDefault() as JsonObject;var text=latest?["text"]?.ToString();var id=latest?["id"]?.ToString();
            return string.IsNullOrWhiteSpace(text)||string.IsNullOrWhiteSpace(id)?null:new(_scope,_session,text,false,id);
        }
    }
    private void ConfirmConversation(JsonObject snapshot)
    {
        lock(_inputGate){var ids=snapshot["newMessages"]!.AsArray().Select(m=>m?["id"]?.ToString()).ToHashSet();
            var items=JsonNode.Parse(_memory.Get(_scope,"conversation_outbox")??"[]")!.AsArray();
            var removed=new List<string>();
            foreach(var item in items.ToArray())if(ids.Contains(item?["id"]?.ToString())){removed.Add(item!["id"]!.ToString());items.Remove(item);}
            if(removed.Count>0){_memory.Set(_scope,"conversation_outbox",items.ToJsonString());Trace("conversation_delivered",new{messageIds=removed});}
        }
    }
    private void ConfirmConversationMessage(string id)
    {
        lock(_inputGate){var items=JsonNode.Parse(_memory.Get(_scope,"conversation_outbox")??"[]")!.AsArray();
            var item=items.FirstOrDefault(m=>m?["id"]?.ToString()==id);if(item==null)return;items.Remove(item);
            _memory.Set(_scope,"conversation_outbox",items.ToJsonString());Trace("conversation_delivered",new{messageIds=new[]{id}});
        }
    }
    private static bool MiningSkill(string skill)=>skill.Contains("mine",StringComparison.OrdinalIgnoreCase)||skill.Contains("cavern",StringComparison.OrdinalIgnoreCase);
    private HashSet<string> DisabledSkills()=>new((PlayerGoal["DisabledSkills"] as JsonArray??PlayerGoal["disabledSkills"] as JsonArray??new()).Select(v=>v!.ToString()).Append("mine_travel"),StringComparer.Ordinal);
    private void ValidateSkillScope(string skill,JsonObject? args=null)
    {
        if(MiningSkill(skill)||DisabledSkills().Contains(skill))throw new InvalidOperationException("skill_disabled:"+skill);
        foreach(var field in new[]{"location","destination","toLocation"}){
            var location=args?[field]?.ToString()??"";
            if(location is "Mine" or "SkullCave" or "SkullCavern" or "VolcanoDungeon" or "BugLand"||location.StartsWith("UndergroundMine",StringComparison.Ordinal))
                throw new InvalidOperationException("location_disabled:"+location);
        }
    }
    private JsonArray DecisionTools(GameObservation state)
    {
        var all=ToolCatalog.Definitions(false,state.Capabilities);
        var w=JsonNode.Parse(state.WorldView) as JsonObject??new();var facts=w["decisionFacts"];
        var work=WorkState();var intent=(work["currentObjective"]?.ToString()??"")+(PlayerGoal["objective"]?.ToString()??"");
        var skills=new HashSet<string>(new[]{"go_to","return_home","sleep","forage_area"});
        skills.UnionWith(_scopedPlanSkills);
        var farming=state.Location=="Farm"||intent.Contains("种")||intent.Contains("作物")||intent.Contains("开荒")||intent.Contains("farm",StringComparison.OrdinalIgnoreCase);
        if(farming)skills.UnionWith(new[]{"water_area","harvest_area","refill_water","cultivate_plot","maintain_plot","harvest_and_replant","gather_resource","clear_area","till_area","plant_area"});
        if(_memory.Chests(_scope).Length>0)
            skills.UnionWith(new[]{"deposit_items","withdraw_items"});
        skills.UnionWith(new[]{"craft_items","craft_and_place"});
        if(state.Menu!="")skills.Add("respond_dialogue");
        if(intent.Contains("钓")||intent.Contains("fish",StringComparison.OrdinalIgnoreCase))skills.Add("fish_at");
        if((w["combat"]?["visibleMonsters"] as JsonArray)?.Count>0||intent.Contains("战斗"))skills.Add("fight_enemies");
        if(state.TimeOfDay>=2200||state.Stamina<40)skills.Add("recover");
        var queries=new HashSet<string>(new[]{"observe","survey_area","inspect_area","inspect_target","inspect_targets","inspect_route","ask_user","wait_for_change","list_capabilities","save_plan","read_context_history"});
        if((w["mailbox"]?["unreadCount"]?.GetValue<int>()??0)>0)queries.Add("read_mailbox");
        var disabled=DisabledSkills();var output=new JsonArray();
        foreach(var tool in all){var clone=tool!.DeepClone();var f=clone["function"]!;var name=f["name"]!.ToString();
            if((name is "deposit_items" or "withdraw_items")&&_memory.Chests(_scope).Length==0)continue;
            if(MiningSkill(name)||disabled.Contains(name))continue;
            if(name.StartsWith("group_",StringComparison.Ordinal)){
                var variants=f["parameters"]!["properties"]!["steps"]!["items"]!["oneOf"]!.AsArray();
                foreach(var variant in variants.ToArray()){
                    var skill=variant!["properties"]!["skill"]!["enum"]![0]!.ToString();
                    var needsBoundChest=skill is "deposit_items" or "withdraw_items";
                    if(MiningSkill(skill)||disabled.Contains(skill)||needsBoundChest&&_memory.Chests(_scope).Length==0)variants.Remove(variant);
                }
                if(variants.Count==0)continue;
            }else if(!_expandedTools&&!queries.Contains(name)&&!ToolCatalog.TopLevelFrequentTools.Contains(name))continue;
            output.Add(clone);
        }
        return output;
    }
    private static JsonObject ScopeFor(JsonArray tools)
    {
        var skills=tools.SelectMany(t=>{
            var name=t?["function"]?["name"]?.ToString()??"";
            if(name.StartsWith("group_",StringComparison.Ordinal))return (t?["function"]?["parameters"]?["properties"]?["steps"]?["items"]?["oneOf"] as JsonArray??new()).Select(s=>s?["properties"]?["skill"]?["enum"]?[0]?.ToString()??"");
            return ToolCatalog.Actions.Contains(name)||BusinessCapabilities.Find(name) is {Query:false}?new[]{name}:Array.Empty<string>();
        }).Where(s=>s!="").Distinct();
        return new JsonObject{["allowedSkillsThisTurn"]=new JsonArray(skills.Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray()),
            ["availableQueries"]=new JsonArray(tools.Select(t=>t?["function"]?["name"]?.ToString()??"").Where(n=>ToolCatalog.Queries.Contains(n)||BusinessCapabilities.Find(n) is {Query:true}||n is "read_context_history" or "list_capabilities").Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray()),
            ["availableTools"]=new JsonArray(tools.Select(t=>t?["function"]?["name"]?.DeepClone()).ToArray()),
            ["capabilityAccess"]="八个类别工具对应不同工作域；调用list_capabilities展开完整技能范围",["historyAccess"]="read_context_history"};
    }
    private JsonObject BuildDecisionInput(GameObservation state,JsonObject delivery,JsonObject conversation,JsonArray tools,string reason)
    {
        lock(_inputGate){
            if(state.SaveId+":"+state.PlayerId!=_scope||state.SessionId!=_session)throw new OperationCanceledException("decision_session_changed");
            var work=WorkState();var feedback=delivery["actionFeedback"];
            work["mode"]=Mode;work["running"]=_resumed;
            var allowedSkills=ScopeFor(tools)["allowedSkillsThisTurn"]!.AsArray().Select(v=>v!.ToString()).ToHashSet();
            work["confirmedMilestones"]=new JsonArray(work["confirmedMilestones"]!.AsArray().Where(m=>allowedSkills.Contains(m?["skill"]?.ToString()??"")||m?["reason"]?.ToString()=="chest_bound").TakeLast(16).Select(m=>m?.DeepClone()).ToArray());
            work["phase"]=PendingQuestion!=""?"awaiting_player":(feedback?["blockers"] as JsonArray)?.Count>0?"blocked":state.ActiveTaskId!=""?"executing":"deciding";
            work["blocked"]=feedback?["blockers"]?.DeepClone()??new JsonArray();
            work["lastAction"]=feedback?["latest"]?.DeepClone();
            var events=(JsonArray)(delivery["pendingEvents"]?.DeepClone()??new JsonArray());
            if(feedback?["latest"] is {} last&&feedback["revision"]?.ToString()!=feedback["deliveredRevision"]?.ToString())
                events.Add(new JsonObject{["kind"]="action_result",["status"]=last["status"]?.DeepClone()??last["result"]?["status"]?.DeepClone(),
                    ["reason"]=last["reason"]?.DeepClone()??last["result"]?["reason"]?.DeepClone(),["detailsReference"]="agentState.lastAction"});
            var memories=JsonSerializer.SerializeToNode(_memory.Relevant(_scope,work["currentObjective"]?.ToString()??""))!.AsArray();
            var relevant=memories.Where(m=>DecisionContextCompression.Text(m,"kind")=="routine").Take(3).Select(m=>new JsonObject{
                ["id"]=DecisionContextCompression.Get(m,"id")?.DeepClone(),["text"]=DecisionContextCompression.Decode(DecisionContextCompression.Get(m,"text")),["source"]=DecisionContextCompression.Get(m,"source")?.DeepClone()}).ToArray();
            var goal=PlayerGoal;goal.Remove("originalInstructions");
            var pendingEvents=delivery["pendingEvents"] as JsonArray??new();
            var kind=(conversation["newMessages"] as JsonArray)?.Count>0?"player_message":pendingEvents.Any(e=>e?["type"]?.ToString()=="day_changed")?"new_day":
                pendingEvents.Count>0?"important_event":feedback?["revision"]?.ToString()!=feedback?["deliveredRevision"]?.ToString()?"task_terminal":_triggerKind;
            var triggerReason=kind switch{
                "player_message"=>"玩家发来新消息", "goal_updated"=>"玩家更新了长期目标", "new_day"=>"进入新的游戏日", "task_terminal"=>"本地操作已返回结果",
                "no_action_retry"=>"上一轮只叙述计划但未选择可执行工具；请选择一个下一步工具，或明确调用wait_for_change等待条件",
                "important_event"=>"收到游戏事件或操作结果", "wait_condition_changed"=>"等待条件已变化", "session_started"=>"连接当前游戏会话", _=>"继续推进当前工作"};
            var season=GetSeasonPlan();
            if(season is JsonObject seasonObject)work["seasonPlan"]=seasonObject;
            var input=new JsonObject{
                ["trigger"]=new JsonObject{["kind"]=kind,["gameDay"]=state.GameDay,["reason"]=triggerReason},
                ["playerGoal"]=goal,["conversation"]=conversation.DeepClone(),["agentState"]=work,
                ["world"]=ProjectWorld(state,tools),["recentEvents"]=events,
                ["relevantMemories"]=new JsonArray(relevant.Select(m=>(JsonNode?)m).ToArray()),["decisionScope"]=ScopeFor(tools)};
            var memoryList=input["relevantMemories"]!.AsArray();
            while(input.ToJsonString(DecisionContextCompression.ReadableJson).Length>32000&&memoryList.Count>0)memoryList.RemoveAt(memoryList.Count-1);
            var size=input.ToJsonString(DecisionContextCompression.ReadableJson).Length;
            Trace("input_projection",new{characters=size,softLimit=32000,overBudget=size>32000,toolsCharacters=tools.ToJsonString(DecisionContextCompression.ReadableJson).Length});
            _lastInput=(JsonObject)input.DeepClone();return input;
        }
    }
    private JsonObject ProjectWorld(GameObservation state,JsonArray tools)
    {
        var w=JsonNode.Parse(state.WorldView) as JsonObject??new();var facts=w["decisionFacts"];
        var world=new JsonObject{["observationRevision"]=state.Revision,["gameDay"]=state.GameDay,["time"]=$"{state.TimeOfDay/100:D2}:{state.TimeOfDay%100:D2}",
            ["location"]=state.Location,["tile"]=JsonSerializer.SerializeToNode(state.Tile),["stamina"]=state.Stamina,["health"]=state.Health,
            ["maxStamina"]=facts?["maxStamina"]?.DeepClone()??w["maxStamina"]?.DeepClone(),["maxHealth"]=facts?["maxHealth"]?.DeepClone(),
            ["money"]=w["money"]?.DeepClone(),["rain"]=w["rain"]?.DeepClone(),["inventoryState"]=w["inventoryState"]?.DeepClone(),
            ["mailbox"]=w["mailbox"]?.DeepClone(),["menu"]=state.Menu,["menuContext"]=JsonNode.Parse(state.MenuContext),
            ["calendar"]=facts?["calendar"]?.DeepClone(),["risks"]=new JsonObject{["nativeForcedPassOutTime"]=facts?["nativeForcedPassOutTime"]?.DeepClone(),
                ["gameMinutesUntil2AM"]=facts?["gameMinutesUntil2AM"]?.DeepClone(),["staminaUntilNativePassOut"]=facts?["staminaUntilNativePassOut"]?.DeepClone(),["consequences"]=facts?["consequences"]?.DeepClone()},
            ["authorizedChests"]=JsonSerializer.SerializeToNode(_memory.Chests(_scope).OrderBy(c=>c.Location==state.Location?0:1).Take(4).ToArray()),
            ["authorizedChestCount"]=_memory.Chests(_scope).Length,["farm"]=facts?["knownFarmProgress"]?["farm"]?.DeepClone()};
        if(world["farm"] is JsonObject farm&&state.Location!="Farm"){
            farm.Remove("chests");farm["cropTiles"]="unknown_from_current_location";farm["source"]="native_farm_summary";
        }
        world["inventoryRelevant"]=w["inventory"]?.DeepClone();
        foreach(var name in new[]{"soil","forage","looseDrops","naturalObstacles","routes","home","water","npcs","chests","animals","pets","buildings","toolUpgrade","knownRecipes"}){
            if(w[name] is JsonArray list){
                var limit=name=="soil"?64:24;
                var ordered=list.OrderBy(v=>Math.Abs((v?["x"]?.GetValue<int>()??state.Tile.X)-state.Tile.X)+Math.Abs((v?["y"]?.GetValue<int>()??state.Tile.Y)-state.Tile.Y)).Take(limit).ToArray();
                world[name]=new JsonObject{["location"]=state.Location,["observedRevision"]=state.Revision,["total"]=list.Count,["truncated"]=list.Count>limit,["items"]=new JsonArray(ordered.Select(v=>v?.DeepClone()).ToArray())};
            }else if(w[name]!=null)world[name]=w[name]!.DeepClone();
        }
        var factsList=new JsonArray();
        foreach(var query in _queryFacts){var q=JsonSerializer.SerializeToNode(query)!;
            if(q["observedDay"]?.ToString()!=state.GameDay.ToString()||q["location"]?.ToString()!=state.Location)continue;
            factsList.Add(new JsonObject{["skill"]=q["skill"]?.DeepClone(),["location"]=state.Location,["observedDay"]=state.GameDay,
                ["observedRevision"]=DecisionContextCompression.Get(q["result"],"observationRevision")?.DeepClone(),["freshness"]="previous_query_observation",
                ["result"]=ProjectQuery(DecisionContextCompression.ToolResult(q["skill"]!.ToString(),q["result"]))});
        }
        world["queryFacts"]=factsList;
        world["starterGift"]=facts?["starterSeeds"]?.DeepClone();
        var allowed=ScopeFor(tools)["allowedSkillsThisTurn"]!.AsArray().Select(v=>v!.ToString()).ToHashSet();
        if(allowed.Contains("fish_at"))world["fishing"]=w["fishing"]?.DeepClone();
        if(allowed.Contains("fight_enemies"))world["combat"]=w["combat"]?.DeepClone();
        return world;
    }
    private static JsonNode? ProjectQuery(JsonNode? node)
    {
        if(node is JsonObject obj){
            var copy=new JsonObject();foreach(var (key,value) in obj)copy[key]=ProjectQuery(value);
            if(obj["columns"] is JsonArray&&obj["rows"] is JsonArray rows){copy["columns"]=obj["columns"]!.DeepClone();copy["rows"]=new JsonArray(rows.Take(16).Select(r=>r?.DeepClone()).ToArray());copy["totalRows"]=rows.Count;copy["sampleOnly"]=rows.Count>16;}
            return copy;
        }
        if(node is JsonArray list)return list.Count>16?new JsonObject{["count"]=list.Count,["sampleOnly"]=true,["items"]=new JsonArray(list.Take(16).Select(ProjectQuery).ToArray())}:new JsonArray(list.Select(ProjectQuery).ToArray());
        return node?.DeepClone();
    }
}
