using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class PlanChecks
{
    internal static async Task Run(string folder)
    {
        static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
        static void Reject(JsonObject args){try{ShortPlan.Parse(args);}catch(InvalidOperationException){return;}throw new Exception("Invalid plan was accepted");}
        static JsonObject Args(int count)=>new(){["planId"]="test-plan",["steps"]=new JsonArray(Enumerable.Range(0,count).Select(i=>(JsonNode)new JsonObject{["skill"]="go_to",["arguments"]=new JsonObject{["location"]="Farm",["x"]=i+1,["y"]=15}}).ToArray())};
        Reject(Args(9));
        var invalid=Args(2);invalid["steps"]![0]!["skill"]="sleep";invalid["steps"]![0]!["arguments"]=new JsonObject();Reject(invalid);
        invalid=Args(2);invalid["steps"]![1]!["arguments"]!.AsObject().Remove("x");Reject(invalid);
        invalid=Args(1);invalid["steps"]![0]!["skill"]="plant_area";invalid["steps"]![0]!["arguments"]=new JsonObject{["location"]="Farm",["x"]=1,["y"]=1,["width"]=1,["height"]=1};Reject(invalid);
        Check(true,"short plans reject >8 steps, nonfinal sleep, missing coordinates and unspecified seed before execution");
        var fishingPlan=new JsonObject{["planId"]="fish-plan",["encounterPolicy"]="avoid",["steps"]=new JsonArray(new JsonObject{["skill"]="fish_at",["arguments"]=new JsonObject{["location"]="Beach",["x"]=10,["y"]=10,["direction"]="right",["casts"]=3,["treasurePolicy"]="ignore"}})};
        Check(ShortPlan.Parse(fishingPlan).EncounterPolicy=="avoid"&&ShortPlan.Parse(fishingPlan).Steps[0].Skill=="fish_at","fish_at and explicit encounter policy pass the production short-plan parser");
        fishingPlan["encounterPolicy"]="invented";Reject(fishingPlan);
        var combatPlan=new JsonObject{["planId"]="combat-plan",["steps"]=new JsonArray(new JsonObject{["skill"]="fight_enemies",["arguments"]=new JsonObject{["maxKills"]=2,["maxGameMinutes"]=30,["weaponId"]="(W)0"}})};
        Check(ShortPlan.Parse(combatPlan).EncounterPolicy=="interrupt","short plans default encounter policy to interrupt and combat parameters validate");
        var minePlan=new JsonObject{["planId"]="mine-plan",["steps"]=new JsonArray(new JsonObject{["skill"]="mine_travel",["arguments"]=new JsonObject{["targetFloor"]=5}})};
        try{PermissionPolicy.Authorize("mine_travel",minePlan["steps"]![0]!["arguments"]!.AsObject(),Array.Empty<ChestBinding>(),new(){WorldReady=true});throw new Exception("Disabled mine travel was authorized");}
        catch(InvalidOperationException){Check(true,"mine travel remains disabled at the permission boundary");}
        using(var db=new MemoryStore(Path.Combine(folder,"persistent-instructions.db"))){
            db.AppendPlayerInstruction("a","five days; no fishing");
            for(var i=0;i<30;i++)db.Chat("a","assistant","intention only");
            Check(db.PlayerInstructions("a").Single()=="five days; no fishing"&&db.PlayerInstructions("b").Length==0,"player objective survives chat truncation and is isolated by scope");
        }
        using(var db=new MemoryStore(Path.Combine(folder,"persistent-instructions.db")))
            Check(db.PlayerInstructions("a").Single().Contains("no fishing"),"objective survives application restart");
        var treeArgs=Args(1);treeArgs["steps"]![0]!["skill"]="clear_area";treeArgs["steps"]![0]!["arguments"]=new JsonObject{["location"]="Farm",["x"]=10,["y"]=10,["width"]=3,["height"]=3,["targetType"]="tree",["removeStumps"]=false};
        Check(ShortPlan.Parse(treeArgs).Steps[0].Arguments["targetType"]!.GetValue<string>()=="tree","mature trees are reachable through the actual model plan schema with explicit stump policy");
        treeArgs["steps"]![0]!["arguments"]!["removeStumps"]="false";Reject(treeArgs);
        treeArgs["steps"]![0]!["arguments"]!["removeStumps"]=false;treeArgs["steps"]![0]!["arguments"]!["targetType"]="debris";
        Check(ShortPlan.Parse(treeArgs).Steps.Length==1,"debris clearing policy is accepted by the primitive schema");
        Check(ControlModePolicy.CanControl(new(){WorldReady=true,IsHost=true,ControlMode="solo_test",Multiplayer=false,PlayerName="SC5Host",TestRunId="SC10D_check"}),"ten-day run naming preserves explicit isolated host control");
        foreach(var scenario in new[]{"eight","failure","pause","low_stamina_late","save_then_execute","invalid_json"}){
            var bridge=new PlanBridge(scenario);
            var planArgs=Args(scenario is "eight" or "low_stamina_late" or "save_then_execute" or "invalid_json"?8:3);
            if(scenario=="low_stamina_late")foreach(var step in planArgs["steps"]!.AsArray()){
                step!["skill"]="water_area";step["arguments"]!["width"]=1;step["arguments"]!["height"]=1;
            }
            var handler=new PlanModel(planArgs,bridge);
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"plan-"+scenario+".db")),settings,new HttpClient(handler),Path.Combine(folder,"plan-"+scenario+".jsonl"));
            bridge.Publish();runtime.SendMessage("autonomous greedy opening; no fishing");await runtime.Resume();runtime.Start();
            var until=Environment.TickCount64+7000;
            if(scenario=="pause"){
                while(bridge.Commands.Count<1&&Environment.TickCount64<until)await Task.Delay(10);
                await runtime.Pause();await Task.Delay(200);
                Check(bridge.Commands.Count==1,"pause cancels remaining plan steps without dispatching step two");continue;
            }
            while(!runtime.Decisions.Read().Any(e=>e.Kind=="plan_finished")&&Environment.TickCount64<until)await Task.Delay(10);
            Check(runtime.Decisions.Read().Any(e=>e.Kind=="plan_finished"),"plan reaches a recorded terminal result: "+scenario);
            if(scenario is "eight" or "low_stamina_late" or "save_then_execute" or "invalid_json"){
                Check(bridge.Commands.Count==8&&handler.BetweenStepRequests==0,"eight verified steps execute with one model selection and no between-step requests");
                await Task.Delay(700);
                Check(handler.Requests>=2,"plan completion wakes the next decision without twenty-second delay");
                Check(bridge.Commands.Count==8,"duplicate plan id is not executed a second time");
                if(scenario=="save_then_execute")Check(runtime.Plans.Single().Revision>=2,"saving a public plan allows a group tool in the same model response");
                if(scenario=="invalid_json")Check(handler.Requests>=2&&runtime.Decisions.Read().Any(e=>e.Kind=="tool_rejected")&&!runtime.Decisions.Read().Any(e=>e.Kind=="model_error"),"malformed arguments are returned to the model for correction without a global one-minute failure cooldown");
            }else Check(bridge.Commands.Count==2,"failed step stops all remaining steps");
            if(scenario=="low_stamina_late"){
                Check(!runtime.Decisions.Read().Any(e=>e.Kind=="safety_trigger"||e.Kind=="task_dispatch"&&e.Data.GetProperty("source").GetString()!="model"),"negative stamina and 25:30 never trigger local return-home or sleep takeover");
            }
            await runtime.Pause();
        }
        var trialBridge=new PlanBridge("trial");trialBridge.Observation!.GameDay=5;
        await using(var trial=new CompanionRuntime(trialBridge,new MemoryStore(Path.Combine(folder,"ten-day-boundary.db")),new ModelSettings(),new HttpClient(),Path.Combine(folder,"ten-day-boundary.jsonl"))){
            trial.PlanningEnabled=false;trial.AutonomousTrialEndDay=10;trialBridge.Observation.ControlMode="solo_test";
            trialBridge.Publish();await trial.Resume();trial.Start();await Task.Delay(300);
            Check(trial.Running,"ten-day trial continues after day five");
            trialBridge.Observation.GameDay=10;trialBridge.Observation.Menu="SaveGameMenu";await Task.Delay(300);
            Check(trial.Running&&!trial.Decisions.Read().Any(e=>e.Kind=="trial_completed"),"trial boundary waits for native save/menu completion");
            trialBridge.Observation.Menu="";var deadline=Environment.TickCount64+3000;
            while(!trial.Decisions.Read().Any(e=>e.Kind=="trial_completed")&&Environment.TickCount64<deadline)await Task.Delay(25);
            Check(!trial.Running&&trial.Decisions.Read().Single(e=>e.Kind=="trial_completed").Data.GetProperty("targetDays").GetInt32()==10,"configured ten-day boundary pauses once on the eleventh morning");
        }
        var state=new GameObservation{Location="Farm",Tile=new(){X=0,Y=0},WorldView="""{"soil":[{"x":8,"y":8,"dead":false,"needsWater":true},{"x":1,"y":1,"dead":false,"ready":true}],"forage":[{"x":0,"y":1}]}"""};
        Check(JsonSerializer.Serialize(GreedyCandidates.Read(state)[0]).Contains("water_area"),"greedy ranking protects existing crops before nearby optional gains");
        var tools=ToolCatalog.Definitions(true);
        Check(!tools.Any(t=>t!["function"]!["name"]!.GetValue<string>()=="execute_plan")&&tools.Count(t=>t!["function"]!["name"]!.GetValue<string>().StartsWith("group_"))==8&&tools.Any(t=>t!["function"]!["name"]!.GetValue<string>()=="plant_area"),"model sees eight group tools and the frequent direct tools without the retired execute_plan interface");
    }
    private sealed class PlanModel(JsonObject args,PlanBridge bridge):HttpMessageHandler
    {
        public int Requests;public int BetweenStepRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            if(bridge.Commands.Count is >0 and <8&&bridge.Scenario is "eight" or "save_then_execute")BetweenStepRequests++;
            Requests++;
            var calls=new List<object>();
            if(bridge.Scenario=="save_then_execute")calls.Add(new{id="save",type="function",function=new{name="save_plan",arguments=JsonSerializer.Serialize(new{planId="test-plan",goal="reach declared targets",basis="observed state",assumptions=Array.Empty<string>(),successCriteria=new[]{"actual receipts"},pending=Array.Empty<string>()})}});
            var skill=args["steps"]![0]!["skill"]!.GetValue<string>();
            calls.Add(new{id="plan",type="function",function=new{name="group_"+BusinessCapabilities.GroupFor(skill),arguments=bridge.Scenario=="invalid_json"&&Requests==1?"{\"planId\": 未引用}":args.ToJsonString()}});
            var response=new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=calls}}}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response))});
        }
    }
    private sealed class PlanBridge(string scenario):IGameBridge
    {
        public string Scenario=>scenario;
        public GameObservation? Observation{get;}=new(){SessionId="plan-session",SaveId="plan-save",PlayerId=8,Revision=1,WorldReady=true,Location="Farm",Stamina=270,Health=100,TimeOfDay=900,WorldView="{}"};
        public bool Connected=>true;public bool Fresh=>true;
        public event Action<GameObservation>? Observed;
        public event Action<TaskResult>? Result;
        public ConcurrentQueue<string> Commands{get;}=new();
        public void Publish(){if(scenario=="low_stamina_late"){Observation!.Stamina=-5;Observation.TimeOfDay=2530;}Observed?.Invoke(Observation!);}
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object args,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt"){
            taskId??=Guid.NewGuid().ToString("N");Commands.Enqueue(skill);var number=Commands.Count;
            Observation!.ActiveTaskId=taskId;
            var id=taskId;
            _=Task.Run(async()=>{
                await Task.Delay(scenario=="pause"?800:35);
                var revision=Observation.Revision;Observation.Revision++;Observation.ActiveTaskId="";
                Result?.Invoke(new(){TaskId=id,Skill=skill,Status=scenario=="failure"&&number==2?"failed":"completed",Reason="native_test_result",Completed=1,ObservationRevision=revision});
            });
            return Task.FromResult(new TaskResult{TaskId=id,Skill=skill,Status="accepted"});
        }
    }
}

