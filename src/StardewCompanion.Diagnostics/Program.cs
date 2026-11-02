using StardewCompanion.Core;

await using var bridge=new GameBridge();
CompanionRuntime? agent=null;
using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(55)};
try
{
if(args.Contains("--agent"))
{
    var data=Path.GetFullPath(Environment.GetEnvironmentVariable("SC_AGENT_DATA")??".local/agent-run");
    var settings=ModelSettings.Load(Path.GetFullPath(Environment.GetEnvironmentVariable("SC_MODEL_SETTINGS")??".local/model.credentials.json"));
    agent=new(bridge,new MemoryStore(Path.Combine(data,"memory.db")),settings,http,Path.Combine(data,"events.jsonl"));
    agent.PlanningEnabled=!args.Contains("--scripted");
    if(args.Contains("--solo-five-day"))agent.AutonomousTrialEndDay=5;
    var daysIndex=Array.IndexOf(args,"--trial-days");
    if(daysIndex>=0){
        if(daysIndex+1>=args.Length||!int.TryParse(args[daysIndex+1],out var days)||days is <1 or >112)
            throw new ArgumentException("--trial-days must be 1..112");
        agent.AutonomousTrialEndDay=days;
    }
    bridge.AttachAgent(agent);
}
await bridge.Start(Path.GetFullPath(Environment.GetEnvironmentVariable("SC_SESSION_FILE")??".local/session.json"));
agent?.Start();
var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stopped.TrySetResult();};
await stopped.Task;
}
finally { if(agent!=null)await agent.DisposeAsync(); }
