using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class SeasonToolChecks
{
    internal static async Task Run(string folder)
    {
        foreach(var scenario in new[]{"invalid_json","missing_progress","unknown_field","invalid_progress"}){
            var scenarioFolder=Path.Combine(folder,"season-"+scenario);
            Directory.CreateDirectory(scenarioFolder);
            var bridge=new SeasonBridge();var model=new SeasonModel(scenario);
            var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="season-check"};settings.SetKey("not-real");
            await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(scenarioFolder,"memory.db")),settings,new HttpClient(model),Path.Combine(scenarioFolder,"events.jsonl"));
            bridge.Publish();await runtime.Resume();bridge.Observation!.GameDay++;bridge.Publish();runtime.Start();
            var deadline=Environment.TickCount64+5000;
            while(!runtime.Decisions.Read().Any(e=>e.Kind=="decision_finished")&&Environment.TickCount64<deadline)await Task.Delay(10);
            await runtime.Pause();
            if(model.Requests.Count<2)throw new Exception("Season tool did not receive correction feedback: "+scenario);
            var correction=model.Requests[1]["messages"]!.AsArray().Last(m=>m?["role"]?.ToString()=="tool")!;
            if(JsonNode.Parse(correction["content"]!.GetValue<string>())?["status"]?.ToString()!="rejected")
                throw new Exception("Invalid season arguments were accepted: "+scenario);
            if(!runtime.Decisions.Read().Any(e=>e.Kind=="tool_rejected")||runtime.Decisions.Read().Any(e=>e.Kind=="model_error"))
                throw new Exception("Season rejection must stay inside the tool correction loop: "+scenario);
            if(runtime.Decisions.Read().Count(e=>e.Kind=="season_plan_created")!=1||runtime.Memory.Get(runtime.Scope,"season_plan")==null)
                throw new Exception("Only the corrected season plan may be persisted: "+scenario);
            if(bridge.Dispatches!=0)throw new Exception("Season maintenance dispatched a game action");
            Console.WriteLine("PASS: season tool rejects "+scenario+", accepts correction and dispatches no game action");
        }
    }

    private sealed class SeasonModel(string scenario):HttpMessageHandler
    {
        public List<JsonNode> Requests {get;}=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;Requests.Add(body);
            var tools=body["tools"]!.AsArray();
            if(!tools.Any(t=>t?["function"]?["name"]?.ToString()=="create_season_plan"))
                throw new Exception("Expected the season maintenance tool scope");
            var arguments=new JsonObject{["season"]="spring",["goal"]="照料已观察的作物",["constraints"]=new JsonArray(),
                ["successCriteria"]=new JsonArray("实际浇水回执"),["progress"]=new JsonArray(),["previousSeasonReview"]="首次创建"};
            if(Requests.Count==1){
                if(scenario=="missing_progress")arguments.Remove("progress");
                if(scenario=="unknown_field")arguments["year"]=99;
                if(scenario=="invalid_progress")arguments["progress"]="not an array";
            }
            var raw=Requests.Count==1&&scenario=="invalid_json"?"{invalid":arguments.ToJsonString();
            var calls=Requests.Count<=2?new JsonArray(new JsonObject{["id"]="season-"+Requests.Count,["type"]="function",
                ["function"]=new JsonObject{["name"]="create_season_plan",["arguments"]=raw}}):new JsonArray();
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{
                ["message"]=new JsonObject{["role"]="assistant",["content"]="",["tool_calls"]=calls}})}.ToJsonString())};
        }
    }

    private sealed class SeasonBridge:IGameBridge
    {
        public GameObservation? Observation {get;}=new(){SessionId="season-session",SaveId="season-save",PlayerId=1,WorldReady=true,Revision=1,GameDay=1,Location="Farm",WorldView="""{"decisionFacts":{"calendar":{"season":"spring"}}}"""};
        public bool Connected=>true;
        public bool Fresh=>true;
        public event Action<GameObservation>? Observed;
        public event Action<TaskResult>? Result {add{} remove{}}
        public int Dispatches;
        public void Publish()=>Observed?.Invoke(Observation!);
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object arguments,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt")
        {Dispatches++;throw new Exception("Maintenance must not dispatch game actions");}
    }
}
