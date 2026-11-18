using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StardewCompanion.Core;
using StardewCompanion.Protocol;
using System.Text;

namespace StardewCompanion.Desktop;

internal sealed class SpringTrialWindow : Window
{
    private readonly SpringTrialSession? _session;
    private readonly string? _observedRoot;
    private readonly HttpClient _observer=new(){Timeout=TimeSpan.FromSeconds(3)};
    private readonly CancellationTokenSource _stop=new();
    private readonly DispatcherTimer _poll=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly TextBlock _phase=new(){FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _date=new(){FontSize=32,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,20,0,2)};
    private readonly TextBlock _detail=new(){FontSize=15,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)};
    private readonly TextBlock _activity=new(){TextWrapping=TextWrapping.Wrap,MinHeight=48,Margin=new Thickness(0,0,0,16)};
    private readonly Border _questionCard=new(){Padding=new Thickness(12),Margin=new Thickness(0,4,0,14),Background=new SolidColorBrush(Color.FromRgb(255,248,220)),BorderBrush=new SolidColorBrush(Color.FromRgb(218,184,95)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Visibility=Visibility.Collapsed};
    private readonly TextBlock _questionLabel=new(){FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _conversation=new(){TextWrapping=TextWrapping.Wrap,MinHeight=44};
    private readonly TextBlock _days=new(){Margin=new Thickness(0,0,0,6)};
    private readonly ProgressBar _progress=new(){Maximum=SpringTrialSession.Days,Height=8,Margin=new Thickness(0,0,0,16)};
    private readonly Button _folder=new(){Content="打开运行记录",IsEnabled=false,Padding=new Thickness(12,6,12,6)};
    private readonly Button _result=new(){Content="查看夏 1 结果",IsEnabled=false,Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0)};
    private Task? _launch;
    private bool _polling,_closing,_closed,_completed;
    private long _logPosition;
    private bool _logInitialized;
    private readonly Queue<string> _recentMessages=new();
    private string _lastPlayerMessage="";
    private string _latestQuestion="";
    private string? RunRoot=>_observedRoot??_session?.RunRoot;

    internal static string? FindActiveRun(string workspace)
    {
        var runs=Path.Combine(workspace,".local","solo5");
        try{
            using var pointer=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace,".local","solo5-current.json")));
            var info=pointer.RootElement;
            if(info.GetProperty("trialDays").GetInt32()!=SpringTrialSession.Days)return null;
            var configured=info.GetProperty("runRoot").GetString();
            if(string.IsNullOrEmpty(configured))return null;
            var root=Path.GetFullPath(configured);
            if(!string.Equals(Path.GetDirectoryName(root),runs,StringComparison.OrdinalIgnoreCase))return null;
            using var game=Process.GetProcessById(info.GetProperty("gamePid").GetInt32());
            return game.ProcessName=="StardewModdingAPI"&&File.Exists(Path.Combine(root,"session.json"))?root:null;
        }
        catch(Exception ex)when(ex is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException){return null;}
    }

    internal SpringTrialWindow(string workspace,ModelSettings settings):this()
    {
        _session=new SpringTrialSession(workspace,settings);
        _phase.Text="正在启动春季测试";
    }

    internal SpringTrialWindow(string root):this()
    {
        _observedRoot=root;
        _phase.Text="正在连接运行记录";
        Loaded+=(_,_)=>_poll.Start();
    }

    private SpringTrialWindow()
    {
        Title="春季自主经营测试";Width=520;MinWidth=440;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.Manual;
        Top=SystemParameters.WorkArea.Top+20;Left=Math.Max(SystemParameters.WorkArea.Left,SystemParameters.WorkArea.Right-545);
        Background=new SolidColorBrush(Color.FromRgb(241,245,238));
        Foreground=new SolidColorBrush(Color.FromRgb(28,46,34));
        FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;
        _progress.Foreground=new SolidColorBrush(Color.FromRgb(53,107,71));
        var panel=new StackPanel{Margin=new Thickness(24)};
        Content=panel;
        panel.Children.Add(_phase);
        panel.Children.Add(new TextBlock{Text="春 1 → 夏 1 · 原版单人存档",Foreground=new SolidColorBrush(Color.FromRgb(71,88,74)),Margin=new Thickness(0,8,0,0)});
        panel.Children.Add(_date);
        panel.Children.Add(_detail);
        panel.Children.Add(_days);
        panel.Children.Add(_progress);
        panel.Children.Add(_questionCard);
        panel.Children.Add(_activity);
        panel.Children.Add(new TextBlock{Text="最近对话",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,4)});
        panel.Children.Add(_conversation);
        var buttons=new WrapPanel();
        buttons.Children.Add(_folder);buttons.Children.Add(_result);
        panel.Children.Add(buttons);
        _folder.Click+=(_,_)=>Open(RunRoot);
        _result.Click+=(_,_)=>Open(RunRoot==null?null:Path.Combine(RunRoot,"summer-01-results.json"));
        _poll.Tick+=async (_,_)=>await Refresh();
        Closing+=OnClosing;
    }

    internal Task Start()=>_launch??=StartCore();

    private async Task StartCore()
    {
        _poll.Start();
        try{
            await _session!.Start(new Progress<string>(message=>{if(!_closing)_activity.Text=message;}),_stop.Token);
            _phase.Text="自主经营进行中";
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(Exception ex){
            _phase.Text="启动未完成";
            _activity.Text=ex.Message;
            _poll.Stop();
            UpdateLinks();
            await _session!.DisposeAsync();
        }
    }

    private async Task Refresh()
    {
        if(_polling||_closing)return;
        _polling=true;
        try{
            UpdateLinks();
            if(_session!=null){
                if(_launch?.IsCompleted!=true)return;
                if(_session.Exited){_phase.Text="游戏已退出";_activity.Text="已保留本轮运行记录。";_poll.Stop();return;}
                if(!_session.Connected){_phase.Text="正在等待游戏连接";return;}
                if(_session.Observation is not {WorldReady:true} local)return;
                UpdateConversation(RunRoot);
                _completed|=_session.Collect(local);
                ShowState(local,_session.Running,_session.Thinking,_session.Status,_session.PendingQuestion);
                return;
            }
            if(File.Exists(Path.Combine(_observedRoot!,"summer-01-results.json"))&&
                Directory.Exists(Path.Combine(_observedRoot!,"checkpoints","summer-01"))){
                _completed=true;
                _phase.Text="已到夏 1 · 存档已归档";
                _date.Text="夏 1";
                _days.Text=$"已结算 {SpringTrialSession.Days} / {SpringTrialSession.Days} 天";
                _progress.Value=SpringTrialSession.Days;
                _activity.Text="已核对原版夏 1 存档。";
                _poll.Stop();
                return;
            }
            var file=Path.Combine(_observedRoot!,"session.json");
            UpdateConversation(_observedRoot);
            using var session=JsonDocument.Parse(await File.ReadAllTextAsync(file,_stop.Token));
            var fields=session.RootElement;
            var endpoint=new Uri(fields.GetProperty("endpoint").GetString()??"");
            if(endpoint.Scheme!="ws"||endpoint.Host!="127.0.0.1")throw new InvalidDataException("运行记录的连接地址不是本机回环地址。");
            using var request=new HttpRequestMessage(HttpMethod.Get,new UriBuilder(endpoint){Scheme="http",Path="/agent/state"}.Uri);
            request.Headers.Authorization=new("Bearer",fields.GetProperty("token").GetString());
            using var response=await _observer.SendAsync(request,_stop.Token);
            response.EnsureSuccessStatusCode();
            using var snapshot=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(_stop.Token));
            var agent=snapshot.RootElement;
            var observation=agent.GetProperty("observation").Deserialize<GameObservation>(
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(observation is not {WorldReady:true}||observation.TestRunId!=Path.GetFileName(_observedRoot))return;
            _completed=observation is {GameDay:SpringTrialSession.Days,Paused:true,Menu:"",ActiveTaskId:""}
                &&Directory.Exists(Path.Combine(_observedRoot!,"checkpoints","summer-01"));
            ShowState(observation,agent.GetProperty("running").GetBoolean(),agent.GetProperty("thinking").GetBoolean(),agent.GetProperty("status").GetString()??"",
                agent.GetProperty("pendingQuestion").GetString()??"");
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(Exception ex)when(ex is IOException or JsonException or HttpRequestException or TaskCanceledException){
            _activity.Text=_observedRoot==null?$"记录暂不可写，正在重试。{ex.Message}":"正在等待本轮测试重新连接。";
        }
        finally{_polling=false;}
    }

    private void ShowState(GameObservation state,bool running,bool thinking,string status,string question)
    {
        var naming=state.Menu==PetNaming.Menu;
        _phase.Text=_completed?"已到夏 1 · 存档已归档":
            naming?question!=""?"等待在游戏中命名":"正在处理游戏命名":
            question!=""?"等待你回复":running?"自主经营进行中":"自主经营已暂停";
        _date.Text=state.GameDay>=SpringTrialSession.Days?"夏 1":$"春 {state.GameDay+1}";
        using var world=JsonDocument.Parse(state.WorldView);
        var cash=world.RootElement.GetProperty("money").GetInt32();
        _detail.Text=$"{state.TimeOfDay/100:00}:{state.TimeOfDay%100:00}  ·  {cash:N0} 金  ·  体力 {state.Stamina:0.#}";
        _progress.Value=Math.Min(state.GameDay,SpringTrialSession.Days);
        _days.Text=$"已结算 {_progress.Value:0} / {SpringTrialSession.Days} 天";
        _activity.ToolTip=status;
        _questionCard.Visibility=question!=""?Visibility.Visible:Visibility.Collapsed;
        _questionLabel.Text=question!=""?(naming?question:"Agent 正在等你回复\n"+question):"";
        if(_questionCard.Child is null)_questionCard.Child=_questionLabel;
        _activity.Text=_completed?"已核对原版夏 1 存档。":naming?status:question!=""?"回复可直接输入前端聊天框，Agent 收到后会继续经营。":
            state.Menu is "SaveGameMenu" or "ShippingMenu"?"正在结算并保存原版存档…":
            state.GameDay==SpringTrialSession.Days?"正在归档夏 1 存档…":
            !running||status.StartsWith("模型",StringComparison.Ordinal)?status:
            state.ActiveTaskId!=""?"正在游戏中执行任务…":
            thinking?"正在规划下一步…":"正在核对游戏状态…";
        UpdateLinks();
        if(_completed&&_result.IsEnabled)_poll.Stop();
    }

    private void UpdateConversation(string? root)
    {
        if(string.IsNullOrEmpty(root))return;
        var path=Path.Combine(root,"logs","decisions.jsonl");
        if(!File.Exists(path))return;
        try{
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            if(!_logInitialized){
                stream.Position=0;
                _logInitialized=true;
            }else stream.Position=Math.Min(_logPosition,stream.Length);
            using(var reader=new StreamReader(stream,Encoding.UTF8,true,4096,true)){
                if(stream.Position>0&&_logPosition==0)_=reader.ReadLine();
                string? line;
                while((line=reader.ReadLine())!=null){
                    if(!line.Contains("\"Kind\":\"player_message\"",StringComparison.Ordinal)&&
                       !line.Contains("\"Kind\":\"model_reply\"",StringComparison.Ordinal)&&
                       !line.Contains("\"Kind\":\"tool_selected\"",StringComparison.Ordinal))continue;
                    try{
                        using var document=JsonDocument.Parse(line);
                        var entry=document.RootElement;
                        var kind=entry.GetProperty("Kind").GetString();
                        var data=entry.GetProperty("Data");
                        if(kind=="player_message"&&ReadString(data,"text") is {Length:>0} player){
                            _lastPlayerMessage=player;
                            _latestQuestion="";
                            while(_recentMessages.Count>0)_recentMessages.Dequeue();
                        }
                        else if(kind=="model_reply"&&ReadString(data,"text") is {Length:>0} reply)
                            _recentMessages.Enqueue(reply);
                        else if(kind=="tool_selected"&&ReadString(data,"name")=="ask_user")
                            _latestQuestion=ReadString(data.GetProperty("arguments"),"question");
                        while(_recentMessages.Count>3)_recentMessages.Dequeue();
                    }catch(Exception ex)when(ex is JsonException or KeyNotFoundException){}
                }
                _logPosition=stream.Position;
            }
            var messages=new List<string>();
            if(_lastPlayerMessage!="")messages.Add("你："+_lastPlayerMessage);
            messages.AddRange(_recentMessages.Select(message=>"Agent："+message));
            if(_latestQuestion!="")messages.Add("需要你决定："+_latestQuestion);
            _conversation.Text=messages.Count==0?"尚无问答":string.Join(Environment.NewLine+Environment.NewLine,messages);
        }catch(IOException){ }
    }

    private static string ReadString(JsonElement element,string property)=>
        element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String
            ?value.GetString()??"": "";

    private void UpdateLinks()
    {
        _folder.IsEnabled=RunRoot!=null&&Directory.Exists(RunRoot);
        _result.IsEnabled=RunRoot!=null&&File.Exists(Path.Combine(RunRoot,"summer-01-results.json"));
    }

    private void Open(string? path)
    {
        if(path==null)return;
        try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true})?.Dispose();}
        catch(System.ComponentModel.Win32Exception ex){_activity.Text="无法打开文件。"+ex.Message;}
    }

    private async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs args)
    {
        if(_closed)return;
        args.Cancel=true;
        if(_closing)return;
        if(_session is {Connected:true}&&!_completed&&MessageBox.Show(this,
            "关闭窗口会停止本轮自主经营。已经写入的原版存档和日志会保留。",
            "结束本轮测试？",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        _closing=true;
        _stop.Cancel();
        _poll.Stop();
        try{
            if(_launch!=null)await _launch;
            if(_session!=null){
                await _session.Pause();
                await _session.DisposeAsync();
            }
        }
        catch(Exception ex){Debug.WriteLine(ex);}
        finally{
            _observer.Dispose();
            _stop.Dispose();
            _closed=true;
            Close();
        }
    }
}
