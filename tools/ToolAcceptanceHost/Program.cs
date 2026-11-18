using StardewCompanion.Core;

// The ordinary runtime on a separate loopback port. No game tasks are injected.
var data=Path.GetFullPath(Environment.GetEnvironmentVariable("SC_AGENT_DATA")??throw new ArgumentException("SC_AGENT_DATA required"));
var runId=Path.GetFileName(data);
if(!StardewCompanion.Protocol.ControlModePolicy.IsSoloTestRunId(runId))throw new ArgumentException("Isolated test run required");
var portText=Environment.GetEnvironmentVariable("SC_ACCEPTANCE_PORT")??"27842";
if(!int.TryParse(portText,out var port)||port is <1024 or >65535)throw new ArgumentException("Invalid test port");
await using var bridge=new GameBridge();
using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(55)};
var settings=ModelSettings.Load(Path.GetFullPath(Environment.GetEnvironmentVariable("SC_MODEL_SETTINGS")??throw new ArgumentException("Model settings required")));
if(settings.Model!="deepseek-chat")throw new ArgumentException("DeepSeek required");
await using var agent=new CompanionRuntime(bridge,new MemoryStore(Path.Combine(data,"memory.db")),settings,http,Path.Combine(data,"events.jsonl"));
agent.PlanningEnabled=true;agent.AutonomousTrialEndDay=null;
bridge.AttachAgent(agent);
await bridge.Start(Path.GetFullPath(Environment.GetEnvironmentVariable("SC_SESSION_FILE")??Path.Combine(data,"session.json")),port);
agent.Start();
var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stopped.TrySetResult();};
await stopped.Task;
