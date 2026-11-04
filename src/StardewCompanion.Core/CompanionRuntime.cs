using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime : IAsyncDisposable
{
    private readonly IGameBridge _bridge;
    private readonly ModelClient _model;
    private readonly MemoryStore _memory;
    private readonly string _logPath;
    private readonly CancellationTokenSource _lifetime=new();
    private sealed record QueuedInstruction(string Scope,string Session,string Text,bool SeasonMaintenance=false,string? ConversationMessageId=null);
    private readonly ConcurrentQueue<QueuedInstruction> _messages=new();
    private readonly ConcurrentQueue<TaskResult> _recentResults=new();
    private readonly HashSet<string> _capturedPromptKinds=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,string> _taskProjects=new();
    private readonly ConcurrentDictionary<string,string> _taskDecisions=new();
    public DecisionJournal Decisions { get; }
    public bool Thinking=>_thinking is {IsCompleted:false};
    public bool PlanningEnabled {get;set;}=true;
    public int? AutonomousTrialEndDay {get;set;}
    private bool _trialFinished;
    private int _emptyDecisionRetries;
    private long _lastOfflineSnapshotAt;
    private CancellationTokenSource? _request;
    private Task? _loop;
    private readonly object _lifecycleGate=new();
    private Task? _disposeTask;
    private Task? _thinking;
    private string _session="";
    private int _day=-1;
    private string _season="";
    private long _epoch;
    private long _lastDecision;
    private long _retryAfter;
    private bool _resumed;
    private bool _planningPaused=true;
    private string _scope="";
    public string Scope=>_scope;
    public ModelSettings Settings {get;set;}
    public string Status {get;private set;}="等待 AI 游戏连接";
    public bool Running=>_resumed;
    public string PendingQuestion {get;private set;}="";
    public event Action? Changed;
    public event Action<string,string>? ChatReceived;
    public MemoryStore Memory=>_memory;
    public GameObservation? Observation=>_bridge.Observation;
    public CompanionRuntime(IGameBridge bridge,MemoryStore memory,ModelSettings settings,HttpClient http,string logPath)
    {
        _bridge=bridge;_memory=memory;Settings=settings;_model=new(http);_logPath=logPath;
        _scope=_memory.Get("_global","last_scope")??_memory.LatestChatScope()??"";
        if(_scope!="")_session=_memory.Get(_scope,"last_session")??"";
        Decisions=new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logPath))!,"decisions.jsonl"));
        bridge.Observed+=OnObserved;bridge.Result+=OnResult;
    }
    public void Start()
    {
        lock(_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask!=null,this);
            if(_scope!=""&&!_bridge.Connected&&LatestPendingConversation() is {} pending)_messages.Enqueue(pending);
            _loop??=Run();
        }
    }
    private void OnObserved(GameObservation state)
    {
        if(!ControlModePolicy.CanControl(state))return;
        var scope=state.SaveId+":"+state.PlayerId;
        _memory.Set("_global","last_scope",scope);
        _memory.Set(scope,"last_session",state.SessionId);
        SaveOfflineSnapshot(scope,state);
        if(state.SessionId!=_session||scope!=_scope)
        {
            _resumed=false;_planningPaused=true;Invalidate();_taskProjects.Clear();_taskDecisions.Clear();_queryFacts.Clear();_capturedPromptKinds.Clear();_lastPlan=null;_decisionRevision=0;_lastActionFingerprint="";_lastActionState="";_lastFailureReason="";_unchangedFailures=0;
            while(_messages.TryDequeue(out _)){} while(_recentResults.TryDequeue(out _)){}
            _expandedTools=false;_scopedPlanSkills=Array.Empty<string>();_triggerKind="session_started";
            lock(_inputGate){_scope=scope;_session=state.SessionId;_day=state.GameDay;_season=CalendarSeason(state);}
            _memory.Reconcile(scope,_session,state.GameDay);
            ReconcileWorkingSession(state);
            _memory.Set(scope,"paused",_memory.Get(scope,"paused")??"true");
            Status="已连接；已核对新会话，点击继续后工作";
            PendingQuestion=_memory.Get(scope,"pendingQuestion")??"";
            Trace("session_observed",new{scope,session=_session,day=_day,paused=true});
        }
        else if(_day!=state.GameDay)
        {
            var previousDay=_day;
            var previousSeason=_season;
            var counts=_memory.EventSummary(scope,_session,previousDay);
            if(counts.Count>0)_memory.Add(scope,"episode",JsonSerializer.Serialize(new{day=previousDay,session=_session,verifiedTaskCounts=counts,source="actual_execution_results",scope="session_observed"}),"game_task_results",previousDay,"session_observed");
            var success=JsonNode.Parse(_memory.Get(scope,"verified_day_results:"+_session+":"+previousDay)??"[]");
            _day=state.GameDay;_season=CalendarSeason(state);Invalidate(false);_memory.Reconcile(scope,_session,_day);RetireWorkingDay();
            _memory.Set(scope,"season_maintenance",JsonSerializer.Serialize(new{endedDay=previousDay,endedSeason=previousSeason,currentSeason=_season,successfulResults=success}));
            while(_recentResults.TryDequeue(out _)){}
            _triggerKind="new_day";Trace("day_changed",new{previousDay,day=_day});
            if(_resumed&&PlanningEnabled)_messages.Enqueue(new(scope,_session,"新的一天已开始，维护上一日季节计划进度。",true));
        }
        ObserveAwareness(state);
        Changed?.Invoke();
    }
    private void OnResult(TaskResult result)
    {
        if(string.IsNullOrEmpty(_scope))return;
        var decision=_taskDecisions.GetValueOrDefault(result.TaskId)??"";
        Trace("task_result",result,decision);
        if(result.Status is "completed" or "partial" or "failed" or "cancelled" or "rejected")
        {
            if(_triggerKind=="no_action_retry"&&!ToolCatalog.Queries.Contains(result.Skill)){_retryAfter=0;_emptyDecisionRetries=0;}
            if(result.Status=="completed"&&result.Completed>0&&!ToolCatalog.Queries.Contains(result.Skill))RecordSuccessfulDayResult(result);
            if(!ToolCatalog.Queries.Contains(result.Skill)&&!result.Reason.EndsWith("_ack"))WakeDecision(result.ObservationRevision);
            if(!result.Reason.EndsWith("_ack") && !ToolCatalog.Queries.Contains(result.Skill))
            {
                _recentResults.Enqueue(result);while(_recentResults.Count>12)_recentResults.TryDequeue(out _);
            }
            if(!ToolCatalog.Queries.Contains(result.Skill)&&!result.Reason.EndsWith("_ack"))RecordActionFeedback(new { kind="task", result=DecisionContextCompression.Receipt(JsonSerializer.SerializeToNode(result)) });
            RecordWorkingResult(result);_triggerKind="task_terminal";
            _memory.Record(_scope,_session,_day,result.TaskId,result.Status,result.Completed,result.Reason);
            if(_taskProjects.TryRemove(result.TaskId,out var projectId)&&result.Status is "completed" or "partial")
                _memory.RecordProjectResult(_scope,projectId,_session,_day,result);
            Log(new{kind="task",taskId=result.TaskId,status=result.Status,reason=result.Reason,count=result.Completed});
            if(!result.Reason.EndsWith("_ack"))Status=$"{result.Status}：{result.Reason}（实际数量 {result.Completed}）";
            _taskDecisions.TryRemove(result.TaskId,out _);
        }
        else Status=$"{result.Reason}（已完成 {result.Completed}）";
        Changed?.Invoke();
    }
    private void Invalidate(bool cancelPlan=true){Interlocked.Increment(ref _epoch);_request?.Cancel();if(cancelPlan)_planCancellation?.Cancel();}
    internal void RecordExternalTask(TaskCommand command)
    {
        var decision="script:"+command.TaskId;_taskDecisions[command.TaskId]=decision;
        Trace("task_dispatch",new{taskId=command.TaskId,skill=command.Skill,arguments=JsonNode.Parse(command.Arguments),source=PlanningEnabled?"operator":"script"},decision);
    }
    public async Task Pause()
    {
        _resumed=false;_planningPaused=true;Invalidate();
        // This sends directly; no model, DB operation, or UI render on the critical input-stop path.
        await _bridge.Control("pause");
        Trace("control",new{action="pause"});
        if(_scope!="")_memory.Set(_scope,"paused","true");
        Status="已暂停工作；联机世界仍继续运行";Changed?.Invoke();
    }
    public async Task Resume()
    {
        if(_bridge.Observation is not {} state||!ControlModePolicy.CanControl(state))throw new InvalidOperationException("请先连接联机农场手或明确隔离的单人测试角色");
        _resumed=true;_planningPaused=false;_retryAfter=0;Invalidate();_memory.Set(_scope,"paused","false");
        await _bridge.Control("resume");Status="继续工作；正在观察";_lastDecision=0;WakeDecision();Changed?.Invoke();
        Trace("control",new{action="resume"});
    }
    public async Task Cancel()
    {
        _resumed=false;_planningPaused=true;Invalidate();await _bridge.Control("pause");await _bridge.Control("cancel");
        if(_scope!="")
        {
            _memory.Set(_scope,"paused","true");_memory.Set(_scope,"cancelled","true");_memory.Set(_scope,"player_instructions","[]");
            CancelInputState();
            RetireWorkingBlockers();
            // Explicitly cancelled projects must not return as autonomous objectives after restart.
            foreach(var p in _memory.List(_scope).Where(m=>m.Kind=="project"&&m.State!="cancelled"))_memory.State(_scope,p.Id,"cancelled");
        }
        while(_messages.TryDequeue(out _)){} Status="已取消；等待玩家新指令";Changed?.Invoke();
        Trace("control",new{action="cancel"});
    }
    public void SendMessage(string message)
    {
        if(string.IsNullOrWhiteSpace(message))return;
        if(_scope==""){
            _scope=_memory.Get("_global","last_scope")??_memory.LatestChatScope()??"offline:general";
            _session=_memory.Get(_scope,"last_session")??"";
        }
        var scope=_scope;var session=_session;
        var conversationMessageId=QueueConversation(message);
        Invalidate(false);WakeDecision();_messages.Enqueue(new(scope,session,message,false,conversationMessageId));_memory.Chat(scope,"user",message);ChatReceived?.Invoke("玩家",message);
        Trace("player_message",new{text=message});
        if(_resumed&&_bridge.Observation is {ActiveTaskId.Length:>0})_ = InterruptActiveTaskForPlayerMessage(scope,session);
        if(PendingQuestion!="") {PendingQuestion="";_memory.Set(_scope,"pendingQuestion","");}
    }
    private async Task InterruptActiveTaskForPlayerMessage(string scope,string session)
    {
        try
        {
            if(scope!=_scope||session!=_session)return;
            await _bridge.Control("cancel");
            Trace("control",new{action="interrupt_for_player_message"});
        }
        catch(Exception ex){Trace("control_error",new{action="interrupt_for_player_message",error=SafeError(ex)});}
    }
    public void ClearQuestion(){PendingQuestion="";if(_scope!="")_memory.Set(_scope,"pendingQuestion","");Invalidate(false);WakeDecision();Changed?.Invoke();}
    public void AddMemory(string kind,string text)
    {if(_scope=="")throw new InvalidOperationException("尚未连接角色");if(kind=="project")_memory.CreateProject(_scope,text,_day);else _memory.Add(_scope,kind,text,"player",_day,kind=="agreement"?"player_stated":"needs_recheck");Changed?.Invoke();}
    public void EditMemory(long id,string text)
    {var entry=_memory.List(_scope).FirstOrDefault(m=>m.Id==id)??throw new InvalidOperationException("记忆不存在");if(entry.Kind=="project")_memory.ReviseProject(_scope,id,text);else _memory.Edit(_scope,id,text);Changed?.Invoke();}
    public void ConfirmProjectComplete(long id)
    {var entry=_memory.List(_scope).FirstOrDefault(m=>m.Id==id&&m.Kind=="project")??throw new InvalidOperationException("项目不存在");_memory.State(_scope,id,"user_confirmed_complete");_memory.Chat(_scope,"user","玩家确认项目完成："+entry.Display);Changed?.Invoke();}
    public async Task BindChest(string name,int x,int y,string[] allowed,string[] protectedIds,CancellationToken cancellation=default)
    {
        if(!_resumed&&PlanningEnabled)throw new InvalidOperationException("绑定前请点击继续；随后可立即暂停");
        if(string.IsNullOrWhiteSpace(name)||allowed.Length==0)throw new InvalidOperationException("请填写箱子名称和允许物品 ID");
        var result=await _bridge.Dispatch("bind_chest",new{x,y},true,cancellation);
        cancellation.ThrowIfCancellationRequested();
        if(result.Status!="completed")throw new InvalidOperationException(result.Reason);
        var v=JsonNode.Parse(result.Details)!;
        var binding=new ChestBinding(name,v["id"]!.GetValue<string>(),v["location"]!.GetValue<string>(),x,y,allowed,protectedIds);
        foreach(var old in _memory.Chests(_scope).Where(c=>c.Name==name))
            foreach(var m in _memory.List(_scope).Where(m=>m.Kind=="place"&&m.Text.Contains(old.Id)))_memory.Delete(_scope,m.Id);
        _memory.Add(_scope,"place",JsonSerializer.Serialize(binding),"player",_day,"player_stated");
        if(PendingQuestion.Contains("箱")||PendingQuestion.Contains("chest",StringComparison.OrdinalIgnoreCase)){
            PendingQuestion="";_memory.Set(_scope,"pendingQuestion","");
        }
        Invalidate(false);WakeDecision();Changed?.Invoke();
    }
    private async Task Run()
    {
        try
        {
            while(!_lifetime.IsCancellationRequested)
            {
                try
                {
                    var s=_bridge.Observation;
                    if(!_bridge.Connected){if(_resumed){_resumed=false;_planningPaused=true;Invalidate();Status="游戏连接中断；重新连接后点击继续";Changed?.Invoke();}
                        if((_thinking==null||_thinking.IsCompleted)&&_messages.TryDequeue(out var offline)&&offline.Scope==_scope)
                            _thinking=AnswerOffline(offline);
                    }
                    else if(!_bridge.Fresh) { Status="游戏正在加载或状态已过期，等待新观察";Changed?.Invoke(); }
                    else if(s?.WorldReady==true)
                    {
                        if(AutonomousTrialEndDay is {} end && s.ControlMode=="solo_test" && s.GameDay>=end && s.Menu=="" && s.ActiveTaskId=="")
                        {
                            if(!_trialFinished){_trialFinished=true;await Pause();Trace("trial_completed",new{gameDay=s.GameDay,targetDays=end,reason="configured_native_days_elapsed",s.TestRunId});}
                            await Task.Delay(200,_lifetime.Token);continue;
                        }
                        if(_waitFingerprint!=""&&FactFingerprint(s)!=_waitFingerprint){_waitFingerprint="";_waitDue=0;_triggerKind="wait_condition_changed";WakeDecision();}
                        else if(_waitDue>0&&Environment.TickCount64>=_waitDue){_waitFingerprint="";_waitDue=0;_triggerKind="wait_timeout";WakeDecision();}
                        if(_retryAfter>0&&Environment.TickCount64>=_retryAfter){_retryAfter=0;if(_triggerKind!="no_action_retry")_triggerKind="model_retry";WakeDecision();}
                        var sleeping=s.Menu is "ReadyCheckDialog" or "SaveGameMenu" or "ShippingMenu";
                        if(PlanningEnabled&&(_thinking==null||_thinking.IsCompleted)&&_planTask is not {IsCompleted:false}&&!sleeping)
                        {
                            if((_resumed||Mode=="plan_only"&&!_planningPaused)&&_messages.TryDequeue(out var queued))
                            { Interlocked.Exchange(ref _decisionRequested,0);if(queued.Scope==_scope&&queued.Session==_session)_thinking=Think(queued.Text,queued.Scope,queued.Session,queued.SeasonMaintenance); }
                            else if((_resumed||Mode=="plan_only"&&!_planningPaused)&&s.ActiveTaskId==""&&PendingQuestion==""&&Environment.TickCount64>=_retryAfter&&Environment.TickCount64>=_decisionDue&&s.Revision>=_decisionRevision&&Interlocked.Exchange(ref _decisionRequested,0)==1)
                                _thinking=Think("根据当前工作状态与游戏观察决定下一步。");
                        }
                    }
                }
                catch(Exception ex){Status="执行已停下："+SafeError(ex);Changed?.Invoke();}
                await Task.Delay(200,_lifetime.Token);
            }
        }
        catch(OperationCanceledException){}
    }
    private void SaveOfflineSnapshot(string scope,GameObservation state)
    {
        var now=Environment.TickCount64;if(now-_lastOfflineSnapshotAt<5000)return;_lastOfflineSnapshotAt=now;
        try{
            var world=JsonNode.Parse(state.WorldView) as JsonObject??new();
            var snapshot=new JsonObject{["observedAtUtc"]=DateTimeOffset.UtcNow.ToString("o"),["gameDay"]=state.GameDay,["timeOfDay"]=state.TimeOfDay,
                ["location"]=state.Location,["tile"]=JsonSerializer.SerializeToNode(state.Tile),["stamina"]=state.Stamina,["health"]=state.Health,
                ["money"]=world["money"]?.DeepClone(),["inventory"]=world["inventory"]?.DeepClone(),["calendar"]=world["decisionFacts"]?["calendar"]?.DeepClone(),
                ["weather"]=world["weather"]?.DeepClone()};
            _memory.Set(scope,"last_known_game_state",snapshot.ToJsonString());
        }catch(JsonException){}
    }
    private async Task AnswerOffline(QueuedInstruction queued)
    {
        var scope=queued.Scope;var session=queued.Session;var decisionId=Guid.NewGuid().ToString("N");
        var epoch=Interlocked.Read(ref _epoch);
        _request=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_request.CancelAfter(TimeSpan.FromSeconds(50));var cancellation=_request.Token;
        try{
            var goal=PlayerGoal;var work=WorkState();
            var goalContext=new JsonObject{["objective"]=goal["objective"]?.DeepClone(),["completionCondition"]=goal["completionCondition"]?.DeepClone(),
                ["priorities"]=goal["priorities"]?.DeepClone(),["restrictions"]=goal["restrictions"]?.DeepClone()};
            var workflow=new JsonObject{["currentObjective"]=work["currentObjective"]?.DeepClone(),["objectiveBasis"]=work["objectiveBasis"]?.DeepClone(),
                ["next"]=work["next"]?.DeepClone(),["confirmedMilestones"]=new JsonArray((work["confirmedMilestones"] as JsonArray??new()).TakeLast(4).Select(m=>m?.DeepClone()).ToArray())};
            var memories=_memory.Relevant(scope,queued.Text).Where(m=>m.Kind is "agreement" or "routine" or "project")
                .Take(4).Select(m=>new JsonObject{["kind"]=m.Kind,["text"]=m.Text.Length>700?m.Text[..700]:m.Text,["state"]=m.State}).ToArray();
            var context=new JsonObject{["kind"]="offline_chat",["gameConnected"]=false,["currentMessage"]=queued.Text,
                ["playerGoal"]=goalContext,["workflow"]=workflow,["lastKnownGameState"]=JsonNode.Parse(_memory.Get(scope,"last_known_game_state")??"null"),
                ["relevantMemories"]=new JsonArray(memories)};
            static string Clip(string? value,int max)=>string.IsNullOrWhiteSpace(value)?"（无记录）":value.Length<=max?value:value[..max]+"…";
            var prompt=string.Join("\n",new[]{"存档目标："+Clip(goal["objective"]?.ToString(),90),
                "最近流程："+Clip(work["currentObjective"]?.ToString(),90),
                "记录的下一步："+string.Join("；",(work["next"] as JsonArray??new()).Take(2).Select(v=>Clip(v?.ToString(),45))),
                "玩家问题："+queued.Text});
            var messages=new JsonArray{new JsonObject{["role"]="system",["content"]=DecisionInstructions.OfflineChat},
                new JsonObject{["role"]="user",["content"]=prompt}};
            Trace("offline_chat_context",context,decisionId);Trace("model_request",new{kind="offline_chat",model=Settings.Model,toolCount=0},decisionId);
            var reply=await _model.Complete(Settings,messages,cancellation,toolOverrides:new JsonArray(),maxTokens:512);
            if(epoch!=Interlocked.Read(ref _epoch)||scope!=_scope||session!=_session)return;
            Trace("model_reply",new{kind="offline_chat",text=reply.Text,toolCalls=reply.Calls.Count,elapsedMs=reply.ElapsedMs,usage=reply.Usage},decisionId);
            if(queued.ConversationMessageId is {Length:>0} messageId)ConfirmConversationMessage(messageId);
            Say(string.IsNullOrWhiteSpace(reply.Text)?"我收到了你的消息。当前没有可用的文字回复，稍后可以再问一次。":reply.Text);
            Status="已回复（游戏未连接；未执行游戏操作）";Changed?.Invoke();
        }catch(OperationCanceledException){Trace("offline_chat_cancelled",new{},decisionId);}
        catch(Exception ex){var error=SafeError(ex);Trace("offline_chat_error",new{error},decisionId);Status="离线聊天失败："+error;Changed?.Invoke();}
        finally{_request?.Dispose();_request=null;}
    }
    private async Task Think(string instruction,string? expectedScope=null,string? expectedSession=null,bool seasonMaintenance=false)
    {
        var epoch=Interlocked.Read(ref _epoch);var scope=_scope;var session=_session;
        if(expectedScope!=null&&(expectedScope!=scope||expectedSession!=session))return;
        var nextProject="";
        var decisionId=Guid.NewGuid().ToString("N");
        _request=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_request.CancelAfter(TimeSpan.FromSeconds(50));var cancellation=_request.Token;
        _lastDecision=Environment.TickCount64;
        try
        {
            var state=_bridge.Observation!;
            var messages=new JsonArray {new JsonObject{["role"]="system",["content"]=seasonMaintenance?DecisionInstructions.SeasonMaintenance:DecisionInstructions.System}};
            var delivery=AwarenessSnapshot();
            var conversation=ConversationSnapshot();
            var promptTools=seasonMaintenance?ToolCatalog.SeasonMaintenanceDefinitions():DecisionTools(state);
            var contextSources=new JsonObject{["instruction"]=instruction,["playerGoal"]=PlayerGoal,
                ["observation"]=new JsonObject{["gameDay"]=state.GameDay,["calendar"]=JsonNode.Parse(state.WorldView)?["decisionFacts"]?["calendar"]?.DeepClone()},
                ["seasonPlan"]=GetSeasonPlan(),["seasonMaintenance"]=JsonNode.Parse(_memory.Get(scope,"season_maintenance")??"{}")};
            var context=seasonMaintenance?contextSources:BuildDecisionInput(state,delivery,conversation,promptTools,instruction);
            Trace("decision_context",context,decisionId);
            messages.Add(new JsonObject{["role"]="user",["content"]=DecisionContextCompression.Serialize(context)});
            for(var round=0;round<3;round++)
            {
                Trace("model_request",new{round,model=Settings.Model,messageContentCharacters=messages.Sum(m=>m?["content"]?.ToString().Length??0),messageJsonCharacters=messages.ToJsonString(DecisionContextCompression.ReadableJson).Length},decisionId);
                if(!seasonMaintenance){
                    promptTools=DecisionTools(_bridge.Observation??state);
                    context["decisionScope"]=ScopeFor(promptTools);
                    lock(_inputGate)_lastInput=(JsonObject)context.DeepClone();
                    messages[1]!["content"]=DecisionContextCompression.Serialize(context);
                }
                var requestId=Guid.NewGuid().ToString("N");
                var reply=await _model.Complete(Settings,messages,cancellation,soloTest:false,capabilities:state.Capabilities,
                    toolOverrides:promptTools, capture:body=>Trace("model_prompt_capture",new{requestId,round,kind=seasonMaintenance?"sleep_maintenance":"daily_action",gameDay=state.GameDay,
                        prompt=body,feedbackRevision=seasonMaintenance?null:delivery["revision"]?.DeepClone(),eventIds=seasonMaintenance?Array.Empty<string?>():delivery["pendingEvents"]?.AsArray().Select(e=>e?["id"]?.ToString()).ToArray()},decisionId));
                _retryAfter=0;
                Log(new{kind="model",elapsedMs=reply.ElapsedMs,usage=reply.Usage});
                if(epoch!=Interlocked.Read(ref _epoch)||scope!=_scope||session!=_session){Trace("decision_discarded",new{reason="context_changed"},decisionId);return;}
                if(!seasonMaintenance&&(reply.Text!=""||reply.Calls.Count>0)){ConfirmAwarenessDelivery(delivery);ConfirmConversation(conversation);_triggerKind="state_changed";}
                Trace("model_reply",new{requestId,round,text=reply.Text,calls=reply.Calls,elapsedMs=reply.ElapsedMs,usage=reply.Usage},decisionId);
                if(reply.Text!="")Say(reply.Text);
                if(reply.Calls.Count==0){
                    if(seasonMaintenance)break;
                    Trace("empty_decision",new{reason="no_tool_selected"},decisionId);
                    if(seasonMaintenance)context["instruction"]="维护本日季节计划进度。";
                    else context["trigger"]=new JsonObject{["kind"]="no_action_selected",["gameDay"]=state.GameDay,["reason"]="请选择下一步工具"};
                    messages[1]!["content"]=DecisionContextCompression.Serialize(context);
                    continue;
                }
                messages.Add(reply.Message.DeepClone());var mutation=false;
                foreach(var call in reply.Calls)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(epoch!=Interlocked.Read(ref _epoch))return;
                    var name=call?["function"]?["name"]?.GetValue<string>()??"";
                    var rawArguments=call?["function"]?["arguments"]?.GetValue<string>()??"{}";
                    JsonObject arguments;
                    try{arguments=JsonNode.Parse(rawArguments) as JsonObject??throw new JsonException("Expected a JSON object");}
                    catch(JsonException ex){
                        var invalid=new{status="rejected",reason="invalid_tool_arguments_json",line=ex.LineNumber,column=ex.BytePositionInLine,
                            correction="工具arguments必须是合法JSON对象；所有文本值用双引号。组合技能仅传planId和steps；save_plan仅传planId、goal、basis。请修正后重试，本次没有执行该工具"};
                        Trace("tool_rejected",new{name,result=invalid,rawArguments},decisionId);
                        messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]=call!["id"]!.GetValue<string>(),["content"]=DecisionContextCompression.Serialize(invalid)});
                        continue;
                    }
                    object result;
                    Trace("tool_selected",new{name,arguments},decisionId);
                    try
                    {
                        if(!promptTools.Any(t=>t?["function"]?["name"]?.ToString()==name))throw new InvalidOperationException("tool_not_in_current_scope:"+name);
                        using(var schema=JsonDocument.Parse(promptTools.First(t=>t?["function"]?["name"]?.ToString()==name)!["function"]!["parameters"]!.ToJsonString()))
                        using(var value=JsonDocument.Parse(arguments.ToJsonString()))ContractValidation.ValidateSchema(schema.RootElement,value.RootElement,name);
                        if(seasonMaintenance){
                            result=name=="update_season_progress"?UpdateSeasonProgress(arguments):CreateSeasonPlan(arguments,state);
                        }
                        else if(name=="save_plan"){
                            result=SaveWorkPlan(arguments);
                            if(Mode=="plan_only"){_decisionDue=Environment.TickCount64+120000;mutation=true;}
                        }
                        else if(name=="list_capabilities"){
                            _expandedTools=true;
                            var expanded=DecisionTools(_bridge.Observation??state);
                            context=BuildDecisionInput(_bridge.Observation??state,delivery,conversation,expanded,instruction);
                            messages[1]!["content"]=DecisionContextCompression.Serialize(context);
                            result=new{status="expanded",scope=ScopeFor(expanded)};
                        }
                        else if(name=="read_context_history"){result=ReadContextHistory(arguments);}
                        else if(name.StartsWith("group_",StringComparison.Ordinal)||name=="execute_plan")
                        {
                            if(mutation)throw new InvalidOperationException("本轮已经提交计划");
                            var allowed=ScopeFor(promptTools)["allowedSkillsThisTurn"]!.AsArray().Select(v=>v!.ToString()).ToHashSet();
                            foreach(var step in arguments["steps"]?.AsArray()??new JsonArray()){
                                var skill=step?["skill"]?.ToString()??"";ValidateSkillScope(skill,step?["arguments"] as JsonObject);
                                if(!allowed.Contains(skill))throw new InvalidOperationException("skill_not_in_current_scope:"+skill+":call_list_capabilities_to_expand");
                                if(name.StartsWith("group_",StringComparison.Ordinal)&&BusinessCapabilities.GroupFor(skill)!=name[6..])throw new InvalidOperationException("skill_outside_parent_group:"+skill);
                            }
                            result=StartPlan(arguments,decisionId);mutation=true;
                        }
                        else if(name=="wait_for_change")
                        {
                            var current=_bridge.Observation!;
                            if(current.Menu!="")throw new InvalidOperationException("modal_menu_blocks_wait:"+current.Menu+":请先使用respond_dialogue或关闭当前界面；菜单打开时游戏时间不会推进");
                            var seconds=arguments["seconds"]?.GetValue<int>()??30;
                            if(seconds is <1 or >120)throw new InvalidOperationException("等待必须为1到120秒");
                            _waitFingerprint=FactFingerprint(current);_waitDue=Environment.TickCount64+seconds*1000;
                            _expandedTools=false;
                            result=new{status="waiting",seconds,reason=arguments["reason"]?.GetValue<string>()};mutation=true;
                        }
                        else if(ToolCatalog.Queries.Contains(name)||BusinessCapabilities.Find(name) is {Query:true})
                        {
                            var queryId=Guid.NewGuid().ToString("N");_taskDecisions[queryId]=decisionId;
                            result=await _bridge.Dispatch(name,arguments,true,cancellation,queryId);
                            if(name!="observe"){
                                _queryFacts.Enqueue(new{skill=name,observedDay=state.GameDay,location=state.Location,result});
                                while(_queryFacts.Count>4)_queryFacts.Dequeue();
                            }
                        }
                        else if(name=="ask_user") {PendingQuestion=arguments["question"]!.GetValue<string>();_memory.Set(scope,"pendingQuestion",PendingQuestion);result=new{status="pending",approved=false};mutation=true;Changed?.Invoke();}
                        else if(name=="remember_experience") {var id=_memory.Add(scope,"routine",arguments["text"]!.GetValue<string>(),"model_inference",_day);result=new{id,state="unverified"};}
                        else if(name=="update_project")
                        {
                            var projectId=arguments["projectId"]!.GetValue<string>();var updated=_memory.UpdateProjectNext(scope,projectId,arguments["nextStep"]!.GetValue<string>());
                            if(updated)nextProject=projectId;
                            result=new{updated,state="next_step_only"};
                        }
                        else if(name=="cancel_task") {if(Mode=="plan_only")throw new InvalidOperationException("plan_only");await _bridge.Control("cancel");result=new{status="cancel_requested"};}
                        else if(ToolCatalog.Actions.Contains(name)||BusinessCapabilities.Find(name) is {Query:false})
                        {
                            if(Mode=="plan_only"||!_resumed)throw new InvalidOperationException("Agent 已暂停，不派发动作");
                            if(mutation)throw new InvalidOperationException("本轮已派发一项任务；其余动作未执行，请等实际回执后逐项选择");
                            ValidateSkillScope(name,arguments);
                            var latest=_bridge.Observation!;
                            var authorized=PermissionPolicy.Authorize(name,arguments,_memory.Chests(scope),latest);
                            Trace("permission_allowed",new{name,arguments=authorized},decisionId);
                            if(latest.ActiveTaskId!=""){await _bridge.Control("cancel");await Task.Delay(100,cancellation);}
                            if(epoch!=Interlocked.Read(ref _epoch))return;
                            var taskId=Guid.NewGuid().ToString("N");
                            _taskDecisions[taskId]=decisionId;
                            if(nextProject!="")_taskProjects[taskId]=nextProject;
                            Trace("task_dispatch",new{taskId,skill=name,arguments=authorized,source="model"},decisionId);
                            try { result=await _bridge.Dispatch(name,authorized,cancellation:cancellation,taskId:taskId); }
                            catch { _taskProjects.TryRemove(taskId,out _);throw; }
                            mutation=true;
                        }
                        else throw new InvalidOperationException("模型选择了未注册工具");
                    }
                    catch(Exception ex)when(ex is not OperationCanceledException){result=new{status="rejected",reason=SafeError(ex)};RecordActionFeedback(new{status="rejected",reason=SafeError(ex),tool=name,arguments});Trace("tool_rejected",new{name,reason=SafeError(ex)},decisionId);}
                    Trace("tool_result",new{name,result},decisionId);
                    object? modelResult=DecisionContextCompression.ToolResult(name,result);
                    if(!seasonMaintenance&&name=="observe"&&result is TaskResult {Status:"completed"} observed){
                        var refreshed=JsonSerializer.Deserialize<GameObservation>(JsonSerializer.Serialize(_bridge.Observation??state))!;
                        if(DecisionContextCompression.Decode(JsonValue.Create(observed.Details)) is JsonObject observedWorld){
                            refreshed.WorldView=observedWorld.ToJsonString();refreshed.Revision=observed.ObservationRevision;
                            refreshed.Location=observedWorld["location"]?.ToString()??refreshed.Location;
                            refreshed.TimeOfDay=observedWorld["decisionFacts"]?["calendar"]?["timeOfDayHHMM"]?.GetValue<int>()??refreshed.TimeOfDay;
                            refreshed.GameDay=observedWorld["observedAt"]?.GetValue<int>()??refreshed.GameDay;
                        }
                        context=BuildDecisionInput(refreshed,delivery,conversation,DecisionTools(refreshed),instruction);
                        messages[1]!["content"]=DecisionContextCompression.Serialize(context);
                        modelResult=new{status=observed.Status,reason=observed.Reason,observationRevision=observed.ObservationRevision,observationUpdated=true,reference="world"};
                    }
                    messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]=call!["id"]!.GetValue<string>(),["content"]=DecisionContextCompression.Serialize(modelResult)});
                    if(mutation||PendingQuestion!="")break;
                }
                if(mutation){_emptyDecisionRetries=0;break;}
                if(round==2&&reply.Calls.Count>0&&PendingQuestion==""&&_waitDue==0&&_bridge.Observation?.ActiveTaskId==""&&_planTask is not {IsCompleted:false}){
                    _triggerKind="tool_followup";WakeDecision(_bridge.Observation?.Revision??state.Revision);
                }
            }
        }
        catch(OperationCanceledException){Trace("decision_cancelled",new{reason=epoch==Interlocked.Read(ref _epoch)?"timeout":"context_changed"},decisionId);if(epoch==Interlocked.Read(ref _epoch)){Status="模型请求超时；当前本地任务继续";_retryAfter=Environment.TickCount64+60000;Changed?.Invoke();}}
        catch(Exception ex){Status="模型暂不可用，60秒后自动重试："+SafeError(ex);_retryAfter=Environment.TickCount64+60000;Log(new{kind="model_error",error=ex.GetType().Name});Trace("model_error",new{error=SafeError(ex)},decisionId);Changed?.Invoke();}
        finally{
            if(_planTask is not {IsCompleted:false}&&Environment.TickCount64>=_decisionDue)_decisionDue=Environment.TickCount64+1000;
            if(seasonMaintenance&&epoch==Interlocked.Read(ref _epoch)&&scope==_scope&&session==_session&&_resumed&&_retryAfter==0){
                _triggerKind="season_maintenance_completed";WakeDecision(_bridge.Observation?.Revision??0);
            }
            else if(!seasonMaintenance&&epoch==Interlocked.Read(ref _epoch)&&_resumed&&PendingQuestion==""&&_waitDue==0&&_retryAfter==0
                &&_bridge.Observation?.ActiveTaskId==""&&_planTask is not {IsCompleted:false}&&Volatile.Read(ref _decisionRequested)==0){
                var delaySeconds=Math.Min(60,5*(1<<Math.Min(_emptyDecisionRetries++,4)));
                _triggerKind="no_action_retry";_retryAfter=Environment.TickCount64+delaySeconds*1000;
                Status=$"模型未选择下一步工具，{delaySeconds}秒后重新规划";Trace("no_action_retry_scheduled",new{delaySeconds},decisionId);Changed?.Invoke();
            }
            Trace("decision_finished",new{},decisionId);_request?.Dispose();_request=null;
        }
    }
    private void Say(string text){_memory.Chat(_scope,"assistant",text);ChatReceived?.Invoke("AI",text);}
    private void Trace(string kind,object data,string decision=""){
        Decisions.Write(_scope,_session,decision,kind,data);
        if(_scope!="")_memory.ArchiveDiagnostic(_scope,_session,kind,decision,JsonSerializer.Serialize(data));
    }
    private static string SafeError(Exception e)=>e switch{
        HttpRequestException {InnerException:{ } inner} http=>$"{http.Message} ({inner.GetType().Name}: {inner.Message})",
        HttpRequestException http=>http.Message,
        IOException io=>io.Message,
        TimeoutException timeout=>timeout.Message,
        InvalidOperationException or ArgumentException=>e.Message,
        _=>e.GetType().Name};
    private void Log(object value){try{Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);lock(_logPath)File.AppendAllText(_logPath,JsonSerializer.Serialize(new{at=DateTime.UtcNow,data=value})+Environment.NewLine);}catch(IOException){}}
    public ValueTask DisposeAsync()
    {
        lock(_lifecycleGate) return new(_disposeTask??=DisposeCore());
    }
    private async Task DisposeCore()
    {
        _bridge.Observed-=OnObserved;_bridge.Result-=OnResult;
        _lifetime.Cancel();
        try
        {
            try { await Pause(); }
            catch(Exception ex) when(ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException) { }
        }
        finally
        {
            try
            {
                await Task.WhenAll(new[]{_loop,_thinking,_planTask}.OfType<Task>());
            }
            finally
            {
                _planCancellation?.Dispose();_lifetime.Dispose();_memory.Dispose();
            }
        }
    }
}
