using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private JsonObject? _continuation;
    private long _continuationFailureVersion;
    private const int ContinuationMaxTokens=1500;

    private async Task<object> RequestTaskCancellation(CancellationToken cancellation,string? expectedTaskId=null)
    {
        var taskId=expectedTaskId??_bridge.Observation?.ActiveTaskId??"";
        if(taskId=="")return new{status="no_active_task",cancelled=false};
        if(_bridge.Observation?.ActiveTaskId!=taskId)return new{status="target_task_changed_not_cancelled",targetTaskId=taskId,cancelled=false};
        cancellation.ThrowIfCancellationRequested();
        _planCancellation?.Cancel();DiscardContinuation(failed:true);
        await _bridge.CancelTask(taskId);
        var until=Environment.TickCount64+3000;
        do{
            var terminal=_recentResults.FirstOrDefault(r=>r.TaskId==taskId);
            if(terminal!=null)return new{status=terminal.Status=="cancelled"?"cancelled":"already_finished",targetTaskId=taskId,cancelled=terminal.Status=="cancelled",receipt=terminal};
            if(_bridge.Observation?.ActiveTaskId is {Length:>0} active&&active!=taskId)
                return new{status="target_task_changed_not_cancelled",targetTaskId=taskId,cancelled=false};
            // Cancelling the plan also cancels its caller token. The command has
            // already been sent, so keep the bounded receipt wait independent.
            await Task.Delay(50);
        }while(Environment.TickCount64<until);
        return new{status="cancel_requested",targetTaskId=taskId,cancelled=(bool?)null,terminalVerificationRequired=true};
    }

    private void DiscardContinuation(bool failed=false)
    {
        lock(_inputGate){_continuation=null;if(failed)_continuationFailureVersion++;}
    }

    // This request overlaps execution: record a hypothesis or cancel the exact observed task.
    private async Task PlanContinuation(JsonObject context,string predecessor,object acceptance,
        string decision,long epoch,long failureVersion,CancellationToken cancellation)
    {
        DiscardContinuation();
        // Decouple the auxiliary continuation deadline from the parent decision
        // cancellation so it gets its full window instead of being cut short by the
        // main decision deadline. The result is discarded on epoch change, so a
        // late reply can never be acted upon after the context moved on.
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            // Continuations need the goal, current plan and pre-action world,
            // not the full chat transcript and event history from the parent
            // decision. Keep the request bounded so this auxiliary planning
            // call has a chance to finish inside the main decision deadline.
            var input=new JsonObject();
            foreach(var key in new[]{"trigger","playerGoal","agentState","planning","world","activeProjects","decisionScope"})
                if(context[key]!=null)input[key]=context[key]!.DeepClone();
            if(context["recentEvents"] is JsonArray recentEvents)
                input["recentEvents"]=new JsonArray(recentEvents.TakeLast(6).Select(e=>e?.DeepClone()).ToArray());
            input["continuationRequest"]=new JsonObject{
                ["predecessorTool"]=predecessor,["acceptance"]=System.Text.Json.JsonSerializer.SerializeToNode(acceptance),
                ["assumption"]="上一条命令或整个短计划成功完成；尚未获得成功证据",
                ["observationMeaning"]="world是执行前快照，不是预测的执行后状态"};
            var messages=new JsonArray{
                new JsonObject{["role"]="system",["content"]=DecisionInstructions.ContinuousPlanning},
                new JsonObject{["role"]="user",["content"]=DecisionContextCompression.Serialize(input)}};
            var tools=new JsonArray(new JsonObject{["type"]="function",["function"]=new JsonObject{
                ["name"]="plan_continuation",["description"]="仅记录成功假设下的后续意图；不执行动作、不确认进度。",
                ["parameters"]=new JsonObject{["type"]="object",["additionalProperties"]=false,
                    ["properties"]=new JsonObject{
                        ["nextSteps"]=Strings(),["assumptions"]=Strings(),["checksBeforeAction"]=Strings(),["onFailure"]=new JsonObject{["type"]="string",["minLength"]=1}},
                    ["required"]=new JsonArray("nextSteps","assumptions","checksBeforeAction","onFailure")}}});
            tools.Add(ToolCatalog.PrimitiveDefinitions().First(t=>t?["function"]?["name"]?.ToString()=="cancel_task")!.DeepClone());
            var controlTaskId=_bridge.Observation?.ActiveTaskId??"";
            input["activeTask"]=System.Text.Json.JsonSerializer.SerializeToNode(_bridge.Observation is {} observed?new{taskId=observed.ActiveTaskId,observed.Location,observed.Tile,observed.Menu}:null);
            messages[1]!["content"]=DecisionContextCompression.Serialize(input);
            Trace("continuation_requested",new{predecessor,assumedSuccess=true},decision);
            Trace("model_request",new{kind="continuation",model=Settings.Model,maxTokens=ContinuationMaxTokens},decision);
            var reply=await _model.Complete(Settings,messages,timeout.Token,toolOverrides:tools,maxTokens:ContinuationMaxTokens,
                toolChoice:JsonValue.Create("auto"),
                capture:body=>Trace("model_prompt_capture",new{kind="continuation",prompt=body},decision));
            Trace("model_reply",new{kind="continuation",text=reply.Text,calls=reply.Calls,elapsedMs=reply.ElapsedMs,usage=reply.Usage,finishReason=reply.FinishReason},decision);
            if(epoch!=Interlocked.Read(ref _epoch)||!_resumed)return;
            if(reply.FinishReason=="length"){
                Trace("continuation_unavailable",new{reason="output_truncated",maxTokens=ContinuationMaxTokens,usage=reply.Usage},decision);
                return;
            }
            var function=reply.Calls.SingleOrDefault()?["function"];
            var name=function?["name"]?.ToString();
            if(name is not ("cancel_task" or "plan_continuation"))return;
            var arguments=function?["arguments"]?.GetValue<string>()??"";
            JsonObject proposal;
            try{
                proposal=JsonNode.Parse(arguments) as JsonObject??throw new InvalidOperationException("invalid_continuation_arguments");
            }
            catch(JsonException ex){
                Trace("continuation_unavailable",new{reason="invalid_arguments_json",tool=name,argumentLength=arguments.Length,
                    line=ex.LineNumber,bytePositionInLine=ex.BytePositionInLine,finishReason=reply.FinishReason,usage=reply.Usage},decision);
                return;
            }
            if(name=="cancel_task"){
                ToolCatalog.ValidateArguments("cancel_task",proposal);
                Trace("tool_selected",new{name,arguments=proposal},decision);
                var result=await RequestTaskCancellation(timeout.Token,controlTaskId);
                Trace("tool_result",new{name="cancel_task",result},decision);return;
            }
            using(var schema=System.Text.Json.JsonDocument.Parse(tools[0]!["function"]!["parameters"]!.ToJsonString()))
            using(var value=System.Text.Json.JsonDocument.Parse(proposal.ToJsonString()))
                ContractValidation.ValidateSchema(schema.RootElement,value.RootElement,"plan_continuation");
            if(!NonemptyStrings(proposal["nextSteps"])||!NonemptyStrings(proposal["assumptions"])
                ||!NonemptyStrings(proposal["checksBeforeAction"])||string.IsNullOrWhiteSpace(proposal["onFailure"]?.ToString()))
                throw new InvalidOperationException("empty_continuation");
            lock(_inputGate){
                if(epoch!=Interlocked.Read(ref _epoch)||failureVersion!=_continuationFailureVersion||!_resumed)return;
                _continuation=new JsonObject{["status"]="hypothesis_requires_verification",["sourceDecision"]=decision,
                    ["predecessorTool"]=predecessor,["proposal"]=proposal,
                    ["executionRule"]="先核对实际回执和当前观察；失败、部分完成或条件改变时放弃不成立的后续意图，重新决策。不得直接当作命令或完成证据。"};
            }
            Trace("continuation_planned",proposal,decision);
        }
        catch(OperationCanceledException){Trace("continuation_unavailable",new{reason="cancelled_or_timeout"},decision);}
        catch(Exception ex)when(ex is not OperationCanceledException){Trace("continuation_unavailable",new{reason=SafeError(ex)},decision);}

        static JsonObject Strings()=>new(){["type"]="array",["minItems"]=1,["maxItems"]=8,
            ["items"]=new JsonObject{["type"]="string",["minLength"]=1}};
    }
}
