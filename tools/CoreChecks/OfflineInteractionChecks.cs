using System.Net;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

internal static class OfflineInteractionChecks
{
    internal static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);var handler=new OfflineModel();var bridge=new OfflineBridge();
        var settings=new ModelSettings{Endpoint="http://127.0.0.1:1/chat/completions",Model="offline-debug-fixture"};settings.SetKey("fixture-key-not-in-request-capture");
        await using var runtime=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(folder,"memory.db")),settings,new HttpClient(handler),Path.Combine(folder,"events.jsonl"));
        runtime.SendMessage("昨天做到哪里了？");runtime.Start();
        var deadline=Environment.TickCount64+5000;
        while(!runtime.Decisions.Read().Any(e=>e.Kind=="decision_finished")&&Environment.TickCount64<deadline)await Task.Delay(20);
        var entries=runtime.Decisions.Read();
        var capture=entries.Single(e=>e.Kind=="model_prompt_capture");var reply=entries.Single(e=>e.Kind=="model_reply");
        var captured=JsonNode.Parse(capture.Data.GetProperty("prompt").GetRawText());
        Check(JsonNode.DeepEquals(captured,handler.Request),"offline capture matches the complete actual HTTP request");
        Check(capture.Data.GetProperty("requestId").GetString()==reply.Data.GetProperty("requestId").GetString()&&capture.DecisionId==reply.DecisionId,
            "offline public reply is correlated to request and decision IDs");
        Check(captured!["tools"]==null&&bridge.Dispatches==0,"offline debugging adds no game tools or actions");
        Check(!capture.Data.GetRawText().Contains("fixture-key-not-in-request-capture")&&entries.Any(e=>e.Kind=="decision_finished"&&e.DecisionId==capture.DecisionId),
            "offline interaction closes its debug lifecycle without capturing authorization secrets");
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    private sealed class OfflineModel:HttpMessageHandler
    {
        internal JsonNode? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Request=JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            return new(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"message":{"role":"assistant","content":"上次记录是照料南侧田地；连接后再核对实际进度。"}}]}""")};
        }
    }
    private sealed class OfflineBridge:IGameBridge
    {
        public GameObservation? Observation=>null;public bool Connected=>false;public bool Fresh=>false;public int Dispatches;
        public event Action<GameObservation>? Observed{add{}remove{}}
        public event Action<TaskResult>? Result{add{}remove{}}
        public Task<string> Control(string type)=>Task.FromResult(type);
        public Task<TaskResult> Dispatch(string skill,object arguments,bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt")
        {Dispatches++;throw new InvalidOperationException("offline fixture must never dispatch");}
    }
}
