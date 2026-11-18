using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using StardewCompanion.Mod;
using StardewCompanion.Protocol;

var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(Environment.GetEnvironmentVariable("SC_GAME_PATH") ?? @"F:\SteamLibrary\steamapps\common\Stardew Valley", name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};
CheckInputSimulator();
var path = Pathfinding.AStar(new(0, 0), new(3, 0), p => p.X >= 0 && p.X <= 3 && p.Y >= 0 && p.Y <= 2 && !(p.X == 1 && p.Y == 0));
Assert(path?.Count == 6 && path.Zip(path.Skip(1), (a, b) => a.Distance(b)).All(d => d == 1), "A* detours around obstacle with cardinal steps");
Assert(Pathfinding.AStar(new(0, 0), new(2, 0), p => p.X == 0 && p.Y == 0) == null, "unreachable goal has no invented path");
var edges = new[] { (From: "Farm", To: "Town", Cost: 8d), (From: "Farm", To: "BusStop", Cost: 2d), (From: "BusStop", To: "Town", Cost: 3d), (From: "Town", To: "Desert", Cost: double.PositiveInfinity) };
var route = Pathfinding.Dijkstra("Farm", "Town", edges, e => e.From, e => e.To, e => e.Cost);
Assert(route?.Count == 2 && route[0].To == "BusStop", "Dijkstra selects lower-cost cross-map route");
Assert(Pathfinding.Dijkstra("Farm", "Desert", edges, e => e.From, e => e.To, e => e.Cost) == null, "closed map connection is excluded");
var sessionFile = Path.Combine(root, ".local", "session.json");
using var session = JsonDocument.Parse(File.ReadAllText(sessionFile));
var endpoint = session.RootElement.GetProperty("endpoint").GetString()!;
var token = session.RootElement.GetProperty("token").GetString()!;
using var unauthenticated = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:27841") };
Assert((await unauthenticated.GetAsync("/health")).StatusCode == HttpStatusCode.Unauthorized, "unauthenticated HTTP rejected");
using var http = new HttpClient { BaseAddress = unauthenticated.BaseAddress };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var initial = JsonDocument.Parse(await http.GetStringAsync("/health"));
if (initial.RootElement.GetProperty("connected").GetBoolean()) throw new Exception("Do not run mock-client checks while a real game is connected.");

using var client = new BridgeClient(endpoint, token);
await Until(() => client.Healthy, "client receives authenticated heartbeat");
var observation = new GameObservation { Revision = 1, SessionId = "check-session", SaveId = "check-save",
    PlayerId = 2, PlayerName = "MockFarmhand", WorldReady = true, IsHost = false, Multiplayer = true, Location = "Farm" };
client.Send("observation", observation.SessionId, observation);
await UntilAsync(async () =>
{
    var state = await http.GetFromJsonAsync<GameObservation>("/state");
    return state?.PlayerId == 2 && state.SaveId == "check-save";
}, "observation roundtrip");

var taskId = Guid.NewGuid().ToString("N");
var taskResponse = await http.PostAsJsonAsync("/task", new { skill = "go_to", arguments = new { location = "Farm", x = 1, y = 1 }, taskId });
Assert(taskResponse.StatusCode == HttpStatusCode.Accepted, "task dispatch returns accepted, not completed");
var received = await Read("task");
var command = JsonSerializer.Deserialize<TaskCommand>(received.Payload)!;
Assert(command.TaskId == taskId && command.PlayerId == 2 && command.SessionId == "check-session", "task bound to observed identity");
client.Send("result", observation.SessionId, new TaskResult { TaskId = taskId, Status = "completed", Reason = "mock_only", Completed = 1 });
await UntilAsync(async () => (await http.GetFromJsonAsync<TaskResult[]>("/results"))!.Any(r => r.TaskId == taskId && r.Status == "completed"), "result event roundtrip");

var timer = Stopwatch.StartNew();
(await http.PostAsync("/pause", null)).EnsureSuccessStatusCode();
await Read("pause");
timer.Stop();
Assert(timer.ElapsedMilliseconds < 250, $"local pause-message delivery {timer.ElapsedMilliseconds}ms (transport only; game stop needs live verification)");
client.Dispose();
await UntilAsync(async () =>
{
    using var response = JsonDocument.Parse(await http.GetStringAsync("/health"));
    return !response.RootElement.GetProperty("connected").GetBoolean();
}, "disconnect clears connection");
Assert(await http.GetStringAsync("/state") == "null", "disconnect removes stale observation");
Console.WriteLine("All M0 transport checks passed. No game or cloud-model behavior was validated by these checks.");

async Task<BridgeEnvelope> Read(string type)
{
    BridgeEnvelope? result = null;
    await Until(() =>
    {
        while (client.TryRead(out var message)) if (message.Type == type) { result = message; return true; }
        return false;
    }, "received " + type);
    return result!;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static void CheckInputSimulator()
{
    var simulator = new AiInputSimulator { Direction = "right" };
    var method = typeof(AiInputSimulator).GetMethod("SimulateInput")!;
    var frame = Enumerable.Repeat<object>(false, 18).ToArray();
    method.Invoke(simulator, frame);
    Assert((bool)frame[7] && (bool)frame[15], "native simulator starts and holds requested direction");
    simulator.Clear();
    frame = Enumerable.Repeat<object>(true, 18).ToArray(); // All physical controls deliberately held.
    method.Invoke(simulator, frame);
    Assert(!(bool)frame[0] && !(bool)frame[2] && !(bool)frame[6] && !(bool)frame[7]
        && !(bool)frame[8] && !(bool)frame[9] && frame.Skip(14).All(v => !(bool)v)
        && (bool)frame[10] && (bool)frame[11] && (bool)frame[12] && (bool)frame[13],
        "clear suppresses physical controls and explicitly releases all movement");
    simulator.Tool = true;
    method.Invoke(simulator, frame);
    Assert((bool)frame[2], "tool press is emitted");
    method.Invoke(simulator, frame);
    Assert(!(bool)frame[2], "held tool does not retrigger press every frame");
    simulator.Clear();
    method.Invoke(simulator, frame);
    Assert((bool)frame[3], "tool release is emitted");
    simulator.Direction="right";frame=Enumerable.Repeat<object>(false,18).ToArray();method.Invoke(simulator,frame);
    simulator.ManualControl=true;frame=Enumerable.Repeat<object>(false,18).ToArray();frame[0]=true;frame[6]=true;frame[14]=true;
    method.Invoke(simulator,frame);
    Assert((bool)frame[0]&&(bool)frame[6]&&(bool)frame[14]&&(bool)frame[11],"manual takeover preserves player actions and releases the previous AI direction");
    simulator.ManualControl=false;simulator.Clear();method.Invoke(simulator,frame);
    Assert(!(bool)frame[0]&&!(bool)frame[6]&&!(bool)frame[14],"resuming autonomous input suppresses physical controls again");
}

static async Task Until(Func<bool> condition, string message)
{
    var until = Stopwatch.StartNew();
    while (!condition()) { if (until.Elapsed > TimeSpan.FromSeconds(6)) throw new Exception("Timed out: " + message); await Task.Delay(10); }
    Console.WriteLine("PASS: " + message);
}

static async Task UntilAsync(Func<Task<bool>> condition, string message)
{
    var until = Stopwatch.StartNew();
    while (!await condition()) { if (until.Elapsed > TimeSpan.FromSeconds(6)) throw new Exception("Timed out: " + message); await Task.Delay(10); }
    Console.WriteLine("PASS: " + message);
}
