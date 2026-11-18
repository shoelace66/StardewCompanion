using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private int CurrentGoalVersion=>PlayerGoal["version"]?.GetValue<int>()??0;
    private bool HasPlayerGoal=>PlayerGoal["status"]?.ToString()=="active"&&!string.IsNullOrWhiteSpace(PlayerGoal["objective"]?.ToString());
    private static bool NonemptyStrings(JsonNode? value)=>value is JsonArray {Count:>0} a&&a.All(v=>v is JsonValue s&&s.TryGetValue<string>(out var text)&&!string.IsNullOrWhiteSpace(text));
    private bool SeasonPlanCurrent(GameObservation state)
    {
        var plan=GetSeasonPlan();
        return plan!=null&&plan["year"]?.GetValue<int>()==state.GameDay/112+1
            &&plan["season"]?.ToString()==CalendarSeason(state)&&plan["goalVersion"]?.GetValue<int>()==CurrentGoalVersion
            &&!string.IsNullOrWhiteSpace(plan["goal"]?.ToString())&&NonemptyStrings(plan["constraints"])
            &&NonemptyStrings(plan["successCriteria"])&&NonemptyStrings(plan["milestones"])&&NonemptyStrings(plan["resourceStrategy"]);
    }
    private string PlanningBlocker(GameObservation? state)
    {
        if(!HasPlayerGoal)return "长期目标未保存：请在 LLM 交互调试的“长期目标”页保存目标";
        if(!_bridge.Connected||!_bridge.Fresh||state?.WorldReady!=true||state.SaveId+":"+state.PlayerId!=_scope||state.SessionId!=_session)return "等待当前角色的有效游戏观察";
        if(string.IsNullOrWhiteSpace(CalendarSeason(state)))return "当前季节未知，等待有效日历观察";
        if(!SeasonPlanCurrent(state))return "季计划缺失或已过期，必须由模型生成并保存";
        return "";
    }
    public JsonObject PlanningPreview
    {
        get {
            var state=_bridge.Observation;var blocker=PlanningBlocker(state);
            return new JsonObject{
                ["seasonPlan"]=OrderSeasonPlan(GetSeasonPlan()),
                ["ready"]=blocker=="",["blocker"]=blocker,["goalVersion"]=CurrentGoalVersion,
                ["objective"]=PlayerGoal["objective"]?.DeepClone(),["gameDay"]=state?.GameDay,
                ["seasonCurrent"]=HasPlayerGoal&&state!=null&&SeasonPlanCurrent(state)};
        }
    }
    private static JsonObject? OrderSeasonPlan(JsonNode? plan,bool includeProgress=true)
    {
        if(plan is not JsonObject source)return null;
        var ordered=new JsonObject();
        foreach(var (key,value) in source.OrderBy(field=>field.Key,StringComparer.Ordinal))
            if(key!="progress")ordered[key]=value?.DeepClone();
        if(includeProgress&&source.ContainsKey("progress"))ordered["progress"]=source["progress"]?.DeepClone();
        return ordered;
    }
    internal void EnsureRequiredPlans(GameObservation state)
    {
        var blocker=PlanningBlocker(state);
        if(blocker!="")throw new InvalidOperationException("planning_required: "+blocker);
    }
    private async Task PrepareRequiredPlan(GameObservation state,CancellationToken cancellation,string decisionId,long epoch)
    {
        if(!HasPlayerGoal){Status=PlanningBlocker(state);_planningPaused=true;_resumed=false;Changed?.Invoke();return;}
        if(string.IsNullOrWhiteSpace(CalendarSeason(state)))throw new InvalidOperationException(PlanningBlocker(state));
        var goalVersion=CurrentGoalVersion;
        var seasonMissing=!SeasonPlanCurrent(state);
        if(!seasonMissing)return;
        var previous=GetSeasonPlan();
        var maintenance=JsonNode.Parse(_memory.Get(_scope,"season_maintenance")??"{}")!;
        var needsProgress=seasonMissing&&previous!=null&&(previous["season"]?.ToString()!=CalendarSeason(state)||previous["year"]?.GetValue<int>()!=state.GameDay/112+1)
            &&maintenance["endedSeason"]?.ToString()==previous["season"]?.ToString()
            &&(maintenance["endedDay"]?.GetValue<int>()??-1)>=0
            &&(maintenance["endedDay"]?.GetValue<int>()??-1)<state.GameDay
            &&_memory.Get(_scope,"season_progress_updated_day")!=maintenance["endedDay"]?.ToString();
        var name=needsProgress?"update_season_progress":"create_season_plan";
        Status=needsProgress?"正在核验上一季实际进度；游戏动作等待计划保存":"正在生成必需的季计划；游戏动作等待计划保存";Changed?.Invoke();
        var tool=(needsProgress?ToolCatalog.SeasonMaintenanceDefinitions():ToolCatalog.RequiredPlanningDefinitions()).First(t=>t?["function"]?["name"]?.ToString()==name)!;
        var tools=new JsonArray(tool.DeepClone());
        var instruction=needsProgress?"必须调用 update_season_progress 核验上一季进度，只追加 seasonMaintenance.successfulResults 支持的事实，无成功回执则传空数组。":"必须调用 create_season_plan 保存完整季计划。目标、约束、成功条件、分阶段日程、资金与体力策略都必须明确。";
        var context=BuildDecisionInput(state,AwarenessSnapshot(),ConversationSnapshot(),tools,instruction);
        context["instruction"]=instruction;
        context["seasonMaintenance"]=maintenance.DeepClone();
        lock(_inputGate)_lastInput=(JsonObject)context.DeepClone();
        var messages=new JsonArray{new JsonObject{["role"]="system",["content"]=DecisionInstructions.RequiredPlanning},new JsonObject{["role"]="user",["content"]=DecisionContextCompression.Serialize(context)}};
        var requestId=Guid.NewGuid().ToString("N");Trace("decision_context",context,decisionId);
        Trace("model_request",new{round=0,model=Settings.Model,kind="required_planning",requiredTool=name},decisionId);
        var reply=await _model.Complete(Settings,messages,cancellation,
            toolChoice:new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]=name}},toolOverrides:tools,
            capture:body=>Trace("model_prompt_capture",new{requestId,round=0,kind="required_planning",gameDay=state.GameDay,prompt=body},decisionId),maxTokens:4000);
        if(epoch!=Interlocked.Read(ref _epoch)||_bridge.Observation is not {} latest||latest.SessionId!=state.SessionId||latest.GameDay!=state.GameDay)throw new OperationCanceledException("planning_context_changed");
        Trace("model_reply",new{requestId,round=0,text=reply.Text,calls=reply.Calls,elapsedMs=reply.ElapsedMs,usage=reply.Usage},decisionId);
        if(reply.Calls.Count!=1||reply.Calls[0]?["function"]?["name"]?.ToString()!=name)throw new InvalidOperationException("required_plan_not_returned: "+name);
        var args=JsonNode.Parse(reply.Calls[0]!["function"]!["arguments"]!.GetValue<string>()) as JsonObject??throw new InvalidOperationException("required_plan_arguments_invalid");
        using(var schema=JsonDocument.Parse(tool["function"]!["parameters"]!.ToJsonString()))
        using(var value=JsonDocument.Parse(args.ToJsonString()))ContractValidation.ValidateSchema(schema.RootElement,value.RootElement,name);
        Trace("tool_selected",new{name,arguments=args},decisionId);
        object result;
        lock(_inputGate){
            if(epoch!=Interlocked.Read(ref _epoch)||goalVersion!=CurrentGoalVersion||state.SessionId!=_session||state.SaveId+":"+state.PlayerId!=_scope||_bridge.Observation?.GameDay!=state.GameDay)throw new OperationCanceledException("planning_context_changed");
            result=needsProgress?UpdateSeasonProgress(args):CreateSeasonPlan(args,latest);
        }
        Trace("tool_result",new{name,result},decisionId);_retryAfter=0;
        Status=PlanningBlocker(latest) is {Length:>0} blocker?blocker:"季计划已保存，可以执行";
        WakeDecision(latest.Revision);Changed?.Invoke();
    }
}
