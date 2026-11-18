using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class DecisionQualityChecks
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    private static object? Invoke(CompanionRuntime runtime,string name,params object[] args)
    {
        try{return typeof(CompanionRuntime).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(runtime,args);}
        catch(TargetInvocationException ex)when(ex.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
    }
    private static JsonObject Work(CompanionRuntime runtime)=>JsonNode.Parse(runtime.Memory.Get(runtime.Scope,"agent_working_state")!)!.AsObject();
    private static JsonObject Water(bool reverse=false)=>reverse?new(){["height"]=1,["width"]=1,["y"]=2,["x"]=1,["location"]="Farm"}:new(){["location"]="Farm",["x"]=1,["y"]=2,["width"]=1,["height"]=1};
    internal static async Task RunToolScope(string folder)
    {
        var bridge=new QualityBridge();
        var state=bridge.Observation!;
        state.Capabilities=BusinessCapabilities.All.Select(c=>new CapabilityStatus{
            Name=c.Name,State=c.Name is "modify_building" or "complete_public_project"?"unavailable":"available_unverified"
        }).ToArray();
        var world=JsonNode.Parse(state.WorldView)!;
        world["decisionFacts"]=JsonNode.Parse("""{"calendar":{"season":"spring"},"starterSeeds":{"giftPresentHere":true}}""");
        state.WorldView=world.ToJsonString();
        var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="tool-scope-fixture"};settings.SetKey("not-real");
        var handler=new ScopeModel();
        await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"memory.db")),settings,new HttpClient(handler),Path.Combine(folder,"events.jsonl"));
        bridge.Publish();
        await runtime.SetMode("plan_only");
        var goal=await runtime.UpdateGoal(new(){Objective="照料春季作物",DisabledSkills=new[]{"plant_trees"}});
        runtime.Memory.Set(runtime.Scope,"season_plan",JsonSerializer.Serialize(new{
            year=1,season="spring",goalVersion=goal["version"],goal="照料春季作物",constraints=new[]{"不破坏作物"},
            successCriteria=new[]{"完成浇水"},milestones=new[]{"春季照常种植"},resourceStrategy=new[]{"预留体力"}
        }));
        var conversation=new JsonObject{["newMessages"]=new JsonArray()};
        JsonArray Tools(bool expanded=false)=>(JsonArray)Invoke(runtime,"DecisionTools",state,conversation,expanded)!;
        static string[] Names(JsonArray tools)=>tools.Select(t=>t!["function"]!["name"]!.ToString()).ToArray();
        static HashSet<string> GroupSkills(JsonArray tools)=>tools
            .Where(t=>t!["function"]!["name"]!.ToString().StartsWith("group_",StringComparison.Ordinal))
            .SelectMany(t=>t!["function"]!["parameters"]!["properties"]!["steps"]!["items"]!["oneOf"]!.AsArray())
            .Select(v=>v!["properties"]!["skill"]!["enum"]![0]!.ToString()).ToHashSet();
        int Size(JsonArray tools)=>tools.ToJsonString(DecisionContextCompression.ReadableJson).Length;
        var compact=Tools();var full=Tools(true);
        var compactSkills=GroupSkills(compact);var fullSkills=GroupSkills(full);
        Check(compactSkills.IsProperSubsetOf(fullSkills)&&compactSkills.IsSupersetOf(new[]{"water_area","claim_starter_gift","purchase_items"})&&
            !compactSkills.Contains("order_building")&&fullSkills.Contains("order_building"),"routine scope keeps default and observed skills and leaves specialized skills for expansion");
        Check(Size(compact)<Size(full),$"routine tool schema uses {Size(compact)} characters versus {Size(full)} expanded");
        Check(!fullSkills.Overlaps(new[]{"mine_travel","plant_trees","modify_building","deposit_items","withdraw_items"})&&
            !Names(full).Contains("deposit_items"),"expanded schema still enforces capability, player, and bound-chest limits");
        conversation["newMessages"]!.AsArray().Add(new JsonObject{["text"]="用 order_building 订购建筑"});
        Check(GroupSkills(Tools()).Contains("order_building"),"a player message naming a specialized skill exposes its schema immediately");
        conversation["newMessages"]!.AsArray().Clear();
        state.Menu="DialogueBox";Check(GroupSkills(Tools(true)).SetEquals(new[]{"respond_dialogue"}),"expanded dialogue scope exposes only the response action");
        state.Menu="NamingMenu";Check(Names(Tools(true)).ToHashSet().SetEquals(new[]{"observe","ask_user","list_capabilities"}),"expanded naming scope still requires player input");
        state.Menu="";

        for(var i=0;i<4;i++)await (Task)Invoke(runtime,"Think","fixture",runtime.Scope,state.SessionId,false)!;
        var buildingRequests=handler.Requests.Select(body=>GroupSkills(body["tools"]!.AsArray()).Contains("order_building")).ToArray();
        Check(buildingRequests.SequenceEqual(new[]{false,true,false,false,false,true,false}),
            "expansion reaches the next model request within a decision or across the round limit, then returns to compact scope");
        Check(runtime.Plans.Any(p=>p.Batch?[0]?["skill"]?.ToString()=="order_building")&&bridge.Dispatches==0,
            "an expanded skill passes normal grouped validation and saves its plan in plan-only mode");
        foreach(var body in handler.Requests){
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            var advertised=input["decisionScope"]!["allowedSkillsThisTurn"]!.AsArray().Select(v=>v!.ToString()).ToHashSet();
            var schema=body["tools"]!.AsArray();
            var direct=Names(schema).Where(ToolCatalog.Actions.Contains);
            Check(advertised.SetEquals(GroupSkills(schema).Union(direct)),"decision scope matches the actual compact or expanded request");
        }
    }
    private sealed class ScopeModel:HttpMessageHandler
    {
        public List<JsonNode> Requests {get;}=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!);
            var number=Requests.Count;
            var (name,arguments)=number switch{
                1 or 5=>("list_capabilities","{}"),
                2=>("group_construction_space","""{"planId":"build","steps":[{"skill":"order_building","arguments":{"buildingType":"Silo","x":2,"y":2,"maxCost":100}}]}"""),
                3 or 4=>("","{}"),
                _=>("save_plan","""{"planId":"routine","goal":"继续照料作物","basis":"当前观察"}""")
            };
            var calls=name==""?Array.Empty<object>():new object[]{new{id="scope-"+number,type="function",function=new{name,arguments}}};
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content="",tool_calls=calls}}}}))};
        }
    }
    internal static async Task Run(string folder)
    {
        var output=Path.Combine(folder,"decision-quality");Directory.CreateDirectory(output);
        var bridge=new QualityBridge();var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="offline-audit-fixture"};settings.SetKey("not-real");
        await using(var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(output,"memory.db")),settings,new HttpClient(),Path.Combine(output,"events.jsonl"))){
            bridge.Publish();var state=bridge.Observation!;
            runtime.AddMemory("routine","先检查作物再补水");runtime.AddMemory("agreement","不要出售稀有物");
            for(var i=0;i<13;i++)runtime.AddMemory("project","项目"+i);
            runtime.Memory.Set(runtime.Scope,"agent_working_state",new JsonObject{
                ["session"]=state.SessionId,["currentObjective"]="持续照料春季作物",["objectiveBasis"]="玩家长期目标",["activePlanId"]="old",
                ["next"]=new JsonArray("旧日瞬时步骤"),["confirmedMilestones"]=new JsonArray(new JsonObject{["skill"]="water_area",["gameDay"]=state.GameDay})}.ToJsonString());
            var conversation=new JsonObject{["newMessages"]=new JsonArray()};
            JsonArray Tools()=> (JsonArray)Invoke(runtime,"DecisionTools",state,conversation,false)!;
            var tools=Tools();
            JsonObject Input()=> (JsonObject)Invoke(runtime,"BuildDecisionInput",state,runtime.AwarenessSnapshot(),conversation,tools,"fixture")!;
            var input=Input();var kinds=input["relevantMemories"]!.AsArray().Select(m=>m!["kind"]!.ToString()).ToHashSet();
            Check(kinds.SetEquals(new[]{"project","agreement","routine"})&&input["activeProjects"]!.AsArray().Count==13,"live input includes projects, agreements and routines without project starvation");
            Check(input["candidateActions"]!.AsArray().Count==3&&input["candidateActions"]![0]!["skill"]!.ToString()=="water_area","live candidates contain allowed observed farming/forage actions in priority order");
            var farming=input["world"]!["farmingPlan"]!;
            Check(farming["waterableCount"]!.GetValue<int>()==1&&farming["harvestableCount"]!.GetValue<int>()==1&&farming["emptyPlotCount"]!.GetValue<int>()==2&&farming["availableSeedCount"]!.GetValue<int>()==3&&farming["seedCoverageRatio"]!.GetValue<double>()==1.5,"farming totals use full observation before world-array truncation");
            var originalWorld=state.WorldView;var large=JsonNode.Parse(originalWorld)!;
            for(var i=0;i<80;i++)large["soil"]!.AsArray().Add(new JsonObject{["x"]=i+10,["y"]=5,["crop"]=null});state.WorldView=large.ToJsonString();
            var full=Input();Check(full["world"]!["soil"]!["items"]!.AsArray().Count==64&&full["world"]!["farmingPlan"]!["emptyPlotCount"]!.GetValue<int>()==82,"farming summary counts omitted plots beyond the 64-tile display limit");
            var unknown=JsonNode.Parse(originalWorld)!;foreach(var item in unknown["inventory"]!.AsArray())item!.AsObject().Remove("isSeed");state.WorldView=unknown.ToJsonString();
            Check(Input()["world"]!["farmingPlan"]!["availableSeedCount"]==null,"old bridges without seed metadata report unknown instead of zero");state.WorldView=originalWorld;
            state.GameDay++;bridge.Publish();
            Check(Work(runtime)["currentObjective"]!.ToString()=="持续照料春季作物"&&Work(runtime)["next"]!.AsArray().Count==0,"day transition preserves objective and retires executable steps");
            state.SessionId="quality-reloaded";bridge.Publish();
            Check(Work(runtime)["currentObjective"]!.ToString()=="持续照料春季作物"&&Work(runtime)["confirmedMilestones"]!.AsArray().Count==0&&Work(runtime)["historicalMilestones"]![0]!["knowledge"]!.ToString()=="historical_requires_recheck","reload preserves objective and archives milestones without claiming current verification");
            await runtime.UpdateGoal(new(){Objective="补充措辞",ReplaceObjective=false});
            Check(Work(runtime)["currentObjective"]!.ToString()=="持续照料春季作物","nonreplacement goal edit preserves current objective");
            await runtime.UpdateGoal(new(){Objective="新发展目标"});
            Check(Work(runtime)["currentObjective"]!.ToString()=="","replacement goal explicitly retires earlier objective");
            Invoke(runtime,"RecordActionFeedback",new{status="failed",tool="purchase_items",arguments=new{location="SeedShop"},reason="shop_closed_for_day"});
            Check(Input()["world"]!["risks"]!["activeConstraints"]![0]!["kind"]!.ToString()=="shop_closed_for_day","native blockers are projected as structured world constraints");
            Invoke(runtime,"StartWait",new JsonObject{["seconds"]=120,["reason"]="等到九点",["expectedChange"]="time_of_day",["targetTime"]=900},state);
            state.TimeOfDay=850;state.Tile=new(){X=10,Y=10};state.WorldView=originalWorld.Replace("\"count\":3","\"count\":4");
            Check(!(bool)Invoke(runtime,"WaitSatisfied",state)!,"time wait ignores position, inventory and intermediate clock changes");
            state.TimeOfDay=900;Check((bool)Invoke(runtime,"WaitSatisfied",state)!,"time wait wakes at its target HHMM");
            try{Invoke(runtime,"StartWait",new JsonObject{["seconds"]=120,["expectedChange"]="time_of_day",["targetTime"]=960},state);throw new Exception("invalid HHMM accepted");}
            catch(InvalidOperationException){Check(true,"invalid HHMM wait target is rejected");}
            Invoke(runtime,"StartWait",new JsonObject{["seconds"]=120,["reason"]="等实际变化"},state);state.TimeOfDay=910;state.Tile=new(){X=11,Y=10};
            Check(!(bool)Invoke(runtime,"WaitSatisfied",state)!,"default wait ignores clock and tile jitter");
            state.Menu="DialogueBox";try{Invoke(runtime,"StartWait",new JsonObject{["seconds"]=10},state);throw new Exception("modal wait accepted");}catch(InvalidOperationException){Check(true,"menus still prohibit waiting");}state.Menu="";
            state.Location="Town";tools=Tools();input=Input();
            var skills=input["decisionScope"]!["allowedSkillsThisTurn"]!.AsArray().Select(s=>s!.ToString()).ToHashSet();
            Check(skills.Contains("water_area")&&skills.Contains("purchase_items"),"Town with empty goal still exposes farming and available commerce groups");
            runtime.Memory.Set(runtime.Scope,"player_goal",new JsonObject{["objective"]="",["disabledSkills"]=new JsonArray("water_area")}.ToJsonString());
            tools=Tools();
            Check(!Input()["candidateActions"]!.AsArray().Any(c=>c?["skill"]?.ToString()=="water_area"),"candidate projection respects disabled skills");
            state.Location="Farm";var a=Water();var b=Water();b["x"]=2;
            Invoke(runtime,"BeginAction","failed-a","water_area",a,state);Invoke(runtime,"FinishAction",new TaskResult{TaskId="failed-a",Status="failed",Reason="no_water"});
            Invoke(runtime,"BeginAction","failed-b","water_area",b,state);Invoke(runtime,"FinishAction",new TaskResult{TaskId="failed-b",Status="failed",Reason="no_water"});
            try{Invoke(runtime,"BeginAction","retry-a","water_area",Water(true),state);throw new Exception("interleaved failure lost");}
            catch(InvalidOperationException ex){Check(ex.Message.Contains("unchanged_target_previously_failed"),"failure history survives another failed target and reordered argument keys");}
            state.GameDay++;Invoke(runtime,"BeginAction","next-day-a","water_area",a,state);Invoke(runtime,"ForgetAction","next-day-a");
            Check(true,"new game day permits failed action revalidation");
        }
        foreach(var scenario in new[]{"single-single","single-group","group-single"})await GuardScenario(output,scenario,settings);
        Console.WriteLine("DECISION QUALITY ARTIFACTS: "+output);
    }
    private static async Task GuardScenario(string folder,string scenario,ModelSettings settings)
    {
        var bridge=new QualityBridge();var handler=new QualityModel(bridge,scenario);
        await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,scenario+".db")),settings,new HttpClient(handler),Path.Combine(folder,scenario,"events.jsonl"));
        bridge.Publish();
        await runtime.UpdateGoal(new(){Objective="持续照料春季作物，并存钱升级农舍",Restrictions=new[]{"不出售稀有物"}});
        PlanningFixture.Seed(runtime);
        runtime.AddMemory("project","升级农舍");runtime.AddMemory("agreement","不出售稀有物");runtime.AddMemory("routine","按实际观察照料作物");
        await runtime.Resume();runtime.Start();
        var until=Environment.TickCount64+10000;
        while(!runtime.Decisions.Read().Any(e=>e.Kind=="tool_result"&&e.Data.GetProperty("name").GetString()=="wait_for_change")&&Environment.TickCount64<until)await Task.Delay(20);
        await runtime.Pause();
        var events=runtime.Decisions.Read();
        Check(events.Any(e=>e.Kind=="tool_result"&&e.Data.GetProperty("name").GetString()=="wait_for_change"),"guard scenario finishes correction loop: "+scenario);
        Check(bridge.Dispatches==2&&events.Any(e=>e.Data.ToString().Contains("unchanged_target_previously_failed")),"shared failure guard blocks unchanged retry across "+scenario+" and permits material-state change");
        var request=handler.Requests[0];
        File.WriteAllText(Path.Combine(folder,"llm-request.fixture.json"),request.ToJsonString(new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        File.WriteAllText(Path.Combine(folder,"llm-context.fixture.json"),JsonNode.Parse(request["messages"]![1]!["content"]!.GetValue<string>())!.ToJsonString(new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        File.WriteAllText(Path.Combine(folder,"plan-execute.fixture.json"),JsonSerializer.Serialize(events,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
    }
    private sealed class QualityModel(QualityBridge bridge,string scenario):HttpMessageHandler
    {
        public List<JsonNode> Requests {get;}=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;Requests.Add(body);var n=Requests.Count;
            bridge.Observation!.TimeOfDay+=10;bridge.Observation.Tile=new(){X=n,Y=n};
            string name;JsonObject arguments;
            if(n<=3){
                var group=n==1?scenario.StartsWith("group"):n==2&&scenario.EndsWith("group");
                name=group?"group_farm_management":"water_area";
                arguments=group?new(){["planId"]="quality-"+n,["steps"]=new JsonArray(new JsonObject{["skill"]="water_area",["arguments"]=Water(true)})}:Water(n==2);
                if(n==3){bridge.ChangeInventory();}
            }else{name="wait_for_change";arguments=new(){["seconds"]=120,["reason"]="测试结束",["expectedChange"]="day_changed"};}
            var calls=new JsonArray(new JsonObject{["id"]="quality-"+n,["type"]="function",["function"]=new JsonObject{["name"]=name,["arguments"]=arguments.ToJsonString()}});
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["message"]=new JsonObject{["role"]="assistant",["content"]="",["tool_calls"]=calls}})}.ToJsonString())};
        }
    }
    private sealed class QualityBridge:IGameBridge
    {
        public GameObservation? Observation {get;}=new(){WorldReady=true,SaveId="quality-save",PlayerId=1,SessionId="quality-session",Location="Farm",Revision=1,TimeOfDay=600,
            Capabilities=new[]{new CapabilityStatus{Name="purchase_items",State="supported"}},
            WorldView="""{"money":500,"inventory":[{"id":"(O)472","count":3,"isSeed":true}],"inventoryState":{"freeSlots":10},"nearbyRefillTiles":[{"x":1,"y":1}],"soil":[{"x":1,"y":2,"crop":"24","needsWater":true},{"x":2,"y":2,"crop":"24","ready":true},{"x":3,"y":2,"crop":null},{"x":4,"y":2,"crop":null}],"forage":[{"x":5,"y":2}]}"""};
        public bool Connected=>true;public bool Fresh=>true;public int Dispatches;
        public event Action<GameObservation>? Observed;public event Action<TaskResult>? Result;
        public void Publish()=>Observed?.Invoke(Observation!);
        public void ChangeInventory(){var world=JsonNode.Parse(Observation!.WorldView)!;world["inventory"]![0]!["count"]=4;Observation.WorldView=world.ToJsonString();}
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object arguments,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt")
        {
            Dispatches++;var state=Observation!;state.Revision+=2;
            var result=new TaskResult{TaskId=taskId!,Skill=skill,Status=Dispatches==1?"failed":"completed",Reason=Dispatches==1?"water_source_unreachable":"watered",Completed=Dispatches==1?0:1,ObservationRevision=state.Revision-1};
            Result?.Invoke(result);return Task.FromResult(result);
        }
    }
}
