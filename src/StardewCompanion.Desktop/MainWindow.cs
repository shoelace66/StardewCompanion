using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StardewCompanion.Core;

namespace StardewCompanion.Desktop;

public sealed class MainWindow:Window
{
    private readonly string _data;
    private readonly int? _trialDays;
    private GameBridge _bridge=new();
    private CompanionRuntime? _runtime;
    private ModelSettings _settings=new();
    private readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(55)};
    private readonly TextBlock _status=new(){Text="正在启动…",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _startupHint=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkRed};
    private readonly TextBlock _world=new(){Text="等待 AI 加入房间",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _question=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBox _chat=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=250};
    private readonly TextBox _message=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=72,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,ToolTip="输入消息或回答 AI；长期目标在操作流窗口单独编辑。Enter 发送，Shift+Enter 换行"};
    private readonly TextBlock _chatHint=new(){Text="给 AI 的目标和补充要求会显示在上方对话中。Enter 发送，Shift+Enter 换行。自主执行模式会据此规划并操作；仅规划模式只生成计划。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray,Margin=new Thickness(0,0,0,6)};
    private readonly TextBox _endpoint=new();private readonly TextBox _model=new();private readonly TextBox _gamePath=new();private readonly PasswordBox _key=new();
    private readonly ListBox _memories=new(){MinHeight=170,DisplayMemberPath="Display"};
    private readonly ComboBox _kind=new(){ItemsSource=new[]{"玩家约定","长期项目","工作经验","地点说明","每日摘要"},SelectedIndex=1};
    private readonly TextBox _memoryText=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBox _chestName=new(){Text="工作箱"};private readonly TextBox _chestX=new(){Text="0"};private readonly TextBox _chestY=new(){Text="0"};private readonly TextBox _allowed=new(){Text="(O)388,(O)24"};private readonly TextBox _protected=new();
    private readonly TextBox _diagnostics=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBox _plans=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private bool _closing;
    private bool _shutdownComplete;
    private bool _settingsLoaded;
    private Task? _initializing;
    private string _loadedScope="";
    public MainWindow(string data,int? trialDays=null)
    {
        _data=data;_trialDays=trialDays;Title="星露谷 · AI 协同陪玩（开发预览）";Width=1100;Height=800;MinWidth=850;MinHeight=650;
        FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;Background=new SolidColorBrush(Color.FromRgb(242,245,238));
        var root=new DockPanel{Margin=new Thickness(20)};Content=root;
        var title=new TextBlock{Text="一起经营这座农场",FontSize=25,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)};DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10)};
        controls.Children.Add(Button("暂停",()=>NeedRuntime().Pause()));controls.Children.Add(Button("继续",()=>NeedRuntime().Resume()));controls.Children.Add(Button("取消工作",()=>NeedRuntime().Cancel()));
        controls.Children.Add(Button("仅规划",()=>NeedRuntime().SetMode("plan_only")));controls.Children.Add(Button("自主执行",()=>NeedRuntime().SetMode("autonomous")));
        controls.Children.Add(Button("打开原版房主",()=>Launch(false)));controls.Children.Add(Button("打开 AI 客户端",()=>Launch(true)));
        DockPanel.SetDock(controls,Dock.Top);root.Children.Add(controls);
        var state=new StackPanel{Margin=new Thickness(0,0,0,12)};state.Children.Add(_status);state.Children.Add(_startupHint);state.Children.Add(_world);DockPanel.SetDock(state,Dock.Top);root.Children.Add(state);
        var tabs=new TabControl();root.Children.Add(tabs);
        var conversation=new DockPanel{Margin=new Thickness(12)};
        var ask=new StackPanel{Margin=new Thickness(0,0,0,8)};ask.Children.Add(_question);ask.Children.Add(Button("移除待答问题（不会批准）",()=>{NeedRuntime().ClearQuestion();return Task.CompletedTask;}));DockPanel.SetDock(ask,Dock.Top);conversation.Children.Add(ask);
        var input=new DockPanel{Margin=new Thickness(0,10,0,0)};
        var send=Button("发送给 AI",SendChat);send.MinWidth=110;DockPanel.SetDock(send,Dock.Right);input.Children.Add(send);input.Children.Add(_message);
        var composer=new DockPanel();composer.Children.Add(input);DockPanel.SetDock(input,Dock.Bottom);DockPanel.SetDock(_chatHint,Dock.Top);composer.Children.Add(_chatHint);DockPanel.SetDock(composer,Dock.Bottom);conversation.Children.Add(composer);conversation.Children.Add(_chat);
        _message.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Shift)==0){e.Handled=true;_ = RunAction(SendChat);}};
        tabs.Items.Add(new TabItem{Header="中文聊天与工作",Content=conversation});
        tabs.Items.Add(new TabItem{Header="工作记忆",Content=MemoryPanel()});
        tabs.Items.Add(new TabItem{Header="设置与工作箱",Content=SettingsPanel()});
        tabs.Items.Add(new TabItem{Header="诊断",Content=_diagnostics});
        tabs.Items.Add(new TabItem{Header="规划与成果",Content=_plans});
        Loaded+=async(_,_)=>await Initialize();Closing+=OnClosing;
        _timer.Tick+=(_,_)=>Refresh();
        _memories.SelectionChanged+=(_,_)=>{if(_memories.SelectedItem is MemoryEntry e)_memoryText.Text=e.Text;};
    }
    private CompanionRuntime NeedRuntime()=>_runtime??throw new InvalidOperationException("程序尚未就绪");
    private Button Button(string text,Func<Task> action)
    {
        var b=new Button{Content=text,Padding=new Thickness(14,7,14,7),Margin=new Thickness(0,0,8,0)};
        b.Click+=async(_,_)=>await RunAction(action);return b;
    }
    private async Task RunAction(Func<Task> action)
    {if(_closing)return;try{await action();}catch(Exception e){_status.Text=e.Message;}}
    private static void Field(Panel p,string label,Control control)
    {p.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,9,0,4)});control.Margin=new Thickness(0,0,0,3);if(control is TextBox t)t.Padding=new Thickness(6);p.Children.Add(control);}
    private UIElement MemoryPanel()
    {
        var p=new StackPanel{Margin=new Thickness(12)};p.Children.Add(new TextBlock{Text="每个存档与 AI 角色单独保存；世界成果在重新加入后需要核对。",TextWrapping=TextWrapping.Wrap});p.Children.Add(_memories);
        Field(p,"新增记忆类型",_kind);Field(p,"目标、约定或记忆内容",_memoryText);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};
        buttons.Children.Add(Button("新增",()=>{var kinds=new[]{"agreement","project","routine","place","episode"};NeedRuntime().AddMemory(kinds[_kind.SelectedIndex],_memoryText.Text);LoadMemory();return Task.CompletedTask;}));
        buttons.Children.Add(Button("更正所选",()=>{if(_memories.SelectedItem is MemoryEntry m)NeedRuntime().EditMemory(m.Id,_memoryText.Text);LoadMemory();return Task.CompletedTask;}));
        buttons.Children.Add(Button("删除所选",()=>{if(_memories.SelectedItem is MemoryEntry m)NeedRuntime().Memory.Delete(NeedRuntime().Scope,m.Id);LoadMemory();return Task.CompletedTask;}));
        buttons.Children.Add(Button("确认项目完成",()=>{if(_memories.SelectedItem is MemoryEntry {Kind:"project"} m)NeedRuntime().ConfirmProjectComplete(m.Id);LoadMemory();return Task.CompletedTask;}));
        buttons.Children.Add(Button("刷新",()=>{LoadMemory();return Task.CompletedTask;}));p.Children.Add(buttons);return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private UIElement SettingsPanel()
    {
        var p=new StackPanel{Margin=new Thickness(12)};
        Field(p,"模型 Chat Completions 完整地址",_endpoint);Field(p,"模型名称",_model);Field(p,"API 密钥（留空则保留现有密钥；使用当前 Windows 用户加密）",_key);Field(p,"星露谷游戏目录",_gamePath);
        p.Children.Add(Button("测试模型工具调用",async()=>
        {
            var testSettings=new ModelSettings{Endpoint=_endpoint.Text.Trim(),Model=_model.Text.Trim(),GamePath=_gamePath.Text.Trim(),ApiKeyDpapi=_settings.ApiKeyDpapi};
            if(_key.Password!="")testSettings.SetKey(_key.Password);
            var test=new ModelClient(_http);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var reply=await test.TestToolCalling(testSettings,timeout.Token);
            _status.Text=$"模型工具调用兼容；observe 探测已验证（{reply.ElapsedMs} ms），未执行游戏动作";
        }));
        p.Children.Add(Button("保存设置",SaveSettings));
        p.Children.Add(Button("重试启动本地服务",Initialize));
        p.Children.Add(new TextBlock{Text="工作箱：先让 AI 到箱子所在地图，再填写当前地图格子坐标。搬迁后需要重新绑定。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,20,0,0)});
        Field(p,"工作箱名称",_chestName);Field(p,"格子 X",_chestX);Field(p,"格子 Y",_chestY);Field(p,"允许物品机器 ID（逗号分隔）",_allowed);Field(p,"保护物品机器 ID（逗号分隔）",_protected);
        p.Children.Add(Button("绑定当前地图的箱子",async()=>{await NeedRuntime().BindChest(_chestName.Text,int.Parse(_chestX.Text),int.Parse(_chestY.Text),Split(_allowed.Text),Split(_protected.Text));LoadMemory();_status.Text="工作箱已绑定";}));
        return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private static string[] Split(string text)=>text.Split(new[]{',','，','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    private async Task SaveSettings()
    {
        var candidate=new ModelSettings{Endpoint=_endpoint.Text.Trim(),Model=_model.Text.Trim(),GamePath=_gamePath.Text.Trim(),ApiKeyDpapi=_settings.ApiKeyDpapi};
        candidate.ValidateEndpoint();
        if(_key.Password!="")candidate.SetKey(_key.Password);
        candidate.Save(Path.Combine(_data,"model.credentials.json"));
        _settings=candidate;_settingsLoaded=true;_key.Clear();_startupHint.Text="";
        if(_runtime!=null){_runtime.Settings=candidate;_status.Text="设置已保存";}
        else await Initialize();
    }
    private Task SendChat()
    {
        var text=_message.Text.Trim();
        if(text.Length==0)return Task.CompletedTask;
        NeedRuntime().SendMessage(text);_message.Clear();_message.Focus();return Task.CompletedTask;
    }
    private Task Initialize()
    {
        if(_closing||_runtime!=null)return Task.CompletedTask;
        return _initializing is {IsCompleted:false}?_initializing:_initializing=InitializeCore();
    }
    private async Task InitializeCore()
    {
        MemoryStore? memory=null;
        try
        {
            Directory.CreateDirectory(_data);
            if(!_settingsLoaded)
            {
                _settings=ModelSettings.LoadForEditing(Path.Combine(_data,"model.credentials.json"),out var recoveryFile);
                _settingsLoaded=true;
                _endpoint.Text=_settings.Endpoint;_model.Text=_settings.Model;_gamePath.Text=_settings.GamePath;
                if(recoveryFile!=null)_startupHint.Text="设置文件损坏，已备份至 "+recoveryFile+"。请重新填写并保存模型设置。";
            }
            _status.Text="正在启动本地服务…";
            memory=new MemoryStore(Path.Combine(_data,"memory.db"));
            await _bridge.Start(Path.Combine(_data,"session.json"));
            if(_closing)return;
            _runtime=new CompanionRuntime(_bridge,memory,_settings,_http,Path.Combine(_data,"logs","events.jsonl"));
            memory=null; // The runtime now owns the store.
            _runtime.AutonomousTrialEndDay=_trialDays;
            _bridge.AttachAgent(_runtime);
            _runtime.ChatReceived+=(role,text)=>Dispatcher.BeginInvoke(()=>{_chat.AppendText($"{role}：{text}\n\n");_chat.ScrollToEnd();});
            _runtime.Start();_timer.Start();_status.Text="服务已就绪。请先打开原版房主，再让 AI 加入。";
        }
        catch(Exception e)
        {
            _status.Text="启动失败，自动执行未启动："+e.Message+"。处理后可在设置页重试启动。";
            try { await StopServices(); }
            catch(Exception cleanup){_status.Text+=" 清理失败："+cleanup.Message;}
            finally { _bridge=new(); }
        }
        finally { memory?.Dispose(); }
    }
    private void Refresh()
    {
        if(_runtime==null)return;
        _status.Text=_runtime.Status;var s=_runtime.Observation;
        _world.Text=s?.WorldReady==true?$"{s.PlayerName} · {s.Location} ({s.Tile.X},{s.Tile.Y}) · {s.TimeOfDay/100:00}:{s.TimeOfDay%100:00} · 体力 {s.Stamina:0} · {(_runtime.Running?"工作已启用":"等待继续")}":"AI 尚未连接游戏存档";
        _question.Text=_runtime.PendingQuestion==""?"暂无待答问题":("等待你的回答："+_runtime.PendingQuestion);
        var modeHint=_runtime.Mode=="plan_only"?"仅规划（只生成/修订计划，不操作游戏）":"自主规划执行";
        var runHint=_runtime.Mode=="plan_only"?"模型可立即规划。":_runtime.Running?"模型会按当前目标自主决策。":"当前已暂停；指令会排队，点“继续”后开始处理。";
        _chatHint.Text=$"当前模式：{modeHint}。{runHint}输入目标或补充要求后发送给 AI；Enter 发送，Shift+Enter 换行。";
        _plans.Text=JsonSerializer.Serialize(new{mode=_runtime.Mode,plans=_runtime.Plans},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        if(_loadedScope!=_runtime.Scope){_loadedScope=_runtime.Scope;LoadMemory();_chat.Clear();foreach(var h in _runtime.Memory.History(_loadedScope))_chat.AppendText($"{(h.Role=="user"?"玩家":"AI")}：{h.Text}\n\n");}
        _diagnostics.Text=JsonSerializer.Serialize(new{connected=_bridge.Connected,status=_runtime.Status,scope=_runtime.Scope,observation=s,decisionEvents=_runtime.Decisions.Read(Math.Max(0,_runtime.Decisions.Latest-30))},new JsonSerializerOptions{WriteIndented=true});
    }
    private void LoadMemory(){if(_runtime!=null)_memories.ItemsSource=_runtime.Memory.List(_runtime.Scope);}
    private Task Launch(bool ai)
    {
        if(_runtime==null)throw new InvalidOperationException("本地服务尚未就绪");
        var game=Path.GetFullPath(_gamePath.Text);var exe=Path.Combine(game,ai?"StardewModdingAPI.exe":"Stardew Valley.exe");
        if(!File.Exists(exe))throw new FileNotFoundException(ai?"未找到 SMAPI，请先安装兼容版本":"未找到原版游戏",exe);
        var start=new ProcessStartInfo(exe){WorkingDirectory=game,UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal};
        foreach(var name in start.Environment.Keys.Where(k=>k.StartsWith("SC_",StringComparison.Ordinal)).ToArray())start.Environment.Remove(name);
        if(ai)
        {
            var mods=Path.Combine(AppContext.BaseDirectory,"Mods");if(!Directory.Exists(mods))mods=Path.GetFullPath(Path.Combine(_data,"ai-mods"));
            if(!File.Exists(Path.Combine(mods,"StardewCompanion.Bridge","manifest.json")))throw new InvalidOperationException("发布包内缺少控制模组，请重新解压完整发布包");
            start.ArgumentList.Add("--mods-path");start.ArgumentList.Add(mods);start.ArgumentList.Add("--no-terminal");
            start.Environment["SC_AI_CLIENT"]="1";start.Environment["SC_BRIDGE_ENDPOINT"]=_bridge.Endpoint;start.Environment["SC_BRIDGE_TOKEN"]=_bridge.Token;
        }
        Process.Start(start);_status.Text=ai?"AI 客户端已打开；首次请手动加入局域网房间":"原版已打开；请选择合作主持存档";return Task.CompletedTask;
    }
    private async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(_shutdownComplete)return;e.Cancel=true;if(_closing)return;_closing=true;_timer.Stop();
        try
        {
            try { if(_initializing!=null)await _initializing; }
            finally { await StopServices(); }
        }
        catch(Exception error){Debug.WriteLine("Shutdown cleanup failed: "+error);}
        finally{_http.Dispose();_shutdownComplete=true;Close();}
    }
    private async Task StopServices()
    {
        try { if(_runtime!=null)await _runtime.DisposeAsync(); }
        finally { _runtime=null;await _bridge.DisposeAsync(); }
    }
}
