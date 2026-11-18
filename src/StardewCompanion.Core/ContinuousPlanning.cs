using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private JsonObject? _continuation;
    private CancellationTokenSource? _continuationCancellation;
    private Task? _continuationTask;
    private static readonly TimeSpan ContinuationDelay=TimeSpan.FromSeconds(3);
    private const int ContinuationMaxTokens=1500;

    private async Task<object> RequestTaskCancellation(CancellationToken cancellation,string? expectedTaskId=null)
    {
        var taskId=expectedTaskId??_bridge.Observation?.ActiveTaskId??"";
        if(taskId=="")return new{status="no_active_task",cancelled=false};
        if(_bridge.Observation?.ActiveTaskId!=taskId)return new{status="target_task_changed_not_cancelled",targetTaskId=taskId,cancelled=false};
        cancellation.ThrowIfCancellationRequested();
        _planCancellation?.Cancel();DiscardContinuation();
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

    private void DiscardContinuation()
    {
        lock(_inputGate){
            _continuation=null;
            _continuationCancellation?.Cancel();
        }
    }

    private static JsonObject BuildContinuationInput(JsonObject context,ShortPlan plan)
    {
        var world=new JsonObject();
        foreach(var key in new[]{"gameDay","time","location","stamina","health","money","calendar","inventoryState","farmingPlan","risks"})
            if(context["world"]?[key] is {} value)world[key]=value.DeepClone();
        return new JsonObject{
            ["playerGoal"]=context["playerGoal"]?.DeepClone(),
            ["planning"]=new JsonObject{["seasonPlan"]=OrderSeasonPlan(context["planning"]?["seasonPlan"],includeProgress:false)},
            ["world"]=world,
            ["continuationRequest"]=new JsonObject{["planId"]=plan.Id,
                ["steps"]=JsonSerializer.SerializeToNode(plan.Steps),
                ["assumption"]="整个短计划成功；执行结果仍需核验",
                ["observationMeaning"]="world 是执行前快照"}
        };
    }

    private CancellationTokenSource? StartContinuation(JsonObject input,string planId,string decision,long epoch,int day,CancellationToken cancellation)
    {
        lock(_inputGate){
            if(epoch!=Interlocked.Read(ref _epoch)||cancellation.IsCancellationRequested)return null;
            if(_continuationTask is {IsCompleted:false}){
                Trace("continuation_skipped",new{planId,reason="previous_request_draining"},decision);
                return null;
            }
            _continuationCancellation?.Dispose();
            _continuationCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _continuationTask=PlanContinuation(input,planId,decision,epoch,day,_continuationCancellation.Token);
            return _continuationCancellation;
        }
    }

    private void StopContinuation(CancellationTokenSource? request)
    {
        lock(_inputGate)
            if(request!=null&&ReferenceEquals(_continuationCancellation,request))request.Cancel();
    }

    private async Task PlanContinuation(JsonObject input,string planId,string decision,long epoch,int day,CancellationToken cancellation)
    {
        var requested=false;
        try
        {
            await Task.Delay(ContinuationDelay,cancellation);
            if(!_resumed||epoch!=Interlocked.Read(ref _epoch)||!_bridge.Fresh||
               _bridge.Observation is not {Menu:""} observed||observed.GameDay!=day){
                Trace("continuation_skipped",new{planId,reason="batch_context_not_ready"},decision);
                return;
            }
            cancellation.ThrowIfCancellationRequested();
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
            var controlTaskId=observed.ActiveTaskId;
            input["activeTask"]=JsonSerializer.SerializeToNode(new{taskId=controlTaskId,observed.Location,observed.Tile,observed.Menu});
            messages[1]!["content"]=DecisionContextCompression.Serialize(input);
            var requestId=Guid.NewGuid().ToString("N");
            requested=true;
            Trace("continuation_requested",new{planId,requestId,assumedSuccess=true},decision);
            Trace("model_request",new{requestId,kind="continuation",model=Settings.Model,maxTokens=ContinuationMaxTokens},decision);
            var reply=await _model.Complete(Settings,messages,cancellation,toolOverrides:tools,maxTokens:ContinuationMaxTokens,
                toolChoice:JsonValue.Create("auto"),
                capture:body=>Trace("model_prompt_capture",new{requestId,kind="continuation",prompt=body},decision));
            Trace("model_reply",new{requestId,kind="continuation",text=reply.Text,calls=reply.Calls,elapsedMs=reply.ElapsedMs,usage=reply.Usage,finishReason=reply.FinishReason},decision);
            cancellation.ThrowIfCancellationRequested();
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
                if(controlTaskId=="")return;
                var result=await RequestTaskCancellation(cancellation,controlTaskId);
                Trace("tool_result",new{name="cancel_task",result},decision);return;
            }
            using(var schema=System.Text.Json.JsonDocument.Parse(tools[0]!["function"]!["parameters"]!.ToJsonString()))
            using(var value=System.Text.Json.JsonDocument.Parse(proposal.ToJsonString()))
                ContractValidation.ValidateSchema(schema.RootElement,value.RootElement,"plan_continuation");
            if(!NonemptyStrings(proposal["nextSteps"])||!NonemptyStrings(proposal["assumptions"])
                ||!NonemptyStrings(proposal["checksBeforeAction"])||string.IsNullOrWhiteSpace(proposal["onFailure"]?.ToString()))
                throw new InvalidOperationException("empty_continuation");
            lock(_inputGate){
                if(epoch!=Interlocked.Read(ref _epoch)||cancellation.IsCancellationRequested||!_resumed)return;
                _continuation=new JsonObject{["status"]="hypothesis_requires_verification",["sourceDecision"]=decision,
                    ["planId"]=planId,["proposal"]=proposal,
                    ["executionRule"]="先核对实际回执和当前观察；失败、部分完成或条件改变时放弃不成立的后续意图，重新决策。不得直接当作命令或完成证据。"};
                Trace("continuation_planned",proposal,decision);
            }
        }
        catch(OperationCanceledException){
            Trace(requested?"continuation_unavailable":"continuation_skipped",
                new{reason=requested?"batch_ended_or_cancelled":"batch_ended_before_delay",planId},decision);
        }
        catch(Exception ex)when(ex is not OperationCanceledException){Trace("continuation_unavailable",new{reason=SafeError(ex)},decision);}

        static JsonObject Strings()=>new(){["type"]="array",["minItems"]=1,["maxItems"]=8,
            ["items"]=new JsonObject{["type"]="string",["minLength"]=1}};
    }
}
