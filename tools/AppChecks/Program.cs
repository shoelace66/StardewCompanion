using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var folder = Path.Combine(root, ".local", "checks", "app-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var failures = 0;
var checks = 0;
await Check("null settings are rejected with a recoverable JSON error", () =>
{
    var path = Path.Combine(folder, "null.json");
    File.WriteAllText(path, "null");
    Throws<JsonException>(() => ModelSettings.Load(path));
    return Task.CompletedTask;
});
await Check("settings support a filename relative to the working directory", () =>
{
    var previous = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = folder;
        new ModelSettings { Model = "saved-model" }.Save("relative.json");
        Require(ModelSettings.Load("relative.json").Model == "saved-model", "settings round trip failed");
    }
    finally { Environment.CurrentDirectory = previous; }
    return Task.CompletedTask;
});
await Check("damaged settings are preserved before editing defaults", () =>
{
    foreach (var text in new[] { "{truncated", "null", "[]", "{\"Model\":null}", "{\"ApiKeyDpapi\":3}" })
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text);
        var settings = ModelSettings.LoadForEditing(path, out var backup);
        Require(backup != null && File.ReadAllText(backup) == text, "original settings were not backed up");
        Require(File.ReadAllText(path) == text, "loading overwrote damaged settings");
        settings.Model = "recovered";
        settings.Save(path);
        Require(ModelSettings.Load(path).Model == "recovered", "replacement settings cannot be loaded");
        Require(File.ReadAllText(backup!) == text, "saving destroyed the recovery copy");
    }
    var defaults = ModelSettings.LoadForEditing(Path.Combine(folder, "missing.json"), out var noBackup);
    Require(noBackup == null && defaults.Model.Length > 0, "first run should use defaults without a backup");
    return Task.CompletedTask;
});
await Check("invalid or failed settings saves preserve the committed file", () =>
{
    var path = Path.Combine(folder, "atomic.json");
    new ModelSettings { Model = "committed" }.Save(path);
    var original = File.ReadAllText(path);
    Throws<InvalidOperationException>(() => new ModelSettings { Endpoint = "http://example.com" }.Save(path));
    Require(File.ReadAllText(path) == original, "validation failure changed settings");
    if (OperatingSystem.IsWindows())
    {
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { new ModelSettings { Model = "uncommitted" }.Save(path); throw new Exception("Replacing a locked file succeeded"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Require(File.ReadAllText(path) == original, "failed replacement truncated settings");
        Require(Directory.GetFiles(folder, "atomic.json.*.tmp").Length == 0, "temporary credentials were left behind");
    }
    return Task.CompletedTask;
});
await Check("encrypted settings survive a save and reload without exposing the key", () =>
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    const string testKey = "app-checks-placeholder-not-a-real-api-key";
    var path = Path.Combine(folder, "encrypted.json");
    var settings = new ModelSettings();
    settings.SetKey(testKey);
    settings.Save(path);
    Require(!File.ReadAllText(path).Contains(testKey), "plaintext key was saved");
    Require(ModelSettings.Load(path).GetKey() == testKey, "encrypted key could not be restored");
    return Task.CompletedTask;
});
await Check("failed session publication releases the listener and permits retry", async () =>
{
    var port = FreePort();
    await using var bridge = new GameBridge();
    var blocked = Path.Combine(folder, "session-is-directory");
    Directory.CreateDirectory(blocked);
    try { await bridge.Start(blocked, port); throw new Exception("Startup unexpectedly succeeded"); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    using (var probe = new TcpListener(IPAddress.Loopback, port)) probe.Start();
    Require(bridge.Endpoint == "", "failed startup exposed a ready endpoint");
    await bridge.Start(Path.Combine(folder, "retry-session.json"), port);
    using var http = AuthenticatedClient(bridge);
    Require((await http.GetAsync("health")).IsSuccessStatusCode, "retry did not start the bridge");
});
await Check("malformed session metadata does not prevent startup", async () =>
{
    foreach (var text in new[] { "{\"endpoint\":7,\"token\":false}", "null", "[]", "{truncated" })
    {
        var path = Path.Combine(folder, "malformed-session.json");
        File.WriteAllText(path, text);
        await using var bridge = new GameBridge();
        await bridge.Start(path, FreePort());
        using var http = AuthenticatedClient(bridge);
        Require((await http.GetAsync("health")).IsSuccessStatusCode, "bridge did not recover");
    }
});
await Check("an occupied port preserves session metadata and can be retried", async () =>
{
    using var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
    var path = Path.Combine(folder, "occupied-session.json");
    const string original = "{\"previous\":true}";
    File.WriteAllText(path, original);
    await using var bridge = new GameBridge();
    try { await bridge.Start(path, port); throw new Exception("Startup unexpectedly succeeded"); }
    catch (IOException) { }
    Require(File.ReadAllText(path) == original, "failed bind replaced session metadata");
    occupied.Stop();
    await bridge.Start(path, port);
    using var http = AuthenticatedClient(bridge);
    Require((await http.GetAsync("health")).IsSuccessStatusCode, "retry after port conflict failed");
});
await Check("bridge restart retains authentication and duplicate start leaves the service intact", async () =>
{
    var port = FreePort();
    var path = Path.Combine(folder, "restart-session.json");
    string token;
    await using (var first = new GameBridge())
    {
        await first.Start(path, port);
        token = first.Token;
        try { await first.Start(path, port); throw new Exception("Duplicate start succeeded"); }
        catch (InvalidOperationException) { }
        using var http = AuthenticatedClient(first);
        Require((await http.GetAsync("health")).IsSuccessStatusCode, "duplicate start broke the running service");
    }
    await using var restarted = new GameBridge();
    await restarted.Start(path, port);
    Require(restarted.Token == token, "an existing game could no longer authenticate after restart");
    using var client = AuthenticatedClient(restarted);
    client.DefaultRequestHeaders.Authorization = null;
    Require((await client.GetAsync("health")).StatusCode == HttpStatusCode.Unauthorized, "anonymous access was accepted");
    await restarted.DisposeAsync();
    await restarted.DisposeAsync();
    using var probe = new TcpListener(IPAddress.Loopback, port);
    probe.Start();
    try { await restarted.Start(path, port); throw new Exception("Disposed service restarted"); }
    catch (ObjectDisposedException) { }
});
await Check("shutdown disconnects a game socket and completes pending dispatches", async () =>
{
    var port = FreePort();
    await using var bridge = new GameBridge();
    await bridge.Start(Path.Combine(folder, "connected-session.json"), port);
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", "Bearer " + bridge.Token);
    await socket.ConnectAsync(new Uri(bridge.Endpoint), CancellationToken.None);
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bridge.Observed += _ => observed.TrySetResult();
    var state = new GameObservation { SessionId = "shutdown-test", SaveId = "test-save", PlayerId = 2, WorldReady = true };
    var envelope = new BridgeEnvelope { Type = "observation", SessionId = state.SessionId, Payload = JsonSerializer.Serialize(state) };
    await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)), WebSocketMessageType.Text, true, CancellationToken.None);
    await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var dispatch = bridge.Dispatch("observe", new { });
    await bridge.DisposeAsync();
    try { await dispatch.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Disconnected dispatch succeeded"); }
    catch (IOException) { }
    using var probe = new TcpListener(IPAddress.Loopback, port);
    probe.Start();
});
await Check("runtime shutdown releases memory even when pause delivery fails", async () =>
{
    var bridge = new FailingBridge();
    using var http = new HttpClient();
    using var memory = new MemoryStore(Path.Combine(folder, "shutdown.db"));
    var runtime = new CompanionRuntime(bridge, memory, new(), http, Path.Combine(folder, "shutdown", "events.jsonl"));
    runtime.Start();runtime.Start();
    try
    {
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Throws<InvalidOperationException>(() => memory.List("test"));
        Require(bridge.Subscribers == 0, "bridge event subscriptions leaked");
        Require(bridge.PauseAttempts == 1, "disposal ran more than once");
        Throws<ObjectDisposedException>(runtime.Start);
    }
    finally { bridge.FailPause = false; await runtime.DisposeAsync(); }
});
Console.WriteLine($"Application checks: {checks - failures}/{checks} passed.");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> check)
{
    checks++;
    try { await check(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
static HttpClient AuthenticatedClient(GameBridge bridge)
{
    var http = new HttpClient { BaseAddress = new Uri(bridge.Endpoint.Replace("ws://", "http://").Replace("/bridge", "/")), Timeout = TimeSpan.FromSeconds(5) };
    http.DefaultRequestHeaders.Authorization = new("Bearer", bridge.Token);
    return http;
}

sealed class FailingBridge : IGameBridge
{
    public bool FailPause { get; set; } = true;
    public int PauseAttempts { get; private set; }
    public GameObservation? Observation => null;
    public bool Connected => false;
    public bool Fresh => false;
    public int Subscribers { get; private set; }
    public event Action<GameObservation>? Observed { add => Subscribers++; remove => Subscribers--; }
    public event Action<TaskResult>? Result { add => Subscribers++; remove => Subscribers--; }
    public Task<string> Control(string type)
    {
        PauseAttempts++;
        return FailPause ? Task.FromException<string>(new IOException("Connection lost")) : Task.FromResult("");
    }
    public Task<TaskResult> Dispatch(string skill, object arguments, bool waitForTerminal = false, CancellationToken cancellation = default, string? taskId = null, string encounterPolicy = "interrupt") => throw new NotSupportedException();
}
