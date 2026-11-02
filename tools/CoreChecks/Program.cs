using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StardewCompanion.Core;
using StardewCompanion.Protocol;
using StardewCompanion.Fishing;

var root=Path.GetFullPath(args.FirstOrDefault()??".");
if(args.Contains("--awareness")){await AwarenessChecks.Run(Path.Combine(root,".local","checks","awareness-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")));return;}
if(args.Contains("--season-tools")){await SeasonToolChecks.Run(Path.Combine(root,".local","checks",Guid.NewGuid().ToString("N")));return;}
if(args.Contains("--live"))
{
    var settings=ModelSettings.Load(Path.Combine(root,".local","model.credentials.json"));
    using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(50)};
    var reply=await new ModelClient(http).TestToolCalling(settings,CancellationToken.None);
    Assert(reply.Calls.Any(c=>c?["function"]?["name"]?.GetValue<string>()=="observe"),"real configured model returns the forced registered tool call without dispatch");
    File.WriteAllText(Path.Combine(root,".local","model-smoke.json"),JsonSerializer.Serialize(new{model=settings.Model,elapsedMs=reply.ElapsedMs,usage=reply.Usage,testedFunction="observe",dispatched=false}));return;
}
var folder=Path.Combine(root,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
var fishPhysics=new FishingPhysics();fishPhysics.ApplyGear(new FishingGear(0,new TackleKit()));
var upward=new FishingState(.5f,0f,.5f,.3f);fishPhysics.Tick(ref upward,ControllerOutput.Press);
var falling=new FishingState(.5f,0f,.5f,.3f);fishPhysics.Tick(ref falling,ControllerOutput.Release);
Assert(upward.BarVelocity<0&&falling.BarVelocity>0,"fishing simulator input sign matches native: hold rises and release falls");
var timeout=new FishingSimulator(new FishSpecies("test",50,FishBehavior.Smooth),new FishingGear(0,new TackleKit()),maxTicks:0).Simulate(false);
Assert(timeout.Outcome==FishingOutcome.TimedOut,"simulator timeout cannot be reported as a catch");
var path=Path.Combine(folder,"memory.db");
using(var db=new MemoryStore(path))
{
    var id=db.Add("a:1","project","浇完授权区域后归仓","player",3,"observed");
    db.Add("b:1","agreement","不能移动我的工具","player",3,"player_stated");
    Assert(db.List("a:1").Count==1&&db.List("b:1").Count==1,"memory isolated by save and farmer");
    db.Record("a:1","old",4,"task","completed",3,"watered");
    db.Reconcile("a:1","new",2);
    Assert(db.List("a:1").Single().State=="needs_recheck","rollback marks world progress for rechecking");
    db.Edit("a:1",id,"只浇当前可见农作物");Assert(db.List("a:1").Single().Source=="player","player memory correction retains provenance");
    db.State("a:1",id,"cancelled");Assert(db.Relevant("a:1","浇水").Count==0,"cancelled project excluded from autonomous retrieval");
    db.Set("a:1","paused","true");
    db.Chat("a:1","user","真实聊天不能随回档被删");db.Reconcile("a:1","again",1);
    Assert(db.History("a:1").Count==1,"rollback preserves actual dialogue");
    var projectId=db.CreateProject("a:1","检查农场作物并逐日浇水",2);
    var project=db.Projects("a:1").Single();
    Assert(project.Goal.Contains("作物")&&project.Remaining.Length==1&&project.VerifiedResults.Length==0,"project starts with explicit evidence gaps and no invented completion");
    Assert(db.UpdateProjectNext("a:1",project.Id,"先观察农场的当前耕地"),"model can update a project next step");
    project=db.Projects("a:1").Single();Assert(project.VerifiedResults.Length==0&&project.NextStep.Contains("观察"),"project next-step update cannot fabricate completed work");
    var row=db.List("a:1").Single(m=>m.Id==projectId);db.State("a:1",projectId,"user_confirmed_complete");db.Reconcile("a:1","next",3);
    Assert(db.List("a:1").Single(m=>m.Id==projectId).State=="user_confirmed_complete","daily reconciliation preserves player-confirmed project completion");
    Assert(row.Display.Contains("作物"),"project memory is readable in the desktop list");
    db.State("a:1",projectId,"active");
    Assert(db.RecordProjectResult("a:1",project.Id,"before-rollback",4,new(){TaskId="harvest-before-rollback",Skill="harvest_area",Status="completed",Completed=7}),"project records an actual receipt");
    db.Reconcile("a:1","after-rollback",2);
    var reconciled=db.Projects("a:1").Single();
    Assert(reconciled.VerifiedResults.Length==0&&reconciled.UnverifiedResults.Length==1&&reconciled.Status=="needs_recheck","rollback removes stale receipts from model-facing verified progress and preserves their history");
    db.RecordProjectResult("a:1",project.Id,"after-rollback",2,new(){TaskId="current-receipt",Skill="water_area",Status="completed",Completed=1});
    db.Reconcile("a:1","after-rollback",3);
    Assert(db.Projects("a:1").Single().VerifiedResults.Length==1,"normal day transition retains verified receipts from the current session");
}
using(var db=new MemoryStore(path))Assert(db.Get("a:1","paused")=="true"&&db.List("a:1").Any(m=>m.State=="cancelled"),"pause and cancellation survive reopening");
var chest=new ChestBinding("工作箱","bound","Farm",1,2,new[]{"(O)388","(T)Axe"},new[]{"(O)24"});
var observation=new GameObservation{WorldReady=true,SessionId="session",SaveId="save",PlayerId=2,Revision=10,GameDay=1,Location="Farm",WorldView="{\"maxStamina\":270}",Stamina=270,Health=100,TimeOfDay=900};
var soloState=JsonSerializer.Deserialize<GameObservation>(JsonSerializer.Serialize(observation))!;soloState.IsHost=true;
Assert(!ControlModePolicy.CanControl(soloState),"ordinary host remains outside the controlled farmhand boundary");
soloState.ControlMode="solo_test";soloState.TestRunId="SC5D_check";soloState.PlayerName="SC5Host";
Assert(ControlModePolicy.CanControl(soloState),"explicit isolated solo test host can be controlled");
soloState.Multiplayer=true;Assert(!ControlModePolicy.CanControl(soloState),"solo test opt-in cannot authorize a multiplayer host");
soloState.Multiplayer=false;soloState.TestRunId="real-farm";Assert(!ControlModePolicy.CanControl(soloState),"normal saves cannot be opted into solo test control");
Reject(()=>PermissionPolicy.Authorize("deposit_items",new JsonObject{["chest"]="工作箱",["itemId"]="(O)388",["count"]=2,["chestId"]="forged"},new[]{chest},observation),"model cannot inject an internal chest binding");
var authorized=PermissionPolicy.Authorize("deposit_items",new JsonObject{["chest"]="工作箱",["itemId"]="(O)388",["count"]=2},new[]{chest},observation);
Assert(authorized["chestId"]!.GetValue<string>()=="bound","authorized chest name resolves to the player binding");
Reject(()=>PermissionPolicy.Authorize("withdraw_items",new JsonObject{["chest"]="工作箱",["itemId"]="(T)Axe",["count"]=1},new[]{chest},observation),"tool protection overrides allowlist");
Reject(()=>PermissionPolicy.Authorize("sell_items",new(),new[]{chest},observation),"business mutation with missing required arguments rejected");

var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var server=builder.Build();
var probeValidated=false;
server.MapPost("/probe",async context=>
{
    var body=await JsonNode.ParseAsync(context.Request.Body);
    var choice=body?["tool_choice"];
    var tools=body?["tools"]?.AsArray();
    probeValidated=choice?["type"]?.GetValue<string>()=="function"
        &&choice["function"]?["name"]?.GetValue<string>()=="observe"
        &&tools?.Any(t=>t?["function"]?["name"]?.GetValue<string>()=="observe")==true;
    await context.Response.WriteAsJsonAsync(new{choices=new[]{new{message=new{role="assistant",content=(string?)null,tool_calls=new[]{new{id="probe1",type="function",function=new{name="observe",arguments="{}"}}}}}},usage=new{total_tokens=3}});
});
var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var scenarioRequests=new System.Collections.Concurrent.ConcurrentQueue<JsonNode>();
var scenarioProject="";
server.MapPost("/scenario",async context=>
{
    var body=(await JsonNode.ParseAsync(context.Request.Body))!;scenarioRequests.Enqueue(body);
    var input=JsonNode.Parse(body["messages"]!.AsArray().First(m=>m?["role"]?.GetValue<string>()=="user")!["content"]!.GetValue<string>())!;
    var calls=new JsonArray();
    if(input["conversation"]?["newMessages"]?.AsArray().Any(m=>m?["text"]?.GetValue<string>()=="关联项目后切换任务")==true)
    {
        if(!body["tools"]!.AsArray().Any(t=>t?["function"]?["name"]?.GetValue<string>()=="update_project"))
            calls.Add(new JsonObject{["id"]="expand",["type"]="function",["function"]=new JsonObject{["name"]="list_capabilities",["arguments"]="{}"}});
        else {
        calls.Add(new JsonObject{["id"]="project",["type"]="function",["function"]=new JsonObject{["name"]="update_project",["arguments"]=JsonSerializer.Serialize(new{projectId=scenarioProject,nextStep="走到农场入口"})}});
        calls.Add(new JsonObject{["id"]="travel",["type"]="function",["function"]=new JsonObject{["name"]="go_to",["arguments"]="{\"location\":\"Farm\",\"x\":1,\"y\":2}"}});
        }
    }
    await context.Response.WriteAsJsonAsync(new JsonObject{["choices"]=new JsonArray(new JsonObject{["message"]=new JsonObject{["role"]="assistant",["content"]="",["tool_calls"]=calls}})});
});
server.MapPost("/chat/completions",async context=>
{
    requested.TrySetResult();await release.Task;
    await context.Response.WriteAsJsonAsync(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=new[]{new{id="call1",type="function",function=new{name="go_to",arguments="{\"location\":\"Farm\",\"x\":1,\"y\":2}"}}}}}},usage=new{total_tokens=15}});
});
await server.StartAsync();
var probeSettings=new ModelSettings{Endpoint=server.Urls.Single()+"/probe",Model="local-fake"};probeSettings.SetKey("test-key-not-real");
using(var probeHttp=new HttpClient())
{
    var probe=await new ModelClient(probeHttp).TestToolCalling(probeSettings,CancellationToken.None);
    Assert(probeValidated&&probe.Calls.Count==1,"desktop model test forces a registered observe tool call without dispatching it");
}
var modelSettings=new ModelSettings{Endpoint=server.Urls.Single()+"/chat/completions",Model="local-fake"};modelSettings.SetKey("test-key-not-real");
var bridge=new FakeBridge();var store=new MemoryStore(Path.Combine(folder,"runtime.db"));
await using(var runtime=new CompanionRuntime(bridge,store,modelSettings,new HttpClient(),Path.Combine(folder,"events.jsonl")))
{
    runtime.Start();bridge.Observe(observation);await runtime.Resume();runtime.SendMessage("走到农场 1,2");
    await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var timer=System.Diagnostics.Stopwatch.StartNew();await runtime.Pause();Assert(timer.ElapsedMilliseconds<250,"runtime pause independent of pending model HTTP");
    release.TrySetResult();await Task.Delay(500);
    Assert(bridge.Tasks.Count==0,"late model reply cannot dispatch after pause");
    Assert(bridge.Controls.Contains("pause"),"pause reaches local bridge");
}
var scenarioSettings=new ModelSettings{Endpoint=server.Urls.Single()+"/scenario",Model="local-fake"};scenarioSettings.SetKey("test-key-not-real");
var projectBridge=new FakeBridge();
await using(var projectRuntime=new CompanionRuntime(projectBridge,new MemoryStore(Path.Combine(folder,"project-runtime.db")),scenarioSettings,new HttpClient(),Path.Combine(folder,"project-runtime.jsonl")))
{
    projectBridge.Observe(observation);projectRuntime.AddMemory("project","先走到入口");
    scenarioProject=projectRuntime.Memory.Projects(projectRuntime.Scope).Single().Id;
    projectBridge.Observation!.ActiveTaskId="previous-task";
    await projectRuntime.Resume();projectRuntime.SendMessage("关联项目后切换任务");projectRuntime.Start();
    await Until(()=>projectBridge.Commands.Count==1,"model switches from an existing task to a project task");
    var task=projectBridge.Commands.Single();
    projectBridge.Complete(task.TaskId,"completed","destination_observed",2);
    var journal=projectRuntime.Decisions.Read();
    var dispatch=journal.Single(e=>e.Kind=="task_dispatch"&&e.Data.GetProperty("taskId").GetString()==task.TaskId);
    Assert(dispatch.DecisionId!=""&&journal.Any(e=>e.Kind=="task_result"&&e.DecisionId==dispatch.DecisionId&&e.Data.GetProperty("TaskId").GetString()==task.TaskId),"decision journal correlates model dispatch and actual task result");
    Assert(journal.Any(e=>e.Kind=="decision_context")&&journal.Any(e=>e.Kind=="model_reply")&&journal.Any(e=>e.Kind=="permission_allowed"),"decision journal exposes context, public model reply and permission outcome");
    Assert(projectRuntime.Memory.Projects(projectRuntime.Scope).Single().VerifiedResults.Length==1,"old cancellation cannot clear the new task's explicit project association");
    projectRuntime.SendMessage("查看执行回执");
    await Until(()=>scenarioRequests.Any(body=>body["messages"]!.AsArray().Any(m=>m?["role"]?.GetValue<string>()=="user"&&m["content"]!.GetValue<string>().Contains(task.TaskId))),"next model decision receives the actual terminal task receipt");
    await projectRuntime.Pause();
}
var isolatedBridge=new FakeBridge();
await using(var isolated=new CompanionRuntime(isolatedBridge,new MemoryStore(Path.Combine(folder,"isolation.db")),scenarioSettings,new HttpClient(),Path.Combine(folder,"isolation.jsonl")))
{
    isolatedBridge.Observe(observation);isolated.SendMessage("旧存档不能带入的新指令");
    var other=JsonSerializer.Deserialize<GameObservation>(JsonSerializer.Serialize(observation))!;other.SessionId="another-session";other.SaveId="another-save";
    isolatedBridge.Observe(other);isolated.Start();await Task.Delay(400);
    Assert(!scenarioRequests.Any(body=>body["messages"]!.AsArray().Any(m=>m?["role"]?.GetValue<string>()=="user"&&m["content"]!.GetValue<string>().Contains("旧存档不能带入的新指令"))),"queued player instructions are discarded when save or session changes");
    Assert(isolated.Memory.History("save:2").Single().Text=="旧存档不能带入的新指令"&&isolated.Memory.History("another-save:2").Count==0,"discarded instructions remain only in their original save's chat history");
}
await ContextChecks.Run(root,folder);
await PlanChecks.Run(folder);
await AwarenessChecks.Run(Path.Combine(folder,"awareness"));
await SeasonToolChecks.Run(folder);
await server.StopAsync();
Console.WriteLine("All core checks passed; live game behavior is tested separately.");

