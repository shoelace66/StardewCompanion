using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private Task? _planTask;
    private CancellationTokenSource? _planCancellation;
    private readonly Dictionary<string,string> _planIds=new();
    private long _decisionDue;
    private long _decisionRevision;
    private long _waitDue;
    private int _decisionRequested;
    private string _waitFingerprint="";
    private string _lastActionFingerprint="";
    private string _lastActionState="";
    private int _unchangedFailures;
    private string _lastFailureReason="";
    private object? _lastPlan;
    private readonly Queue<object> _queryFacts=new();

    private static string FactFingerprint(GameObservation s)
    {
        var w=JsonNode.Parse(s.WorldView);
        return JsonSerializer.Serialize(new{s.GameDay,s.TimeOfDay,s.Location,s.Tile,s.Stamina,s.Health,s.Menu,s.MenuContext,inventory=w?["inventory"],soil=w?["soil"],forage=w?["forage"],obstacles=w?["naturalObstacles"],chests=w?["chests"],water=w?["water"]});
    }
    private void WakeDecision(long revision=0)
    {_decisionDue=Environment.TickCount64+200;_decisionRevision=Math.Max(_decisionRevision,revision);Interlocked.Exchange(ref _decisionRequested,1);}
    private object StartPlan(JsonObject args,string decision)
    {
        var s=_bridge.Observation!;
        if(Mode=="plan_only"){SaveWorkPlan(args,args["steps"],false);return new{status="saved_plan_only",executed=false};}
        if(!_resumed||_planTask is {IsCompleted:false}||s.ActiveTaskId!="")throw new InvalidOperationException("当前忙碌或已暂停，不能提交新计划");
        var normalized=(JsonObject)args.DeepClone();
        var work=WorkState();
        if(normalized["goal"] is null){var goal=work["currentObjective"]?.ToString();if(string.IsNullOrWhiteSpace(goal))goal=PlayerGoal["objective"]?.ToString();normalized["goal"]=JsonValue.Create(string.IsNullOrWhiteSpace(goal)?"执行明确操作":goal);}
        normalized["basis"]??=JsonValue.Create($"{s.Location} {s.TimeOfDay/100:D2}:{s.TimeOfDay%100:D2}; day {s.GameDay}; observed revision {s.Revision}");
        normalized["assumptions"]??=new JsonArray();
        normalized["successCriteria"]??=new JsonArray();
        normalized["pending"]??=new JsonArray((normalized["steps"]?.AsArray()??new JsonArray()).Select(step=>(JsonNode?)JsonValue.Create(step?["skill"]?.ToString()??"next step")).ToArray());
        var plan=ShortPlan.Parse(normalized);
        if(s.Menu=="DialogueBox"&&plan.Steps[0].Skill!="respond_dialogue")
            throw new InvalidOperationException("当前有未处理的对话弹窗；计划第一步必须按menuContext中的responseKey调用respond_dialogue");
        var key=_scope+":"+_session+":"+plan.Id;
        var memoryKey="plan_seen:"+_session+":"+plan.Id;
        var currentFacts=FactFingerprint(s);
        var priorFacts=_planIds.GetValueOrDefault(key)??_memory.Get(_scope,memoryKey);
        if(priorFacts==currentFacts){
            _decisionDue=Environment.TickCount64+5000;
            var rejected=new{planId=plan.Id,status="duplicate_not_executed",reason="plan_id_already_submitted",originalFailure=_lastFailureReason,results=Array.Empty<object>(),unexecuted=plan.Steps};
            RecordActionFeedback(rejected);return rejected;
        }
        SaveWorkPlan(normalized,normalized["steps"],false);
        _planIds[key]="running";
        _memory.Set(_scope,memoryKey,"running");
        _scopedPlanSkills=plan.Steps.Select(s=>s.Skill).Distinct().ToArray();_expandedTools=false;
        _planCancellation?.Dispose();_planCancellation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _planTask=ExecutePlan(plan,decision,_scope,_session,s.GameDay,_planCancellation.Token,DecisionContextCompression.Text(normalized,"goal"));
        return new{status="accepted",planId=plan.Id,steps=plan.Steps.Length};
    }
    private async Task ExecutePlan(ShortPlan plan,string decision,string scope,string session,int day,CancellationToken ct,string goal)
    {
        // Yield so StartPlan publishes the running task before any synchronous callback can complete it.
        await Task.Yield();
        var results=new List<object>();var status="completed";var reason="";
        Trace("plan_started",new{planId=plan.Id,steps=plan.Steps,source="model"},decision);
        try
        {
            for(var i=0;i<plan.Steps.Length;i++)
            {
                ct.ThrowIfCancellationRequested();
                var step=plan.Steps[i];ValidateSkillScope(step.Skill,step.Arguments);var state=_bridge.Observation!;
                if(!_resumed||scope!=_scope||session!=_session||day!=state.GameDay||!_bridge.Fresh||!ControlModePolicy.CanControl(state))
                    throw new InvalidOperationException("plan_context_changed");
                var signature=step.Skill+":"+step.Arguments.ToJsonString();
                var facts=FactFingerprint(state);
                if(signature==_lastActionFingerprint&&facts==_lastActionState&&_unchangedFailures>0)
                    throw new InvalidOperationException("unchanged_target_previously_failed:"+_lastFailureReason+":目标及相关状态未变化，先解决阻塞或选择其他工作");
                var authorized=PermissionPolicy.Authorize(step.Skill,step.Arguments,_memory.Chests(scope),state);
                var taskId=Guid.NewGuid().ToString("N");_taskDecisions[taskId]=decision;
                Trace("permission_allowed",new{name=step.Skill,arguments=authorized,planId=plan.Id,step=i},decision);
                Trace("task_dispatch",new{taskId,skill=step.Skill,arguments=authorized,source="model",planId=plan.Id,step=i},decision);
                var receipt=await new ShortPlanExecutor(_bridge).Step(step.Skill,authorized,taskId,ct,plan.EncounterPolicy);
                results.Add(new{step=i,receipt});
                _lastActionFingerprint=signature;_lastActionState=FactFingerprint(_bridge.Observation!);
                _unchangedFailures=receipt.Status!="completed"?1:0;
                _lastFailureReason=receipt.Status!="completed"?receipt.Reason:"";
                if(receipt.Status!="completed"){status="stopped";reason=receipt.Reason;break;}
            }
        }
        catch(OperationCanceledException){status="cancelled";reason="pause_cancel_or_context_change";}
        catch(Exception ex){status="stopped";reason=SafeError(ex);if(scope==_scope&&session==_session&&_resumed)await _bridge.Control("cancel");}
        finally
        {
            var finalFacts=_bridge.Observation is {} finalState?FactFingerprint(finalState):"";
            _planIds[scope+":"+session+":"+plan.Id]=finalFacts;
            _memory.Set(scope,"plan_seen:"+session+":"+plan.Id,finalFacts);
            var summary=new{planId=plan.Id,goal,status,reason,originalFailure=_lastFailureReason,results,unexecuted=plan.Steps.Skip(results.Count).ToArray()};
            RecordPlanEvidence(scope,plan.Id,summary,status=="completed"?"batch_completed":status);
            if(scope==_scope&&session==_session)RetireWorkingBatch(plan.Id);
            Decisions.Write(scope,session,decision,"plan_finished",summary);
            if(scope==_scope&&session==_session){_lastPlan=summary;RecordActionFeedback(summary);WakeDecision();}
        }
    }
    private object CompactObservation(GameObservation s)=>new{
        s.Revision,s.GameDay,s.TimeOfDay,timeOfDayHHMM=$"{s.TimeOfDay/100:D2}:{s.TimeOfDay%100:D2}",minutesSinceMidnight=s.TimeOfDay/100*60+s.TimeOfDay%100,
        clockMeaning="timeOfDay is HHMM-style game clock (e.g.1320 means 13:20), not elapsed minutes",s.ControlMode,s.Location,s.Tile,s.Stamina,s.Health,s.Menu,menuContext=JsonNode.Parse(s.MenuContext),s.ActiveTaskId,
        world=JsonNode.Parse(s.WorldView),costMeaning="候选距离是本地估算；未知或未核验收益不能当成事实"};
}
