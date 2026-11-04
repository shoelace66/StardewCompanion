using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class AwarenessChecks
{
    static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
    static async Task Until(Func<bool> condition,string name)
    {var deadline=Environment.TickCount64+8000;while(!condition()&&Environment.TickCount64<deadline)await Task.Delay(20);Check(condition(),name);}
    internal static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);var dbPath=Path.Combine(folder,"awareness.db");var log=Path.Combine(folder,"awareness-events.jsonl");
        var model=new CaptureModel();var bridge=new AwarenessBridge();
        var settings=new ModelSettings{Endpoint="http://localhost:5011/v1/chat/completions",Model="awareness-check"};settings.SetKey("secret-must-not-be-captured");
        await using(var runtime=new CompanionRuntime(bridge,new MemoryStore(dbPath),settings,new HttpClient(model),log)){
            bridge.Publish();runtime.Start();await runtime.Resume();runtime.SendMessage("采黄水仙然后回家睡觉");
            await Until(()=>model.Requests.Count>=3,"failure and duplicate rejection both trigger a new model request");
            await runtime.Pause();
            var requests=model.Requests.ToArray();var feedback=Context(requests[1])["agentState"]!.ToJsonString();
            Check(feedback.Contains("inventory_full")&&feedback.Contains("forage_area")&&feedback.Contains("52"),"next actual request contains full-bag failure and the unexecuted same-group forage target");
            Check(Context(requests[2])["agentState"]!.ToJsonString().Contains("unchanged_target_previously_failed")&&Context(requests[2])["agentState"]!.ToJsonString().Contains("inventory_full"),"duplicate target rejection retains the original inventory_full reason");
            Check(bridge.Commands.Count==1&&bridge.Commands.Single()=="forage_area","failed forage never executes dependent steps or repeats the actual failed action");
            Check(runtime.AwarenessSnapshot()["actionFeedback"]!["blockers"]!.AsArray().Count==1,"task and plan summaries merge into one unresolved blocker");
            bridge.FreeSlots=1;bridge.Publish();
            Check(runtime.AwarenessSnapshot()["actionFeedback"]!["blockers"]!.AsArray().Count==0,"freeing a backpack slot resolves the inventory blocker");
            bridge.EventIds=new[]{"mail-1","passout-1","day-1"};bridge.Publish();bridge.Publish();
            Check(runtime.AwarenessSnapshot()["pendingEvents"]!.AsArray().Count==3&&bridge.Acks.Contains("mail-1"),"paused events are durably received and deduplicated before bridge intake acknowledgement");
            var count=model.Requests.Count;model.Fail=true;await runtime.Resume();runtime.SendMessage("核对事件");
            await Until(()=>runtime.Decisions.Read().Any(e=>e.Kind=="model_error"),"failed HTTP request is observed");await runtime.Pause();
            Check(model.Requests.Count>count&&runtime.AwarenessSnapshot()["pendingEvents"]!.AsArray().Count==3,"failed model response leaves all events pending");
            model.Fail=false;await runtime.Resume();runtime.SendMessage("重试核对事件");
            await Until(()=>runtime.AwarenessSnapshot()["pendingEvents"]!.AsArray().Count==0,"successful model response confirms only the delivered events");await runtime.Pause();
            Check(runtime.AwarenessSnapshot()["deliveredEvents"]!.AsArray().Count==3,"mail, pass-out and day-change delivery is visible for debugging");
            var captures=runtime.Decisions.Read(0,10000).Where(e=>e.Kind=="model_prompt_capture").ToArray();
            var sent=model.Requests.ToArray();
            Check(captures.Length==sent.Length&&captures.Select((e,i)=>JsonNode.Parse(e.Data.GetProperty("prompt").GetRawText())!.ToJsonString()==sent[i].ToJsonString()).All(x=>x),"every successful or failed request capture equals the complete actual HTTP JSON body");
            Check(captures.All(e=>!e.Data.GetRawText().Contains("secret-must-not-be-captured")&&e.Data.GetProperty("prompt").TryGetProperty("max_tokens",out _)),"captures include model parameters and exclude authorization secrets");
            bridge.EventIds=new[]{"mail-2"};bridge.Publish();
            runtime.Memory.AppendPlayerInstruction(runtime.Scope,"为什么不去社交一下？");
            Check(!runtime.Memory.PlayerInstructions(runtime.Scope).Any(x=>x.Contains("为什么")),"ordinary chat questions are not persisted as instructions");
            bridge.Observation!.GameDay++;bridge.Publish();
            Check(runtime.Memory.Get(runtime.Scope,"season_maintenance")!.Contains("spring"),"sleep maintenance resolves the actual decisionFacts.calendar.season path");
        }
        {
            var replacementBridge=new AwarenessBridge();var replacementSettings=new ModelSettings{Endpoint="http://localhost:5011/v1/chat/completions",Model="replacement-goal-check"};
            await using var replacement=new CompanionRuntime(replacementBridge,new MemoryStore(Path.Combine(folder,"replacement-goal.db")),replacementSettings,new HttpClient(new CaptureModel()),Path.Combine(folder,"replacement-goal.jsonl"));
            replacementBridge.Publish();
            replacement.Memory.Set(replacement.Scope,"decision_awareness",new JsonObject{
                ["revision"]=4,["actionFeedback"]=new JsonObject{["revision"]=3,["deliveredRevision"]=1,["latest"]=new JsonObject{["reason"]="old_path_unreachable"},["blockers"]=new JsonArray(new JsonObject{["reason"]="old_path_unreachable"})},
                ["pendingEvents"]=new JsonArray(),["deliveredEvents"]=new JsonArray(),["seenEventIds"]=new JsonArray()}.ToJsonString());
            await replacement.UpdateGoal(new PlayerGoalUpdate{Objective="继续投资土豆并完成播种"});
            var feedback=replacement.AwarenessSnapshot()["actionFeedback"]!;
            Check(feedback["blockers"]!.AsArray().Count==0&&feedback["latest"]==null&&feedback["revision"]!.GetValue<long>()==feedback["deliveredRevision"]!.GetValue<long>(),"a replacement goal retires stale action feedback without granting permissions");
        }
        await using(var restored=new CompanionRuntime(bridge,new MemoryStore(dbPath),settings,new HttpClient(new CaptureModel()),Path.Combine(folder,"restored.jsonl"))){
            bridge.EventIds=new[]{"mail-1","mail-2"};bridge.Publish();
            Check(restored.AwarenessSnapshot()["pendingEvents"]!.AsArray().Count==1,"restart retains pending mail but does not replay acknowledged older events");
            bridge.Observation!.SaveId="other-save";bridge.EventIds=Array.Empty<string>();bridge.Publish();
            Check(restored.AwarenessSnapshot()["pendingEvents"]!.AsArray().Count==0,"event queues are isolated by save and farmer");
        }
        var compressed=DecisionContextCompression.Build(new JsonObject{["actionFeedback"]=new JsonObject{["reason"]="inventory_full"},["gameEvents"]=new JsonArray(new JsonObject{["type"]="mail_arrived"}),["instruction"]=new string('x',500)},1);
        Check(compressed.OverBudget&&compressed.Context["actionFeedback"]!=null&&compressed.Context["gameEvents"]!=null,"compression never drops current feedback or events even when over budget");
    }
    static JsonNode Context(JsonObject request)=>JsonNode.Parse(request["messages"]!.AsArray().First(m=>m?["role"]?.ToString()=="user")!["content"]!.GetValue<string>())!;
    sealed class CaptureModel:HttpMessageHandler
    {
        public ConcurrentQueue<JsonObject> Requests=new();public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests.Enqueue(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if(Fail)return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("unavailable")};
            var n=Requests.Count;var name=n<=2?"group_resources_exploration":"wait_for_change";
            object args=n<=2?new{planId="forage-"+n,steps=new[]{new{skill="forage_area",arguments=new{location="Town",x=51,y=31,width=1,height=1}},new{skill="forage_area",arguments=new{location="Town",x=52,y=31,width=1,height=1}}}}:new{seconds=120,reason="验证后等待"};
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=new[]{new{id="call-"+n,type="function",function=new{name,arguments=JsonSerializer.Serialize(args)}}}}}}}))};
        }
    }
    sealed class AwarenessBridge:IGameBridge
    {
        public GameObservation? Observation{get;}=new(){SessionId="awareness-session",SaveId="awareness-save",PlayerId=1,Revision=1,WorldReady=true,Location="Town",Tile=new(){X=51,Y=30},TimeOfDay=1800,GameDay=3,Stamina=68,Health=100};
        public int FreeSlots;public string[] EventIds=Array.Empty<string>();
        public bool Connected=>true;public bool Fresh=>true;
        public event Action<GameObservation>? Observed;public event Action<TaskResult>? Result;
        public ConcurrentQueue<string> Commands=new();public ConcurrentQueue<string> Acks=new();
        public void Publish(){Observation!.WorldView=JsonSerializer.Serialize(new{inventoryState=new{capacity=12,freeSlots=FreeSlots},forage=new[]{new{x=51,y=31,id="(O)18",name="黄水仙",canAccept=FreeSlots>0}},decisionFacts=new{calendar=new{season="spring"}},gameEvents=EventIds.Select(id=>new{id,type=id.StartsWith("mail")?"mail_arrived":id.StartsWith("passout")?"pass_out":"day_changed",facts=new{source="test_native_event"}})});Observed?.Invoke(Observation);}
        public Task<string> AcknowledgeEvents(string[] ids){foreach(var id in ids)Acks.Enqueue(id);return Task.FromResult("");}
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object arguments,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt"){
            Commands.Enqueue(skill);var revision=Observation!.Revision++;var result=new TaskResult{TaskId=taskId!,Skill=skill,Status="failed",Reason="inventory_full",ObservationRevision=revision,Details=JsonSerializer.Serialize(new{requestedTarget=arguments,collectedItems=new Dictionary<string,int>()})};Result?.Invoke(result);return Task.FromResult(result);
        }
    }
}
