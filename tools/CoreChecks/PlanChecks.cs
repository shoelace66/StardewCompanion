using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class PlanChecks
{
    internal static async Task RunContinuation(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach(var scenario in new[]{"eight","direct","failure","pause"}){
            var bridge=new PlanBridge(scenario,holdPlan:scenario!="direct");
            bridge.Observation!.WorldView="""{"decisionFacts":{"calendar":{"season":"spring"}}}""";
            var args=new JsonObject{["planId"]="continuation-test",["steps"]=new JsonArray(Enumerable.Range(0,8).Select(i=>(JsonNode)new JsonObject{
                ["skill"]="go_to",["arguments"]=new JsonObject{["location"]="Farm",["x"]=i+1,["y"]=15}}).ToArray())};
            var handler=new PlanModel(args,bridge);
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,scenario+".db")),settings,new HttpClient(handler),Path.Combine(folder,scenario+".jsonl"));
            bridge.Publish();
            PlanningFixture.Seed(runtime,"reach the targets then harvest");
            runtime.SendMessage("reach the targets then harvest");await runtime.Resume();runtime.Start();
            if(runtime.PlanningPreview["ready"]?.GetValue<bool>()!=true
                ||runtime.PlanningPreview.ContainsKey("dayPlan")||runtime.PlanningPreview.ContainsKey("dayCurrent")
                ||ToolCatalog.RequiredPlanningDefinitions().Count!=1)
                throw new Exception("FAIL: a valid season plan must allow execution without a daily plan");
            var until=Environment.TickCount64+8500;
            if(scenario=="direct"){
                while(handler.Requests<2&&Environment.TickCount64<until)await Task.Delay(10);
                if(handler.Requests<2||handler.Continuations!=0||bridge.Commands.Count!=1)
                    throw new Exception("FAIL: a direct action reaches its next decision without preplanning");
            }else{
                while(handler.Continuations==0&&Environment.TickCount64<until)await Task.Delay(10);
                if(handler.Continuations!=1||!handler.ContinuationWhileRunning)throw new Exception("FAIL: preplanning must overlap a long-running batch");
                if(scenario=="pause")await runtime.Pause();
                if(scenario=="eight")
                    while(!runtime.Decisions.Read().Any(e=>e.Kind=="continuation_planned")&&Environment.TickCount64<until)await Task.Delay(10);
                bridge.ReleaseFirstStep();
                while(!runtime.Decisions.Read().Any(e=>e.Kind=="plan_finished")&&Environment.TickCount64<until)await Task.Delay(10);
            }
            if(scenario=="eight"){
                while(!handler.SawContinuation&&Environment.TickCount64<until)await Task.Delay(10);
                if(!handler.SawContinuation||bridge.Commands.Count!=8)throw new Exception("FAIL: verified next decision must receive continuation without executing speculative actions");
            }else if(scenario is "failure" or "pause"){
                await Task.Delay(300);
                if(runtime.Decisions.Read().Any(e=>e.Kind=="continuation_planned")||bridge.Commands.Count!=(scenario=="pause"?1:2))throw new Exception("FAIL: failed or paused predecessors must discard speculative responses");
            }
            await runtime.Pause();Console.WriteLine("PASS: continuous planning "+scenario);
        }
    }

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
        var combatPlan=new JsonObject{["planId"]="combat-plan",["steps"]=new JsonArray(new JsonObject{["skill"]="fight_enemies",["arguments"]=new JsonObject{["maxKills"]=2,["maxGameMinutes"]=30,["weaponName"]="Rusty Sword"}})};
        Check(ShortPlan.Parse(combatPlan).EncounterPolicy=="interrupt","short plans default encounter policy to interrupt and combat parameters validate");
        var minePlan=new JsonObject{["planId"]="mine-plan",["steps"]=new JsonArray(new JsonObject{["skill"]="mine_travel",["arguments"]=new JsonObject{["targetFloor"]=5}})};
        try{PermissionPolicy.Authorize("mine_travel",minePlan["steps"]![0]!["arguments"]!.AsObject(),Array.Empty<ChestBinding>(),new(){WorldReady=true});throw new Exception("Disabled mine travel was authorized");}
        catch(InvalidOperationException){Check(true,"mine travel remains disabled at the permission boundary");}
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
            bridge.Publish();PlanningFixture.Seed(runtime,"autonomous greedy opening; no fishing");runtime.SendMessage("autonomous greedy opening; no fishing");await runtime.Resume();runtime.Start();
            var until=Environment.TickCount64+7000;
            if(scenario=="pause"){
                while(bridge.Commands.Count<1&&Environment.TickCount64<until)await Task.Delay(10);
                await runtime.Pause();await Task.Delay(200);
                Check(bridge.Commands.Count==1,"pause cancels remaining plan steps without dispatching step two");continue;
            }
            while(!runtime.Decisions.Read().Any(e=>e.Kind=="plan_finished")&&Environment.TickCount64<until)await Task.Delay(10);
            Check(runtime.Decisions.Read().Any(e=>e.Kind=="plan_finished"),"plan reaches a recorded terminal result: "+scenario);
            if(scenario=="eight")Check(JsonNode.Parse(runtime.Memory.Get(runtime.Scope,"agent_working_state")!)!["next"]!.AsArray().Count==0,"terminal batches retire their working-state pending steps");
            if(scenario is "eight" or "low_stamina_late" or "save_then_execute" or "invalid_json"){
                Check(bridge.Commands.Count==8&&handler.BetweenStepRequests==0,"eight verified steps execute with one model selection and no between-step requests");
                await Task.Delay(700);
                Check(handler.Requests>=2,"plan completion wakes the next decision without twenty-second delay");
                Check(handler.Continuations==0&&!handler.SawContinuation,"a short batch reaches its next decision without speculative planning");
                Check(bridge.Commands.Count==8,"duplicate plan id is not executed a second time");
                if(scenario=="save_then_execute")Check(runtime.Plans.Single().Revision>=2,"saving a public plan allows a group tool in the same model response");
                if(scenario=="invalid_json")Check(handler.Requests>=2&&runtime.Decisions.Read().Any(e=>e.Kind=="tool_rejected")&&!runtime.Decisions.Read().Any(e=>e.Kind=="model_error"),"malformed arguments are returned to the model for correction without a global one-minute failure cooldown");
            }else {
                Check(bridge.Commands.Count==2,"failed step stops all remaining steps");
                await Task.Delay(250);
                Check(!runtime.Decisions.Read().Any(e=>e.Kind=="continuation_planned"),"failure discards a late speculative response without recording success or dispatching its next steps");
            }
            if(scenario=="low_stamina_late"){
                Check(!runtime.Decisions.Read().Any(e=>e.Kind=="safety_trigger"||e.Kind=="task_dispatch"&&e.Data.GetProperty("source").GetString()!="model"),"negative stamina and 25:30 never trigger local return-home or sleep takeover");
            }
            await runtime.Pause();
        }
        {
            var bridge=new PlanBridge("modal_retry");
            var handler=new ModalRetryModel(bridge);
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"plan-modal-retry.db")),settings,new HttpClient(handler),Path.Combine(folder,"plan-modal-retry.jsonl"));
            bridge.Publish();PlanningFixture.Seed(runtime,"dismiss the shop dialogue");runtime.SendMessage("dismiss the shop dialogue");await runtime.Resume();runtime.Start();
            var until=Environment.TickCount64+5000;
            while(bridge.Commands.Count<2&&Environment.TickCount64<until)await Task.Delay(10);
            Check(bridge.Commands.Count==2&&!runtime.Decisions.Read().Any(e=>e.Kind=="tool_result"&&e.Data.GetProperty("result").GetProperty("status").GetString()=="duplicate_not_executed"),"same plan id can be retried when a new modal appears after the prior plan completed");
        }
        {
            var bridge=new PlanBridge("idle");var handler=new IdleModel();
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"observation-does-not-trigger.db")),settings,new HttpClient(handler),Path.Combine(folder,"observation-does-not-trigger.jsonl"));
            bridge.Observation!.WorldView="""{"inventoryState":{"freeSlots":0}}""";
            bridge.Publish();PlanningFixture.Seed(runtime,"当前不要使用箱子");runtime.SendMessage("当前不要使用箱子");await runtime.Resume();runtime.Start();
            var toolsDeadline=Environment.TickCount64+4000;while(handler.LastRequest is null&&Environment.TickCount64<toolsDeadline)await Task.Delay(10);
            var storage=handler.LastRequest?["tools"]?.AsArray().FirstOrDefault(t=>t?["function"]?["name"]?.ToString()=="group_storage_economy");
            var variants=storage?["function"]?["parameters"]?["properties"]?["steps"]?["items"]?["oneOf"] as JsonArray??new();
            var exposed=handler.LastRequest?["tools"]?.AsArray()??new();
            Check(!exposed.Any(t=>t?["function"]?["name"]?.ToString() is "deposit_items" or "withdraw_items")&&
                !variants.Any(v=>v?["properties"]?["skill"]?["enum"]?[0]?.ToString() is "deposit_items" or "withdraw_items"),
                "unbound storage tools stay hidden even when the prompt mentions a box and the backpack is full");
            using var pulseStop=new CancellationTokenSource();
            var pulses=Task.Run(async()=>{while(!pulseStop.IsCancellationRequested){bridge.PublishNextObservation();await Task.Delay(40,pulseStop.Token);}},pulseStop.Token);
            var deadline=Environment.TickCount64+4000;while(handler.Requests<3&&Environment.TickCount64<deadline)await Task.Delay(10);
            await Task.Delay(1300);
            Check(handler.Requests==3,"repeated property observations do not schedule new model turns after an idle decision");
            bridge.PublishResult();deadline=Environment.TickCount64+4000;while(handler.Requests<6&&Environment.TickCount64<deadline)await Task.Delay(10);
            Check(handler.Requests>=6,"a terminal tool result wakes a fresh model decision with the latest observation");
            pulseStop.Cancel();try{await pulses;}catch(OperationCanceledException){}
            await runtime.Pause();
        }
        {
            var bridge=new PlanBridge("modal_menu");var handler=new ModalWaitModel();
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"modal-wait.db")),settings,new HttpClient(handler),Path.Combine(folder,"modal-wait.jsonl"));
            bridge.Publish();PlanningFixture.Seed(runtime,"处理弹窗");await runtime.Resume();runtime.Start();
            var deadline=Environment.TickCount64+5000;while(!bridge.Commands.Contains("respond_dialogue")&&Environment.TickCount64<deadline)await Task.Delay(10);
            Check(bridge.Commands.Contains("respond_dialogue")&&runtime.Decisions.Read().Any(e=>e.Kind=="tool_rejected"&&e.Data.GetProperty("reason").GetString()?.StartsWith("modal_menu_blocks_wait")==true),"waiting is rejected while a blocking menu is open and the model resolves the dialogue instead");
            await runtime.Pause();
        }
        {
            var bridge=new PlanBridge("idle");var handler=new SynchronousPlanModel();
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"sync-tool-followup.db")),settings,new HttpClient(handler),Path.Combine(folder,"sync-tool-followup.jsonl"));
            bridge.Publish();PlanningFixture.Seed(runtime,"prepare the opening plan");runtime.SendMessage("prepare the opening plan");await runtime.Resume();runtime.Start();
            var deadline=Environment.TickCount64+7000;while(handler.Requests<6&&Environment.TickCount64<deadline)await Task.Delay(10);
            await Task.Delay(700);
            Check(handler.Requests==6&&runtime.Decisions.Read().Any(e=>e.Kind=="decision_context"&&e.Data.GetProperty("trigger").GetProperty("kind").GetString()=="tool_followup"),"a synchronous tool result on the final model round queues one follow-up decision instead of leaving the plan stalled");
            await runtime.Pause();
        }
        var trialBridge=new PlanBridge("trial");trialBridge.Observation!.GameDay=5;
        {
            var bridge=new PlanBridge("no_action_retry");var handler=new NoActionModel();
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"no-action-retry.db")),settings,new HttpClient(handler),Path.Combine(folder,"no-action-retry.jsonl"));
            bridge.Publish();PlanningFixture.Seed(runtime,"等待下一项工作");await runtime.Resume();runtime.Start();var deadline=Environment.TickCount64+10000;
            while(handler.Requests<4&&Environment.TickCount64<deadline)await Task.Delay(20);
            Check(handler.Requests>=4&&runtime.Decisions.Read().Any(e=>e.Kind=="no_action_retry_scheduled"),"autonomous text-only replies schedule a bounded retry without relying on property-change observations");
            await runtime.Pause();
        }
        {
            var bridge=new PlanBridge("closed_shop");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"closed-shop-day.db")),new ModelSettings(),new HttpClient(),Path.Combine(folder,"closed-shop-day.jsonl"));
            bridge.Publish();bridge.PublishShopClosure();bridge.Observation!.Tile.X++;bridge.PublishNextObservation();
            Check(runtime.AwarenessSnapshot()["actionFeedback"]!["blockers"]!.AsArray().Any(b=>b?["reason"]?.ToString()=="shop_closed_for_day"),"a closed-for-day shop remains blocked after movement or dialogue acknowledgement");
            bridge.Observation.GameDay++;bridge.PublishNextObservation();
            Check(runtime.AwarenessSnapshot()["actionFeedback"]!["blockers"]!.AsArray().Count==0,"a closed-for-day shop can be reconsidered on the next native day");
        }
        foreach(var askFirst in new[]{true,false}){
            var bridge=new PlanBridge("ordered_actions");var handler=new OrderedActionModel(askFirst);
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"ordered-actions-"+askFirst+".db")),settings,new HttpClient(handler),Path.Combine(folder,"ordered-actions-"+askFirst+".jsonl"));
            bridge.Publish();PlanningFixture.Seed(runtime,"按序处理调用");await runtime.Resume();runtime.Start();
            var deadline=Environment.TickCount64+5000;
            while((askFirst?runtime.PendingQuestion=="":bridge.Commands.IsEmpty)&&Environment.TickCount64<deadline)await Task.Delay(10);
            await Task.Delay(400);
            Check(askFirst?runtime.PendingQuestion!=""&&bridge.Commands.IsEmpty:runtime.PendingQuestion==""&&bridge.Commands.Count==1,
                askFirst?"asking a question prevents later gameplay calls in the same model response":"dispatching a gameplay action defers later questions until its actual result");
            await runtime.Pause();
        }
        {
            var bridge=new PlanBridge("new_day");var handler=new IdleModel(completeMaintenance:true);
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="local-test"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"day-maintenance-followup.db")),settings,new HttpClient(handler),Path.Combine(folder,"day-maintenance-followup.jsonl"));
            bridge.Observation!.WorldView="""{"decisionFacts":{"calendar":{"season":"spring"}}}""";
            bridge.Publish();
            await runtime.UpdateGoal(new(){Objective="土豆种植"});
            runtime.Memory.Set(runtime.Scope,"season_plan",JsonSerializer.Serialize(new{year=1,season="spring",goal="土豆种植",constraints=new[]{"仅使用实际资金"},successCriteria=new[]{"核验实际收获"},progress=Array.Empty<string>(),createdAt="test",goalVersion=1,milestones=new[]{"春季种植"},resourceStrategy=new[]{"保留浇水体力"}}));
            await runtime.Resume();runtime.Start();
            var deadline=Environment.TickCount64+5000;
            while(handler.Requests<3&&Environment.TickCount64<deadline)await Task.Delay(10);
            var working=JsonNode.Parse(runtime.Memory.Get(runtime.Scope,"agent_working_state")!)!.AsObject();working["currentObjective"]="finish spring 1";working["next"]=new JsonArray("old pending step");runtime.Memory.Set(runtime.Scope,"agent_working_state",working.ToJsonString());
            for(var i=0;i<15;i++)bridge.PublishResult("day-result-"+i);
            bridge.Observation!.GameDay=1;bridge.PublishNextObservation();
            deadline=Environment.TickCount64+5000;
            while(!runtime.Decisions.Read().Any(e=>e.Kind=="model_prompt_capture"&&e.Data.GetProperty("gameDay").GetInt32()==1&&e.Data.GetProperty("kind").GetString()=="daily_action")&&Environment.TickCount64<deadline)await Task.Delay(10);
            Check(runtime.Decisions.Read().Any(e=>e.Kind=="season_progress_updated")&&runtime.Decisions.Read().Any(e=>e.Kind=="model_prompt_capture"&&e.Data.GetProperty("gameDay").GetInt32()==1&&e.Data.GetProperty("kind").GetString()=="daily_action"),"saving the verified previous day's progress schedules the next gameplay decision");
            var maintenance=JsonNode.Parse(runtime.Memory.Get(runtime.Scope,"season_maintenance")!)!;
            Check(maintenance["successfulResults"]!.AsArray().Count==15,"daily maintenance retains all successful receipts beyond the twelve-item recent window");
            var freshWorking=JsonNode.Parse(runtime.Memory.Get(runtime.Scope,"agent_working_state")!)!;
            Check(freshWorking["currentObjective"]?.ToString()=="finish spring 1"&&freshWorking["next"]!.AsArray().Count==0,"new native days preserve the current objective and retire the previous day's pending steps");
            await runtime.Pause();
        }
        await using(var trial=new CompanionRuntime(trialBridge,new MemoryStore(Path.Combine(folder,"ten-day-boundary.db")),new ModelSettings(),new HttpClient(),Path.Combine(folder,"ten-day-boundary.jsonl"))){
            trial.PlanningEnabled=false;trial.AutonomousTrialEndDay=10;trialBridge.Observation.ControlMode="solo_test";
            trialBridge.Publish();PlanningFixture.Seed(trial,"完成十天测试");await trial.Resume();trial.Start();await Task.Delay(300);
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
        public int Continuations;public bool ContinuationWhileRunning;public bool SawContinuation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if(body["tools"]?.AsArray().Any(t=>t?["function"]?["name"]?.ToString()=="plan_continuation")==true){
                var deadline=Environment.TickCount64+1000;
                while(bridge.Commands.Count==0&&Environment.TickCount64<deadline)await Task.Delay(1,ct);
                Continuations++;ContinuationWhileRunning=bridge.Commands.Count>0&&bridge.Observation!.ActiveTaskId!="";
                if(bridge.Scenario is "failure" or "pause")await Task.Delay(200,ct);
                var proposalCall=new{id="continuation",type="function",function=new{name="plan_continuation",arguments=JsonSerializer.Serialize(new{nextSteps=new[]{"harvest mature crops"},assumptions=new[]{"previous batch completed"},checksBeforeAction=new[]{"verify actual receipts and mature crops"},onFailure="replan from current observation"})}};
                var proposalResponse=new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=new[]{proposalCall}}}}};
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(proposalResponse))};
            }
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>());
            SawContinuation|=input?["continuation"]?["status"]?.ToString()=="hypothesis_requires_verification";
            if(bridge.Commands.Count is >0 and <8&&bridge.Scenario is "eight" or "save_then_execute")BetweenStepRequests++;
            Requests++;
            var calls=new List<object>();
            if(bridge.Scenario=="direct"){
                calls.Add(Requests==1
                    ?new{id="direct",type="function",function=new{name="go_to",arguments=args["steps"]![0]!["arguments"]!.ToJsonString()}}
                    :new{id="wait",type="function",function=new{name="wait_for_change",arguments="{\"seconds\":120,\"reason\":\"end test\"}"}});
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=calls}}}}))};
            }
            if(bridge.Scenario=="save_then_execute")calls.Add(new{id="save",type="function",function=new{name="save_plan",arguments=JsonSerializer.Serialize(new{planId="test-plan",goal="reach declared targets",basis="observed state"})}});
            var skill=args["steps"]![0]!["skill"]!.GetValue<string>();
            calls.Add(new{id="plan",type="function",function=new{name="group_"+BusinessCapabilities.GroupFor(skill),arguments=bridge.Scenario=="invalid_json"&&Requests==1?"{\"planId\": 未引用}":args.ToJsonString()}});
            var response=new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=calls}}}};
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response))};
        }
    }
    private sealed class IdleModel(bool completeMaintenance=false):HttpMessageHandler
    {
        private int _requests;
        public int Requests=>Volatile.Read(ref _requests);
        public JsonObject? LastRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            LastRequest=JsonNode.Parse(request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult())!.AsObject();
            Interlocked.Increment(ref _requests);
            var calls=completeMaintenance&&LastRequest["tools"]?.AsArray().Any(t=>t?["function"]?["name"]?.ToString()=="update_season_progress")==true
                ?new object[]{new{id="season-progress",type="function",function=new{name="update_season_progress",arguments="""{"progress":["已核对十五条浇水回执"]}"""}}}
                :Array.Empty<object>();
            var response=new{choices=new[]{new{message=new{role="assistant",content="等待明确事件。",tool_calls=calls}}}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response))});
        }
    }
    private sealed class SynchronousPlanModel:HttpMessageHandler
    {
        private int _requests;
        public int Requests=>Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            var number=Interlocked.Increment(ref _requests);
            var calls=number<=3?new[]{new{id="save",type="function",function=new{name="save_plan",arguments=JsonSerializer.Serialize(new{planId="sync-plan",goal="continue after local result",basis="current observed state"})}}}:null;
            var response=new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=calls}}}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response))});
        }
    }
    private sealed class ModalWaitModel:HttpMessageHandler
    {
        private int _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var number=Interlocked.Increment(ref _requests);
            object call=number==1
                ?new{id="wait",type="function",function=new{name="wait_for_change",arguments=JsonSerializer.Serialize(new{seconds=120,reason="wait for shop opening"})}}
                :new{id="dialogue",type="function",function=new{name="group_social_progress",arguments=JsonSerializer.Serialize(new{planId="close_dialogue",steps=new[]{new{skill="respond_dialogue",arguments=new{responseName=""}}}})}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=new[]{call}}}}}))});
        }
    }
    private sealed class NoActionModel:HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            var count=Interlocked.Increment(ref Requests);
            var calls=count<=3?Array.Empty<object>():new object[]{new{id="wait",type="function",function=new{name="wait_for_change",arguments="{\"seconds\":120,\"reason\":\"wait after retry\"}"}}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="准备继续工作",tool_calls=calls}}}}))});
        }
    }
    private sealed class OrderedActionModel(bool askFirst):HttpMessageHandler
    {
        private int _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            var ask=new{id="ask",type="function",function=new{name="ask_user",arguments="{\"question\":\"选择下一步\"}"}};
            var sleep=new{id="sleep",type="function",function=new{name="sleep",arguments="{}"}};
            var wait=new{id="wait",type="function",function=new{name="wait_for_change",arguments="{\"seconds\":120,\"reason\":\"wait after action\"}"}};
            var calls=Interlocked.Increment(ref _requests)==1?(askFirst?new[]{ask,sleep}:new[]{sleep,ask}):new[]{wait};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=calls}}}}))});
        }
    }
    private sealed class ModalRetryModel(PlanBridge bridge):HttpMessageHandler
    {
        private int _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var number=Interlocked.Increment(ref _requests);
            if(number==2){bridge.Observation!.Menu="DialogueBox";bridge.Observation.Revision++;}
            var call=new{id="dialogue",type="function",function=new{name="group_social_progress",arguments=JsonSerializer.Serialize(new{planId="close_dialogue",steps=new[]{new{skill="respond_dialogue",arguments=new{responseName=""}}}})}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=new[]{call}}}}}))});
        }
    }
    private sealed class PlanBridge(string scenario,bool holdPlan=false):IGameBridge
    {
        public string Scenario=>scenario;
        public GameObservation? Observation{get;}=new(){SessionId="plan-session",SaveId="plan-save",PlayerId=8,Revision=1,WorldReady=true,Location="Farm",Stamina=270,Health=100,TimeOfDay=900,WorldView="{}"};
        public bool Connected=>true;public bool Fresh=>true;
        public event Action<GameObservation>? Observed;
        public event Action<TaskResult>? Result;
        private readonly TaskCompletionSource _firstStep=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseFirstStep()=>_firstStep.TrySetResult();
        public ConcurrentQueue<string> Commands{get;}=new();
        public void Publish(){if(scenario=="low_stamina_late"){Observation!.Stamina=-5;Observation.TimeOfDay=2530;}if(scenario is "modal_menu" or "modal_retry")Observation!.Menu="DialogueBox";Observed?.Invoke(Observation!);}
        public void PublishNextObservation(){Observation!.Revision++;Observed?.Invoke(Observation);}
        public void PublishResult(string taskId="event-result"){var revision=Observation!.Revision;Result?.Invoke(new(){TaskId=taskId,Skill="water_area",Status="completed",Reason="native_test_result",Completed=1,ObservationRevision=revision});}
        public void PublishShopClosure()=>Result?.Invoke(new(){TaskId="closed-shop",Skill="purchase_items",Status="failed",Reason="shop_closed_for_day",Details="{\"requestedTarget\":{\"location\":\"Town\",\"shopId\":\"SeedShop\"},\"businessDetails\":{\"shopDialogue\":\"周三打烊\"}}"});
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object args,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt"){
            taskId??=Guid.NewGuid().ToString("N");Commands.Enqueue(skill);var number=Commands.Count;
            Observation!.ActiveTaskId=taskId;
            if(scenario=="modal_retry")Observation.Menu="";
            var id=taskId;
            _=Task.Run(async()=>{
                if(holdPlan&&number==1)await _firstStep.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(scenario=="pause"?800:35);
                var revision=Observation.Revision;Observation.Revision++;Observation.ActiveTaskId="";
                Result?.Invoke(new(){TaskId=id,Skill=skill,Status=scenario=="failure"&&number==2?"failed":"completed",Reason="native_test_result",Completed=1,ObservationRevision=revision});
            });
            return Task.FromResult(new TaskResult{TaskId=id,Skill=skill,Status="accepted"});
        }
    }
}

