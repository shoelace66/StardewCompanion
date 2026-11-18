using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

namespace StardewCompanion.Desktop;

internal sealed class SpringTrialSession(string workspace,ModelSettings settings) : IAsyncDisposable
{
    internal const int Days=28;
    private const int Port=27842;
    private readonly GameBridge _bridge=new();
    private readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(55)};
    private CompanionRuntime? _runtime;
    private Process? _game;
    private SpringTrialEvidence? _evidence;
    private long _journalCursor;
    private bool _boundaryObserved;
    private bool _disposed;

    internal string? RunRoot {get;private set;}
    internal GameObservation? Observation=>_bridge.Observation;
    internal bool Exited=>_game?.HasExited==true;
    internal bool Connected=>_bridge.Connected;
    internal bool Running=>_runtime?.Running==true;
    internal bool Thinking=>_runtime?.Thinking==true;
    internal string Status=>_runtime?.Status??"";
    internal string PendingQuestion=>_runtime?.PendingQuestion??"";

    internal async Task Start(IProgress<string> status,CancellationToken cancellation)
    {
        var runningGames=Process.GetProcessesByName("StardewModdingAPI").Concat(Process.GetProcessesByName("Stardew Valley")).ToArray();
        foreach(var process in runningGames)process.Dispose();
        if(runningGames.Length>0)throw new InvalidOperationException("已有星露谷游戏窗口。请先结束当前游戏，再开一轮新测试。");
        if(string.IsNullOrWhiteSpace(settings.ApiKeyDpapi))throw new InvalidOperationException("请先在“设置与工作箱”中保存模型密钥。");
        settings.ValidateEndpoint();
        var gamePath=Path.GetFullPath(settings.GamePath);
        var gameExe=Path.Combine(gamePath,"StardewModdingAPI.exe");
        if(!File.Exists(gameExe))throw new InvalidOperationException("游戏目录中找不到 SMAPI。请在设置中核对游戏目录。");
        var stage=Path.Combine(workspace,"tools","stage-solo-mods.ps1");
        var plan=Path.Combine(workspace,"docs","春季全流程自主经营测试计划.md");
        var objective=File.ReadLines(plan).Single(line=>line.StartsWith("> 自主经营",StringComparison.Ordinal))[2..];
        var runId=$"SC28D_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        var root=RunRoot=Directory.CreateDirectory(Path.Combine(workspace,".local","solo5",runId)).FullName;
        var mods=Path.Combine(root,"Mods");
        status.Report("正在准备独立存档与测试模组…");
        var stageStart=new ProcessStartInfo("powershell.exe"){
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true
        };
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",stage,"-ModsPath",mods})stageStart.ArgumentList.Add(arg);
        using(var process=Process.Start(stageStart)??throw new InvalidOperationException("无法准备测试模组。")){
            var errors=process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            var error=await errors;
            if(process.ExitCode!=0)throw new InvalidOperationException("准备测试模组失败，请先构建开发版。"+Environment.NewLine+error.Trim());
        }

        status.Report("正在验证模型连通与工具调用…");
        var probe=await new ModelClient(_http).TestToolCalling(settings,cancellation);
        SpringTrialEvidence.Write(root,"preflight-model.json",new{at=DateTimeOffset.UtcNow,probe.ElapsedMs,probe.Usage});
        var entries=Directory.EnumerateFiles(mods,"*",SearchOption.AllDirectories)
            .Concat(new[]{typeof(SpringTrialSession).Assembly.Location,typeof(CompanionRuntime).Assembly.Location,
                Path.Combine(gamePath,"Stardew Valley.dll"),Path.Combine(gamePath,"StardewModdingAPI.dll"),stage});
        SpringTrialEvidence.Write(root,"trial.json",new{
            runId,trialDays=Days,createdAt=DateTimeOffset.UtcNow,settings.Model,provider=settings.ValidateEndpoint().Host,
            thinking="no request override",farmLayout="Standard (0)",disabledSkills=Array.Empty<string>(),
            commit=await Git("rev-parse","HEAD"),worktree=await Git("status","--short","-uno"),
            files=entries.ToDictionary(path=>path,path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        });
        if(await Git("diff","--binary","HEAD") is {Length:>0} diff)
            File.WriteAllText(Path.Combine(root,"source-changes.patch"),diff+"\n");

        var profile=Path.Combine(root,"profile");
        var roaming=Path.Combine(profile,"AppData","Roaming");
        var local=Path.Combine(profile,"AppData","Local");
        var gameProfile=Directory.CreateDirectory(Path.Combine(roaming,"StardewValley")).FullName;
        Directory.CreateDirectory(local);
        var preferences=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"StardewValley","startup_preferences");
        if(File.Exists(preferences)){
            var xml=XDocument.Load(preferences);
            xml.Root?.SetElementValue("windowedBorderlessFullscreen",false);
            xml.Root?.SetElementValue("fullscreen",false);
            xml.Save(Path.Combine(gameProfile,"startup_preferences"));
        }
        status.Report("正在以窗口模式创建春 1 新农场…");
        await _bridge.Start(Path.Combine(root,"session.json"),Port);
        _runtime=new CompanionRuntime(_bridge,new MemoryStore(Path.Combine(root,"memory.db")),settings,_http,Path.Combine(root,"logs","events.jsonl"))
            {AutonomousTrialEndDay=Days};
        _bridge.AttachAgent(_runtime);
        _runtime.Start();
        var start=new ProcessStartInfo(gameExe){WorkingDirectory=gamePath,UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal};
        foreach(var name in start.Environment.Keys.Where(key=>key.StartsWith("SC_",StringComparison.Ordinal)).ToArray())start.Environment.Remove(name);
        foreach(var arg in new[]{"--mods-path",mods,"--no-terminal"})start.ArgumentList.Add(arg);
        start.Environment["USERPROFILE"]=profile;
        start.Environment["APPDATA"]=roaming;
        start.Environment["LOCALAPPDATA"]=local;
        start.Environment["SC_AI_CLIENT"]="1";
        start.Environment["SC_BRIDGE_ENDPOINT"]=_bridge.Endpoint;
        start.Environment["SC_BRIDGE_TOKEN"]=_bridge.Token;
        start.Environment["SC_SOLO_TEST"]="1";
        start.Environment["SC_SOLO_TEST_NAME"]=runId;
        start.Environment["SC_M0_FIXTURE"]="solo5";
        start.Environment["SC_SOLO_TEST_EVIDENCE"]=Path.Combine(root,"world.json");
        cancellation.ThrowIfCancellationRequested();
        _game=Process.Start(start)??throw new InvalidOperationException("无法启动 SMAPI 游戏窗口。");
        var expires=DateTime.UtcNow.AddMinutes(2);
        while(DateTime.UtcNow<expires&&!Exited){
            await Task.Delay(200,cancellation);
            if(!_bridge.Fresh||_runtime.Scope==""||Observation is not {WorldReady:true,Multiplayer:false,GameDay:0,Menu:"",ActiveTaskId:"",ControlMode:"solo_test"} observation||observation.TestRunId!=runId)continue;
            _evidence??=new SpringTrialEvidence(root,Path.Combine(gameProfile,"Saves"),runId);
            if(!_evidence.CaptureCheckpoint(observation))continue;
            File.WriteAllText(Path.Combine(root,"initial-instruction.txt"),objective);
            var goal=await _runtime.UpdateGoal(new PlayerGoalUpdate{
                Objective=objective,CompletionCondition="春 28 夜原版结算并保存，夏 1 早晨停止。",
                Autonomy="由模型自主决定游戏内的开局消费、经营顺序、工作和休息。",DisabledSkills=[],ExpectedVersion=0
            });
            File.WriteAllText(Path.Combine(root,"saved-goal.json"),goal.ToJsonString());
            cancellation.ThrowIfCancellationRequested();
            await _runtime.Resume();
            return;
        }
        throw new InvalidOperationException("未能在两分钟内读取隔离存档的春 1 状态，请查看运行记录。");
    }

    internal bool Collect(GameObservation observation)
    {
        if(_runtime==null||_evidence==null||!_bridge.Fresh)return false;
        _evidence.CaptureObservation(observation);
        foreach(var entry in _runtime.Decisions.Read(_journalCursor,1000)){
            _journalCursor=entry.Sequence;
            _boundaryObserved|=entry.Kind=="trial_completed";
        }
        if(observation.GameDay is 7 or 14 or 21)_evidence.CaptureCheckpoint(observation);
        if(!_boundaryObserved||observation is not {GameDay:Days,Paused:true,Menu:"",ActiveTaskId:""})return false;
        return _evidence.CaptureCheckpoint(observation);
    }

    internal async Task Pause()
    {
        if(_runtime!=null&&Connected)await _runtime.Pause();
    }

    private async Task<string?> Git(params string[] arguments)
    {
        try{
            var start=new ProcessStartInfo("git"){WorkingDirectory=workspace,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
            foreach(var argument in arguments)start.ArgumentList.Add(argument);
            using var process=Process.Start(start);
            if(process==null)return null;
            var output=await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode==0?output.TrimEnd():null;
        }
        catch(System.ComponentModel.Win32Exception){return null;}
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;
        _disposed=true;
        try{
            if(_runtime!=null)await _runtime.DisposeAsync();
        }
        finally{
            try{await _bridge.DisposeAsync();}
            finally{
                var game=_game;
                _game=null;
                if(game is {HasExited:false})game.CloseMainWindow();
                game?.Dispose();
                _http.Dispose();
            }
        }
    }
}
