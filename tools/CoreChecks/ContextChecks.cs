using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class ContextChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    private static JsonObject Source(int historicalPlans)
    {
        var plans=new JsonArray();
        for(var i=0;i<historicalPlans;i++)plans.Add(new JsonObject{
            ["Id"]="p"+i,["Goal"]="已执行批次"+i,["State"]="batch_completed",["UpdatedAt"]=$"2026-01-01T00:00:{i:000000}Z",
            ["Pending"]=new JsonArray("等待明天确认建造完成"),["Evidence"]=new JsonObject{["unusedFullHistory"]=new string('x',8000)}});
        plans.Add(new JsonObject{["Id"]="current",["Goal"]="读提示后继续开荒",["State"]="planned",["UpdatedAt"]="2027-01-01",["Pending"]=new JsonArray("明天检查洒水器覆盖"),["Assumptions"]=new JsonArray("尚未确认建造完成")});
        var detail=new{
            collectedItems=new Dictionary<string,int>{{"(O)388",17}},actualCost=new{netStaminaUsed=12},
            affectedTiles=Enumerable.Range(0,40).Select(x=>new{x,y=1}),
            businessDetails=new{stages=new[]{new{skill="daily_info",details=new{results=new[]{new{content="明天来领取升级工具。"}}}}}}
        };
        var receipt=new JsonObject{["TaskId"]="receipt-1",["Skill"]="gather_resource",["Status"]="partial",["Reason"]="target_unreachable",["Completed"]=5,["Details"]=JsonSerializer.Serialize(detail)};
        return new(){["instruction"]="继续",["playerInstructions"]=new JsonArray("完成十日开荒，不能自动出售稀有物。","完成十日开荒，不能自动出售稀有物。"),["operatingMode"]="autonomous",["capabilities"]=new JsonArray(new JsonObject{["name"]="sell_items",["state"]="available_unverified",["reason"]="needs native validation",["validated"]=false}),["workPlans"]=plans,
            ["observation"]=new JsonObject{["GameDay"]=5,["Menu"]="DialogueBox",["menuContext"]=new JsonObject{["text"]="你发现了一个晶球……",["choices"]=new JsonArray()},["world"]=new JsonObject{
                ["money"]=500,["inventory"]=new JsonArray(Enumerable.Range(0,12).Select(i=>(JsonNode)new JsonObject{["slot"]=i,["name"]="木材",["count"]=i}).ToArray()),["soil"]=new JsonArray()}},
            ["actionFeedback"]=new JsonObject{["latest"]=DecisionContextCompression.Receipt(receipt)},["recentTaskResults"]=new JsonArray(receipt.DeepClone()),["lastPlan"]=new JsonObject{["planId"]="last",["results"]=new JsonArray(new JsonObject{["receipt"]=receipt.DeepClone()})},
            ["queryFacts"]=new JsonArray(new JsonObject{["skill"]="observe",["observedDay"]=5,["result"]=new string('x',4000)}),
            ["memories"]=new JsonArray(new JsonObject{["Id"]=1,["Kind"]="agreement",["Text"]="用户约定必须原文保留"})};
    }
    private static JsonNode? Untable(JsonNode? value)
    {
        if(value is JsonObject table&&table["columns"] is JsonArray columns&&table["rows"] is JsonArray rows)
            return new JsonArray(rows.OfType<JsonArray>().Select((row,index)=>{
                var item=new JsonObject();for(var i=0;i<columns.Count;i++){
                    if((table["missing"] as JsonArray??new()).OfType<JsonArray>().Any(m=>m[0]!.GetValue<int>()==index&&m[1]!.GetValue<int>()==i))continue;
                    item[columns[i]!.GetValue<string>()]=Untable(row[i]);
                }return (JsonNode)item;
            }).ToArray());
        if(value is JsonObject obj)return new JsonObject(obj.Select(p=>KeyValuePair.Create(p.Key,Untable(p.Value))));
        if(value is JsonArray array)return new JsonArray(array.Select(Untable).ToArray());
        return value?.DeepClone();
    }
    internal static async Task Run(string root,string folder)
    {
        var journalPath=Path.Combine(folder,"resume-journal.jsonl");
        new DecisionJournal(journalPath).Write("save","old","","before_restart",new{});
        File.AppendAllText(journalPath,"{incomplete-event}\n");
        var resumedJournal=new DecisionJournal(journalPath);resumedJournal.Write("save","new","","after_restart",new{});
        Check(resumedJournal.Read().Single().Sequence==2,"resumed journal continues event IDs after a restart and ignores incomplete records");
        var source=Source(10);var small=DecisionContextCompression.Build(source);var large=DecisionContextCompression.Build(Source(1000));
        var context=small.Context;var payload=DecisionContextCompression.Serialize(context);
        Check(Math.Abs(large.Characters-small.Characters)<100,"1000 completed plans do not inflate context relative to 10 plans");
        Check(context["playerInstructions"]!.AsArray().Single()!.GetValue<string>()=="完成十日开荒，不能自动出售稀有物。","player requirements are preserved verbatim with exact duplicate removal only");
        Check(context["observation"]!["menuContext"]!["text"]!.GetValue<string>()=="你发现了一个晶球……","current popup remains verbatim in compressed context");
        Check(!payload.Contains("等待明天确认建造完成")&&payload.Contains("明天检查洒水器覆盖"),"stopped/completed plan pending is retired while the current plan pending remains visible");
        Check(context["capabilities"]![0]!["name"]!.GetValue<string>()=="sell_items"&&context["dailyPlan"]!=null&&context["planning"]==null,"capabilities and current daily progress are present without archived plan histories");
        var restarted=Source(0);restarted["workPlans"]![0]!["State"]="stopped";
        restarted["workPlans"]![0]!["Evidence"]=new JsonObject{["status"]="stopped",["reason"]="materials_missing:wood=17/50"};
        restarted["actionFeedback"]=new JsonObject{["latest"]=new JsonObject{["status"]="stopped",["reason"]="materials_missing:wood=17/50"}};restarted.Remove("lastPlan");restarted.Remove("recentTaskResults");
        Check(DecisionContextCompression.Serialize(DecisionContextCompression.Build(restarted).Context).Contains("materials_missing:wood=17/50"),"stored failure survives runtime restart without relying on the in-memory receipt queue");
        Check(context["dailyPlan"]!["done"]!.AsArray().Count==0&&payload.Contains("target_unreachable")&&payload.Contains("明天来领取升级工具。"),"duplicate receipts merge while failure reason, dialogue and verified item gains survive");
        Check(context["actionFeedback"]!["latest"]!["collectedItems"]!["(O)388"]!.GetValue<int>()==17,"actual material gains are not inferred from completed unit counts");
        Check(!payload.Contains("\\u")&&context["queryFacts"]!.AsArray().Count==0,"model receives natural Unicode without duplicated observe snapshots");
        var legacy=Source(0);legacy["memories"]!.AsArray().Add(new JsonObject{["Id"]=2,["Kind"]="place",["Text"]="农场南侧有一条已发现的小路"});
        legacy["memories"]!.AsArray().Add(new JsonObject{["Id"]=3,["Kind"]="project",["Text"]="旧版本保存的未完成用户目标"});
        Check(DecisionContextCompression.Serialize(DecisionContextCompression.Build(legacy).Context).Contains("旧版本保存的未完成用户目标")&&
            DecisionContextCompression.Serialize(DecisionContextCompression.Build(legacy).Context).Contains("农场南侧有一条已发现的小路"),"non-chest place notes and legacy project text are not mistaken for duplicate projections");
        Check(JsonNode.DeepEquals(Untable(context["observation"]),source["observation"]),"tabular observation round-trips without losing current world facts");
        var sparse=Source(0);sparse["observation"]!["world"]!["inventory"]![0]!["optional"]=null;
        sparse["observation"]!["world"]!["inventory"]![1]!["optional"]=new JsonObject{["unknown"]=true};
        Check(JsonNode.DeepEquals(Untable(DecisionContextCompression.Build(sparse).Context["observation"]),sparse["observation"]),"table encoding distinguishes missing fields, explicit null and actual unknown objects");
        var protectedSource=Source(0);protectedSource["playerInstructions"]=new JsonArray(new string('约',40000));
        var protectedResult=DecisionContextCompression.Build(protectedSource);
        Check(protectedResult.OverBudget&&protectedResult.Context["playerInstructions"]![0]!.GetValue<string>().Length==40000,"soft budget reports protected-content overflow instead of truncating user requirements");
        using(var db=new MemoryStore(Path.Combine(folder,"context-history.db"))){
            var oldId=db.Add("archive","agreement","很早的用户约定","player");
            for(var i=0;i<510;i++)db.Add("archive","routine","历史经验"+i,"model");
            Check(db.ReadHistory("archive",oldId.ToString(),0,1).Entries.Single().Text=="很早的用户约定"&&db.ReadHistory("archive","",510,1).Entries.Single().Id==oldId,
                "paginated archive can retrieve memories older than the normal 500-item working window");
            Check(db.ReadHistory("other-save",oldId.ToString(),0,1).Total==0,"archive retrieval cannot cross save/character scope");
            var project=db.CreateProject("archive","跨日建筑项目",0);
            var projectGuid=DecisionContextCompression.Text(DecisionContextCompression.Decode(JsonValue.Create(db.ReadHistory("archive",project.ToString(),0,1).Entries.Single().Text)),"id");
            Check(db.ReadHistory("archive",projectGuid,0,1).Entries.Single().Id==project,"project summary IDs resolve to original local memory documents");
        }
        await RuntimeCheck(folder);
        var log=Path.Combine(root,".local","solo5","SC10D_20261029_233711","decisions.jsonl");
        if(!File.Exists(log))return;
        var replay=new List<object>();var beforeTotal=0L;var afterTotal=0L;var cases=0;
        foreach(var line in File.ReadLines(log)){
            var entry=JsonNode.Parse(line)!;if(entry["Kind"]?.ToString()!="decision_context")continue;
            var raw=entry["Data"]!.AsObject();var packed=DecisionContextCompression.Build(raw);
            if(!JsonNode.DeepEquals(Untable(packed.Context["observation"]),Untable(raw["observation"]))){
                File.WriteAllText(Path.Combine(folder,"replay-expected.json"),raw["observation"]!.ToJsonString());
                File.WriteAllText(Path.Combine(folder,"replay-actual.json"),Untable(packed.Context["observation"])!.ToJsonString());
                throw new Exception("Replay lost current observation: "+entry["Sequence"]+" folder="+folder);
            }
            foreach(var instruction in raw["playerInstructions"]?.AsArray()??new())
                if(!packed.Context["playerInstructions"]!.AsArray().Any(i=>JsonNode.DeepEquals(i,instruction)))throw new Exception("Replay lost player instruction");
            beforeTotal+=packed.OriginalCharacters;afterTotal+=packed.Characters;cases++;
            replay.Add(new{sequence=entry["Sequence"]?.DeepClone(),day=DecisionContextCompression.Get(raw["observation"],"gameDay")?.DeepClone(),packed.OriginalCharacters,packed.Characters,packed.OverBudget});
        }
        var report=new{scope="offline_context_replay_not_billed_token_estimate",contexts=cases,beforeCharacters=beforeTotal,afterCharacters=afterTotal,
            reductionPercent=beforeTotal==0?0:Math.Round(100.0*(beforeTotal-afterTotal)/beforeTotal,2),cases=replay};
        File.WriteAllText(Path.Combine(folder,"context-compression-replay.json"),DecisionContextCompression.Serialize(report));
        Check(cases>0,"historical run replays with every current observation and player instruction preserved");
        Console.WriteLine("CONTEXT REPLAY "+DecisionContextCompression.Serialize(new{cases,beforeTotal,afterTotal,reductionPercent=100.0*(beforeTotal-afterTotal)/beforeTotal,report=Path.Combine(folder,"context-compression-replay.json")}));
    }
    private static async Task RuntimeCheck(string folder)
    {
        var bridge=new ContextBridge();var model=new ContextModel();
        var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="offline"};settings.SetKey("not-real");
        await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"context-runtime.db")),settings,new HttpClient(model),Path.Combine(folder,"context-runtime.jsonl"));
        bridge.Publish();runtime.Memory.Set(runtime.Scope,"work_plans",Source(20)["workPlans"]!.ToJsonString());
        runtime.SendMessage("开荒目标原文不能变");await runtime.Resume();runtime.Start();
        var until=Environment.TickCount64+5000;
        while(!runtime.Decisions.Read().Any(e=>e.Kind=="tool_result"&&e.Data.GetProperty("name").GetString()=="wait_for_change")&&Environment.TickCount64<until)await Task.Delay(10);
        await runtime.Pause();
        Check(model.Requests.Count==2,"context compression and historical retrieval need no separate model summarization request");
        var second=model.Requests[1];var messages=second["messages"]!.AsArray();var context=JsonNode.Parse(messages[1]!["content"]!.GetValue<string>())!;
        Check(context["world"]!["money"]!.GetValue<int>()==999,"observe refreshes the one current snapshot during tool-call follow-up");
        var observeReply=JsonNode.Parse(messages.Last()!["content"]!.GetValue<string>())!;
        Check(observeReply["observationUpdated"]!.GetValue<bool>()&&observeReply["data"]==null,"observe tool response references the updated snapshot without retransmitting it");
        var history=JsonNode.Parse(messages[messages.Count-2]!["content"]!.GetValue<string>())!;
        Check(history["plan"]!["id"]!.GetValue<string>()=="p0"&&runtime.Plans.Length==21,"registered history tool retrieves archived plan while original local storage stays complete");
        Check(bridge.Dispatched.SequenceEqual(new[]{"observe"}),"context/history handling dispatches no gameplay actions");
    }
    private sealed class ContextModel:HttpMessageHandler
    {
        public List<JsonNode> Requests{get;}=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            JsonObject Call(string id,string name,object args)=>new(){["id"]=id,["type"]="function",["function"]=new JsonObject{["name"]=name,["arguments"]=JsonSerializer.Serialize(args)}};
            var calls=Requests.Count==1?new JsonArray(Call("h","read_context_history",new{source="plans",id="p0",offset=0,limit=2}),Call("o","observe",new{}))
                :new JsonArray(Call("w","wait_for_change",new{seconds=120,reason="offline test complete"}));
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["message"]=new JsonObject{["role"]="assistant",["content"]="",["tool_calls"]=calls}})}.ToJsonString())};
        }
    }
    private sealed class ContextBridge:IGameBridge
    {
        public GameObservation? Observation{get;}=new(){WorldReady=true,SessionId="context-session",SaveId="context-save",PlayerId=2,Location="Farm",Revision=1,WorldView="{\"money\":500}"};
        public bool Connected=>true;public bool Fresh=>true;
        public event Action<GameObservation>? Observed;public event Action<TaskResult>? Result;
        public List<string> Dispatched{get;}=new();
        public void Publish()=>Observed?.Invoke(Observation!);
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object arguments,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt")
        {
            Dispatched.Add(skill);var result=new TaskResult{TaskId=taskId??"observe",Skill=skill,Status="completed",Reason="observation",ObservationRevision=2,Details="{\"money\":999}"};
            Result?.Invoke(result);return Task.FromResult(result);
        }
    }
}

