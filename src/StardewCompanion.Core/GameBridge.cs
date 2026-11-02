using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public interface IGameBridge
{
    GameObservation? Observation { get; }
    bool Connected { get; }
    bool Fresh { get; }
    event Action<GameObservation>? Observed;
    event Action<TaskResult>? Result;
    Task<string> Control(string type);
    Task<string> AcknowledgeEvents(string[] ids)=>Task.FromResult("");
    Task<TaskResult> Dispatch(string skill, object arguments, bool waitForTerminal = false, CancellationToken cancellation = default, string? taskId = null, string encounterPolicy = "interrupt");
}

public sealed class GameBridge : IGameBridge, IAsyncDisposable
{
    private WebSocket? _socket;
    private WebApplication? _app;
    private readonly SemaphoreSlim _writer = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskResult>> _waiting = new();
    private readonly ConcurrentDictionary<string, bool> _terminalOnly = new();
    private readonly ConcurrentQueue<TaskResult> _results = new();
    private readonly object _connectionGate = new();
    private readonly SemaphoreSlim _lifecycle = new(1);
    private bool _disposed;
    private CompanionRuntime? _agent;
    public void AttachAgent(CompanionRuntime runtime)=>_agent=runtime;
    public GameObservation? Observation { get; private set; }
    public bool Connected => _socket?.State == WebSocketState.Open;
    private long _lastObservation;
    public bool Fresh => Connected && Environment.TickCount64-Interlocked.Read(ref _lastObservation)<3500;
    public event Action<GameObservation>? Observed;
    public event Action<TaskResult>? Result;
    public string Endpoint { get; private set; } = "";
    public string Token { get; private set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public async Task Start(string sessionFile, int port = 27841)
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_app != null) throw new InvalidOperationException("本地服务已经启动。");
            try { await StartCore(sessionFile, port); }
            catch
            {
                var failed = _app; _app = null; Endpoint = "";
                if (failed != null) await failed.DisposeAsync();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }
    private async Task StartCore(string sessionFile, int port)
    {
        // Keep an already running game able to reconnect after its frontend restarts.
        if(File.Exists(sessionFile)){
            try{
                using var previous=JsonDocument.Parse(File.ReadAllText(sessionFile));
                var root=previous.RootElement;
                if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("endpoint",out var endpoint)&&endpoint.ValueKind==JsonValueKind.String&&endpoint.GetString()==$"ws://127.0.0.1:{port}/bridge"
                    &&root.TryGetProperty("token",out var token)&&token.ValueKind==JsonValueKind.String
                    &&token.GetString() is {Length:64} value&&value.All(Uri.IsHexDigit))Token=value;
            }
            catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){ }
        }
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build(); _app.UseWebSockets();
        _app.Use(async (context,next) =>
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + Token))) { context.Response.StatusCode=401; return; }
            await next(context);
        });
        _app.Map("/bridge", async context =>
        { if (!context.WebSockets.IsWebSocketRequest) {context.Response.StatusCode=400;return;} using var socket=await context.WebSockets.AcceptWebSocketAsync(); await Connect(socket,context.RequestAborted); });
        _app.MapGet("/health", () => new { connected=Connected, fresh=Fresh, stage="development", modelEnabled=_agent?.PlanningEnabled==true });
        _app.MapGet("/state", () => Results.Text(JsonSerializer.Serialize(Connected ? Observation : null),"application/json"));
        _app.MapGet("/results", () => _results.ToArray());
        _app.MapGet("/agent/state",()=>_agent==null?Results.Problem("Agent is not attached",statusCode:503):Results.Json(new{
            _agent.Status,_agent.Running,_agent.Thinking,_agent.PlanningEnabled,_agent.Mode,_agent.Scope,_agent.PendingQuestion,goalVersion=_agent.PlayerGoal["version"]?.GetValue<int>()??0,awareness=_agent.AwarenessSnapshot(),latestEvent=_agent.Decisions.Latest,observation=Observation}));
        _app.MapGet("/agent/plans",()=>_agent==null?Results.Problem("Agent is not attached",statusCode:503):Results.Json(_agent.Plans));
        _app.MapGet("/agent/goal",()=>_agent==null?Results.Problem("Agent is not attached",statusCode:503):Results.Json(_agent.PlayerGoal));
        _app.MapGet("/agent/input",()=>_agent==null?Results.Problem("Agent is not attached",statusCode:503):Results.Json(_agent.InputPreview));
        _app.MapPost("/agent/goal",async(PlayerGoalUpdate goal)=>{
            if(_agent==null)return Results.Problem("Agent is not attached",statusCode:503);
            try{return Results.Json(await _agent.UpdateGoal(goal));}
            catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
            catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
        });
        _app.MapPost("/agent/mode",async(AgentMode request)=>{
            if(_agent==null)return Results.Problem("Agent is not attached",statusCode:503);
            try{await _agent.SetMode(request.Mode);return Results.Ok(new{_agent.Mode});}
            catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
        });
        _app.MapGet("/agent/events",(long? after,int? limit)=>_agent==null?Results.Problem("Agent is not attached",statusCode:503):Results.Json(new{
            latest=_agent.Decisions.Latest,events=_agent.Decisions.Read(after??0,limit??200)}));
        _app.MapPost("/agent/message",(AgentMessage message)=>{
            if(_agent==null)return Results.Problem("Agent is not attached",statusCode:503);
            if(string.IsNullOrWhiteSpace(message.Text)||message.Text.Length>16000)return Results.BadRequest(new{error="Message length must be 1..16000"});
            try{_agent.SendMessage(message.Text);return Results.Accepted();}catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
        });
        foreach(var action in new[]{"pause","resume","cancel"})
            _app.MapPost("/agent/"+action,async()=>{
                if(_agent==null)return Results.Problem("Agent is not attached",statusCode:503);
                try{await (action=="pause"?_agent.Pause():action=="resume"?_agent.Resume():_agent.Cancel());return Results.Ok(new{_agent.Status,_agent.Running});}
                catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
            });
        _app.MapPost("/agent/chest",async(AgentChest request)=>{
            if(_agent==null)return Results.Problem("Agent is not attached",statusCode:503);
            try{await _agent.BindChest(request.Name,request.X,request.Y,request.AllowedIds,request.ProtectedIds);return Results.Ok();}
            catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
        });
        foreach (var type in new[] {"pause","resume","cancel"})
            _app.MapPost("/"+type, async () => Results.Accepted(value:new {requestId=await Control(type)}));
        _app.MapPost("/task", async (BridgeTask request) =>
        {
            var state=Observation;
            if (!Fresh || state?.WorldReady!=true) return Results.Conflict(new {error="No fresh game observation; loading or game not updating"});
            var command=Command(state,request.Skill,request.Arguments,request.TaskId);
            _agent?.RecordExternalTask(command);
            await Send("task",command); return Results.Accepted(value:new {command.TaskId});
        });
        await _app.StartAsync(); Endpoint=$"ws://127.0.0.1:{port}/bridge";
        AtomicFile.WriteAllText(sessionFile,JsonSerializer.Serialize(new {endpoint=Endpoint,token=Token}));
    }
    private static TaskCommand Command(GameObservation state,string skill,object arguments,string? taskId=null,string encounterPolicy="interrupt") => new()
    {TaskId=taskId??Guid.NewGuid().ToString("N"),SessionId=state.SessionId,SaveId=state.SaveId,PlayerId=state.PlayerId,ObservationRevision=state.Revision,ExpiresAtUtcMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+5000,Skill=skill,Arguments=JsonSerializer.Serialize(arguments),EncounterPolicy=encounterPolicy};
    public Task<string> AcknowledgeEvents(string[] ids)=>Send("events_ack",new{ids});
    public Task<string> Control(string type) => Send(type,new {});
    public async Task<TaskResult> Dispatch(string skill, object arguments, bool waitForTerminal=false,CancellationToken cancellation=default,string? taskId=null,string encounterPolicy="interrupt")
    {
        var state=Observation;
        if (!Fresh || state?.WorldReady!=true) throw new InvalidOperationException("游戏正在加载或观察已过期，暂不执行动作");
        var command=Command(state,skill,arguments,taskId,encounterPolicy);
        var completion=new TaskCompletionSource<TaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[command.TaskId]=completion; _terminalOnly[command.TaskId]=waitForTerminal;
        try
        {
            await Send("task",command);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(waitForTerminal?15:5),cancellation);
        }
        finally {_waiting.TryRemove(command.TaskId,out _);_terminalOnly.TryRemove(command.TaskId,out _);}
    }
    private async Task Connect(WebSocket socket,CancellationToken cancellation)
    {
        lock(_connectionGate) {if (_socket==null) _socket=socket;}
        if (!ReferenceEquals(_socket,socket)) {await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,"Only one AI client",cancellation);return;}
        Observation=null;
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var heartbeat=Heartbeat(lifetime.Token);
        try
        {
            var buffer=new byte[8192];
            while(socket.State==WebSocketState.Open)
            {
                using var message=new MemoryStream(); WebSocketReceiveResult frame;
                do
                {
                    frame=await socket.ReceiveAsync(buffer,lifetime.Token);
                    if(frame.MessageType==WebSocketMessageType.Close)return;
                    if(frame.MessageType!=WebSocketMessageType.Text||message.Length+frame.Count>1024*1024)throw new IOException("Invalid bridge frame");
                    message.Write(buffer,0,frame.Count);
                }while(!frame.EndOfMessage);
                var envelope=JsonSerializer.Deserialize<BridgeEnvelope>(message.ToArray());
                if(envelope?.ProtocolVersion!="0.1")throw new IOException("Protocol mismatch");
                if(envelope.Type=="observation")
                { var observation=JsonSerializer.Deserialize<GameObservation>(envelope.Payload)!; if(observation.SessionId!=envelope.SessionId)throw new IOException("Session mismatch"); Interlocked.Exchange(ref _lastObservation,Environment.TickCount64); Observation=observation; Observed?.Invoke(observation); }
                if(envelope.Type=="result" && (Observation==null || envelope.SessionId==Observation.SessionId))
                {
                    var result=JsonSerializer.Deserialize<TaskResult>(envelope.Payload)!;
                    _results.Enqueue(result);while(_results.Count>200)_results.TryDequeue(out _);
                    if(_waiting.TryGetValue(result.TaskId,out var waiting) && (!_terminalOnly.GetValueOrDefault(result.TaskId)||result.Status is "completed" or "partial" or "failed" or "cancelled" or "rejected")) waiting.TrySetResult(result);
                    Result?.Invoke(result);
                }
            }
        }
        catch(Exception e) when(e is WebSocketException or OperationCanceledException or IOException or JsonException) {}
        finally
        {
            lifetime.Cancel(); socket.Abort();
            try {await heartbeat;}catch(Exception e)when(e is OperationCanceledException or WebSocketException or IOException){}
            lock(_connectionGate){if(ReferenceEquals(_socket,socket))_socket=null;}
            Observation=null;
            foreach(var waiter in _waiting.Values)waiter.TrySetException(new IOException("游戏连接已断开，动作结果需重新观察"));
        }
    }
    private async Task Heartbeat(CancellationToken cancellation)
    {while(!cancellation.IsCancellationRequested){await Send("heartbeat",new{});await Task.Delay(1000,cancellation);}}
    private async Task<string> Send(string type,object payload)
    {
        await _writer.WaitAsync();
        try
        {
            var socket=_socket;if(socket?.State!=WebSocketState.Open)return "";
            var envelope=new BridgeEnvelope{Type=type,MessageId=Guid.NewGuid().ToString("N"),SessionId=Observation?.SessionId??"",Payload=JsonSerializer.Serialize(payload)};
            using var timeout=new CancellationTokenSource(2000);
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)),WebSocketMessageType.Text,true,timeout.Token);
            return envelope.MessageId;
        }
        finally{_writer.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await Control("pause"); }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException) { }
            finally
            {
                _socket?.Abort();
                try { if (_app != null) await _app.DisposeAsync(); }
                finally { _app = null; _agent = null; Endpoint = ""; Observation = null; }
            }
        }
        finally { _lifecycle.Release(); }
    }
}
public sealed record BridgeTask(string Skill,JsonElement Arguments,string? TaskId);
  public sealed record AgentMode(string Mode);
public sealed record AgentMessage(string Text);
public sealed record AgentChest(string Name,int X,int Y,string[] AllowedIds,string[] ProtectedIds);