static void Assert(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);Console.WriteLine("PASS: "+text);}
static void Reject(Action action,string text){try{action();}catch(InvalidOperationException){Console.WriteLine("PASS: "+text);return;}throw new Exception("FAIL: "+text);}
static async Task Until(Func<bool> condition,string text)
{var until=Environment.TickCount64+7000;while(!condition()&&Environment.TickCount64<until)await Task.Delay(25);Assert(condition(),text);}
sealed class FakeBridge:IGameBridge
{
    public GameObservation? Observation{get;private set;}
    public bool Connected=>true;
    public bool Fresh=>true;
    public event Action<GameObservation>? Observed;
    public event Action<TaskResult>? Result;
    public System.Collections.Concurrent.ConcurrentQueue<string> Controls{get;}=new();public System.Collections.Concurrent.ConcurrentQueue<string> Tasks{get;}=new();
    public System.Collections.Concurrent.ConcurrentQueue<(string TaskId,string Skill)> Commands{get;}=new();
    public void Observe(GameObservation o){Observation=JsonSerializer.Deserialize<GameObservation>(JsonSerializer.Serialize(o))!;Observed?.Invoke(Observation);}
    public Task<string> Control(string type)
    {
        Controls.Enqueue(type);
        if(type is "cancel" or "pause" && Observation?.ActiveTaskId is {Length:>0} task)Complete(task,"cancelled","cancelled_by_player");
        return Task.FromResult(Guid.NewGuid().ToString("N"));
    }
    public void Complete(string task,string status,string reason,int count=0)
    {
        var skill=Commands.FirstOrDefault(c=>c.TaskId==task).Skill??"";
        if(Observation?.ActiveTaskId==task)Observation.ActiveTaskId="";
        Result?.Invoke(new(){TaskId=task,Skill=skill,Status=status,Reason=reason,Completed=count});
    }
    public Task<TaskResult> Dispatch(string skill,object arguments,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt")
    {taskId??=Guid.NewGuid().ToString("N");Tasks.Enqueue(skill);Commands.Enqueue((taskId,skill));if(Observation!=null)Observation.ActiveTaskId=taskId;var r=new TaskResult{TaskId=taskId,Skill=skill,Status="accepted"};Result?.Invoke(r);return Task.FromResult(r);}
}

