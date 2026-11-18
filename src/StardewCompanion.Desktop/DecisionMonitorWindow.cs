using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using StardewCompanion.Protocol;

namespace StardewCompanion.Desktop;

internal sealed class DecisionMonitorWindow:Window
{
    private sealed record Row(long Sequence,string Time,string Kind,string Summary,string Detail);
    private readonly ObservableCollection<Row> _rows=new();
    private readonly Dictionary<string,Row> _progressRows=new();
    private readonly DecisionInteractionView _interactions=new();
    private readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(4)};
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly TextBlock _state=new(){FontSize=17,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _connection=new(){Foreground=Brushes.LightGreen,Margin=new(0,10,0,0)};
    private readonly TextBlock _counts=new(){Foreground=Brushes.LightGray,Margin=new(0,8,0,0)};
    private readonly TextBlock _popup=new(){Foreground=Brushes.Orange,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0),MaxHeight=72};
    private readonly TextBlock _awareness=new(){Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,MaxHeight=100,Margin=new(0,8,0,0)};
    private readonly TextBox _awarenessDetails=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),FontSize=13,Padding=new(12)};
    private readonly TextBox _inputPreview=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),FontSize=13,Padding=new(12)};
    private readonly TextBox _goalObjective=new(){AcceptsReturn=true,MinHeight=100,TextWrapping=TextWrapping.Wrap};
    private readonly TextBox _goalCompletion=new(),_goalPriorities=new(){AcceptsReturn=true,MinHeight=60},_goalRestrictions=new(){AcceptsReturn=true,MinHeight=60},_goalAutonomy=new(){TextWrapping=TextWrapping.Wrap},_goalDisabled=new(){AcceptsReturn=true,MinHeight=45};
    private readonly TextBlock _goalStatus=new(){Margin=new(0,8,0,0),TextWrapping=TextWrapping.Wrap};
    private readonly CheckBox _replaceObjective=new(){Content="替换当前工作目标（仅补充说明时可取消勾选）",IsChecked=true,Margin=new(0,12,0,0)};
    private string _goalScope="";
    private int _goalVersion;
    private bool _goalDirty,_loadingGoal;
    private readonly TextBox _plans=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Microsoft YaHei UI"),FontSize=14,Padding=new(12)};
    private readonly PlanPreviewPanel _seasonPreview=new();
    private readonly CheckBox _follow=new(){Content="跟随最新记录",IsChecked=true,Margin=new(8),VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBox _details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),FontSize=13,Padding=new(12),BorderThickness=new(0)};
    private readonly TextBox _message=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=58,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,ToolTip="输入消息或回答 AI；Enter 发送，Shift+Enter 换行"};
    private readonly TextBlock _messageStatus=new(){Text="发送消息或回答 AI；长期目标在“长期目标”页修改。",Foreground=Brushes.DimGray,VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,0,0)};
    private readonly DataGrid _grid=new(){IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeight=33,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,BorderThickness=new(0)};
    private long _after;
    private bool _loading;
    private bool _closed;
    private int _requests,_planCount,_finished,_failures;
    private readonly Uri _base;
    private static readonly JsonSerializerOptions Pretty=new(){WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public DecisionMonitorWindow(string sessionFile)
    {
        Title="StardewCompanion · LLM 交互调试";Width=1440;Height=940;MinWidth=1100;MinHeight=760;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var session=JsonNode.Parse(File.ReadAllText(sessionFile))!;
        var ws=new Uri(session["endpoint"]!.GetValue<string>());
        if(!ws.IsLoopback)throw new InvalidOperationException("监控仅连接本机服务");
        _base=new UriBuilder("http",ws.Host,ws.Port).Uri;
        _http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session["token"]!.GetValue<string>());
        var root=new DockPanel{Background=new SolidColorBrush(Color.FromRgb(241,245,249))};Content=root;
        var top=new StackPanel{Background=new SolidColorBrush(Color.FromRgb(20,35,53)),Margin=new(0)};
        var header=new StackPanel{Margin=new(20,12,20,12)};
        header.Children.Add(new TextBlock{Text="LLM 交互调试 · 输入 → 回复 → 工具 → 实际执行",Foreground=Brushes.White,FontSize=21,FontWeight=FontWeights.SemiBold});
        header.Children.Add(_state);header.Children.Add(_connection);header.Children.Add(_counts);header.Children.Add(_popup);header.Children.Add(_awareness);top.Children.Add(header);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var toolbar=new WrapPanel{Margin=new(12,4,12,4)};
        _follow.Content="跟随原始事件";toolbar.Children.Add(_follow);
        foreach(var control in new[]{("暂停","pause"),("继续","resume")}){
            var button=new Button{Content=control.Item1,Padding=new(10,5,10,5),Margin=new(5)};
            button.Click+=async(_,_)=>{try{using var response=await _http.PostAsJsonAsync(new Uri(_base,"agent/"+control.Item2),new{});await RequireSuccess(response);await Poll();}catch(Exception ex){_connection.Text="控制失败："+ex.Message;}};
            toolbar.Children.Add(button);
        }
        foreach(var mode in new[]{("仅规划","plan_only"),("自主规划执行","autonomous")}){
            var button=new Button{Content=mode.Item1,Padding=new(10,5,10,5),Margin=new(5)};
            button.Click+=async(_,_)=>{try{using var response=await _http.PostAsJsonAsync(new Uri(_base,"agent/mode"),new{mode=mode.Item2});await RequireSuccess(response);await Poll();}catch(Exception ex){_connection.Text="模式切换失败："+ex.Message;}};
            toolbar.Children.Add(button);
        }
        var copy=new Button{Content="复制选中记录",Padding=new(12,5,12,5),Margin=new(8)};copy.Click+=(_,_)=>{if(_grid.SelectedItem is Row row)Clipboard.SetText(row.Detail);};toolbar.Children.Add(copy);
        toolbar.Children.Add(new TextBlock{Text="主视图按决策分组；原始事件保留完整日志",VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.DimGray,Margin=new(10,0,0,0)});
        DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var body=new Grid{Margin=new(12,0,12,12)};
        body.RowDefinitions.Add(new(){Height=new GridLength(3,GridUnitType.Star)});body.RowDefinitions.Add(new(){Height=new GridLength(5)});body.RowDefinitions.Add(new(){Height=new GridLength(2,GridUnitType.Star)});
        _grid.Columns.Add(new DataGridTextColumn{Header="时间",Binding=new Binding(nameof(Row.Time)),Width=90});
        _grid.Columns.Add(new DataGridTextColumn{Header="类型",Binding=new Binding(nameof(Row.Kind)),Width=125});
        _grid.Columns.Add(new DataGridTextColumn{Header="公开回复 / 计划 / 实际结果",Binding=new Binding(nameof(Row.Summary)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        _grid.ItemsSource=_rows;_grid.SelectionChanged+=(_,_)=>{if(_grid.SelectedItem is Row row)_details.Text=row.Detail;};
        body.Children.Add(_grid);var split=new GridSplitter{Height=5,HorizontalAlignment=HorizontalAlignment.Stretch};Grid.SetRow(split,1);body.Children.Add(split);
        Grid.SetRow(_details,2);body.Children.Add(_details);
        var tabs=new TabControl();tabs.Items.Add(new TabItem{Header="模型交互",Content=_interactions});tabs.Items.Add(new TabItem{Header="原始事件",Content=body});tabs.Items.Add(new TabItem{Header="规划 · 成果 · 待办",Content=_plans});tabs.Items.Add(new TabItem{Header="反馈 · 阻塞 · 事件",Content=_awarenessDetails});
        tabs.Items.Add(new TabItem{Header="长期目标",Content=GoalEditor()});
        tabs.Items.Add(new TabItem{Header="季计划预览",Content=_seasonPreview});
        tabs.Items.Add(new TabItem{Header="最近模型输入",Content=_inputPreview});
        var composer=new DockPanel{Margin=new(12,0,12,12)};
        var send=new Button{Content="发送给 AI",Padding=new(14,8,14,8),MinWidth=110,Margin=new(0,0,0,0)};
        send.Click+=async(_,_)=>await SendMessage();DockPanel.SetDock(send,Dock.Right);composer.Children.Add(send);
        var composeBody=new DockPanel{Margin=new(0,0,10,0)};DockPanel.SetDock(_messageStatus,Dock.Top);composeBody.Children.Add(_messageStatus);DockPanel.SetDock(_message,Dock.Bottom);composeBody.Children.Add(_message);composer.Children.Add(composeBody);
        _message.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Shift)==0){e.Handled=true;_ = SendMessage();}};
        DockPanel.SetDock(composer,Dock.Bottom);root.Children.Add(composer);root.Children.Add(tabs);
        _timer.Tick+=async(_,_)=>await Poll();
        Loaded+=async(_,_)=>{await Poll();_timer.Start();};
        Closed+=(_,_)=>{_closed=true;_timer.Stop();_http.Dispose();};
    }
    private static JsonNode? Get(JsonNode? node,string key)=>node is JsonObject o?o.FirstOrDefault(p=>p.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).Value:null;
    private UIElement GoalEditor()
    {
        var panel=new StackPanel{Margin=new(16)};
        foreach(var field in new[]{("目标原文",_goalObjective),("完成条件",_goalCompletion),("优先事项（每行一项）",_goalPriorities),("任务限制（每行一项）",_goalRestrictions),("自主行动范围",_goalAutonomy),("额外关闭的技能标识（每行一项；矿洞固定关闭）",_goalDisabled)}){
            panel.Children.Add(new TextBlock{Text=field.Item1,Margin=new(0,10,0,4)});panel.Children.Add(field.Item2);
            field.Item2.TextChanged+=(_,_)=>{if(!_loadingGoal)_goalDirty=true;};
        }
        panel.Children.Add(_replaceObjective);
        var save=new Button{Content="更新长期目标",Padding=new(12,7,12,7),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,14,0,0)};
        save.Click+=async(_,_)=>{
            static string[] Lines(string text)=>text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            try{using var response=await _http.PostAsJsonAsync(new Uri(_base,"agent/goal"),new{objective=_goalObjective.Text,completionCondition=_goalCompletion.Text,
                priorities=Lines(_goalPriorities.Text),restrictions=Lines(_goalRestrictions.Text),autonomy=_goalAutonomy.Text,disabledSkills=Lines(_goalDisabled.Text),expectedVersion=_goalVersion,replaceObjective=_replaceObjective.IsChecked==true});
                if(!response.IsSuccessStatusCode)throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
                var saved=JsonNode.Parse(await response.Content.ReadAsStringAsync());_goalVersion=Get(saved,"version")?.GetValue<int>()??_goalVersion;_goalDirty=false;
                _goalStatus.Text="长期目标已保存（版本 "+_goalVersion+"）。季计划需匹配此版本；点击“仅规划”可先生成预览，点击“继续”将在季计划保存后执行。";
            }catch(Exception ex){_goalStatus.Text="更新失败："+ex.Message;}
        };
        panel.Children.Add(save);
        var reload=new Button{Content="重新加载目标",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,8,0,0)};
        reload.Click+=async(_,_)=>{_goalScope="";_goalDirty=false;await Poll();};panel.Children.Add(reload);
        panel.Children.Add(_goalStatus);return new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private static string Text(JsonNode? n,string key)=>Get(n,key)?.ToString()??"";
    private static async Task RequireSuccess(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        var body=await response.Content.ReadAsStringAsync();
        try{throw new InvalidOperationException(JsonNode.Parse(body)?["error"]?.ToString()??body);}
        catch(JsonException){throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{body}");}
    }
    private static JsonNode Readable(JsonNode original)
    {
        var node=original.DeepClone();
        void Expand(JsonNode? item){
            if(item is JsonObject obj)foreach(var key in obj.Select(p=>p.Key).ToArray()){
                if(key.Equals("details",StringComparison.OrdinalIgnoreCase)||key.Equals("arguments",StringComparison.OrdinalIgnoreCase)){
                    if(obj[key] is JsonValue v&&v.TryGetValue<string>(out var text)&&text.TrimStart().StartsWithAnyJson()){
                        try{obj[key]=JsonNode.Parse(text);}catch(JsonException){}
                    }
                }
                Expand(obj[key]);
            }
            else if(item is JsonArray array)foreach(var child in array)Expand(child);
        }
        Expand(node);return node;
    }
    private async Task Poll()
    {
        if(_loading||_closed)return;_loading=true;
        try
        {
            var state=JsonNode.Parse(await _http.GetStringAsync(new Uri(_base,"agent/state")))!;
            var scope=Text(state,"scope");
            var version=Get(state,"goalVersion")?.GetValue<int>()??0;
            if(scope!=""&&(scope!=_goalScope||(!_goalDirty&&version!=_goalVersion))){
                var goal=JsonNode.Parse(await _http.GetStringAsync(new Uri(_base,"agent/goal")));
                _loadingGoal=true;
                _goalObjective.Text=Text(goal,"objective");_goalCompletion.Text=Text(goal,"completionCondition");_goalAutonomy.Text=Text(goal,"autonomy");
                _goalPriorities.Text=string.Join(Environment.NewLine,(Get(goal,"priorities") as JsonArray??new()).Select(v=>v?.ToString()));
                _goalRestrictions.Text=string.Join(Environment.NewLine,(Get(goal,"restrictions") as JsonArray??new()).Select(v=>v?.ToString()));
                _goalDisabled.Text=string.Join(Environment.NewLine,(Get(goal,"disabledSkills") as JsonArray??new()).Select(v=>v?.ToString()));_goalScope=scope;_goalVersion=Get(goal,"version")?.GetValue<int>()??version;_loadingGoal=false;_goalDirty=false;
            }
            var input=JsonNode.Parse(await _http.GetStringAsync(new Uri(_base,"agent/input")));
            _inputPreview.Text=input?.ToJsonString(Pretty)??"等待首次决策";
            var awareness=Get(state,"awareness");
            var feedback=Get(awareness,"actionFeedback");
            var delivered=Text(feedback,"revision")==Text(feedback,"deliveredRevision");
            _awareness.Text=$"最近反馈：{(delivered?"模型已收到":"待送入模型")} · 当前阻塞 {(Get(feedback,"blockers") as JsonArray)?.Count??0} · 待送达事件 {(Get(awareness,"pendingEvents") as JsonArray)?.Count??0}（详见反馈页）";
            _awarenessDetails.Text=awareness?.ToJsonString(Pretty)??"等待游戏连接";
            var plans=JsonNode.Parse(await _http.GetStringAsync(new Uri(_base,"agent/plans")));
            _plans.Text=plans?.ToJsonString(Pretty)??"尚无工作规划";
            var planning=JsonNode.Parse(await _http.GetStringAsync(new Uri(_base,"agent/planning")));
            _seasonPreview.Update(planning);
            var o=Get(state,"observation");
            var observation=o?.Deserialize<GameObservation>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            _connection.Foreground=Brushes.LightGreen;
            _state.Text=observation==null?"等待游戏连接":$"第 {observation.GameDay+1} 天   {observation.TimeOfDay/100:00}:{observation.TimeOfDay%100:00}    {observation.Location} ({observation.Tile.X},{observation.Tile.Y})    体力 {observation.Stamina:0.#}    生命 {observation.Health}    {(observation.Paused?"已暂停":"运行中")}";
            _connection.Text=(Text(state,"mode")=="plan_only"?"仅规划 · ":"自主规划执行 · ")+Text(state,"status")+"  ·  "+(Text(state,"thinking")=="true"?"LLM 正在返回决策":"本地执行 / 等待下一轮")+"  ·  "+DateTime.Now.ToString("HH:mm:ss");
            try{
                var popup=string.IsNullOrEmpty(observation?.MenuContext)?null:JsonNode.Parse(observation.MenuContext);
                var popupText=Text(popup,"text");var choices=Get(popup,"choices") as JsonArray;
                _popup.Text=popupText==""?"":$"游戏弹窗：{popupText}"+(choices is {Count:>0}?"  选项："+string.Join(" / ",choices.Select(c=>Text(c,"responseKey")+"="+Text(c,"text"))):"");
            }catch(JsonException){_popup.Text="游戏弹窗文本暂不可读";}
            for(var page=0;page<4;page++){
                var batch=JsonNode.Parse(await _http.GetStringAsync(new Uri(_base,$"agent/events?after={_after}&limit=200")))!;
                var events=Get(batch,"events")!.AsArray();if(events.Count==0)break;
                foreach(var e in events){
                    var sequence=Get(e,"sequence")!.GetValue<long>();if(sequence<=_after)continue;
                    if(sequence>_after+1){_interactions.Log.HistoryGap=true;_connection.Text="部分较早事件已移入磁盘日志；以下显示当前可用记录";}
                    _after=sequence;Add(e!);
                }
                if(events.Count<200)break;
            }
            _interactions.Refresh();
            _counts.Text=$"本次载入：模型请求 {_requests}   计划 {_planCount}   已结束 {_finished}   失败/受阻 {_failures}   最新事件 #{_after}   每秒刷新";
        }
        catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException){
            if(!_closed){_connection.Text="连接中断，保留已载入操作流并自动重试 · "+ex.GetType().Name;_connection.Foreground=Brushes.Orange;}
        }
        finally{_loading=false;}
    }
    private void Add(JsonNode e)
    {
        _interactions.Accept(e);
        var replaceSelected=false;
        var kind=Text(e,"kind");var data=Get(e,"data");var summary="";var label=kind;
        switch(kind){
            case "context_compression":label="上下文压缩";summary=Text(data,"originalCharacters")+" → "+Text(data,"characters")+" 字符 · "+(Text(data,"overBudget")=="true"?"关键内容超出软预算，完整保留":"预算内");break;
            case "model_request":_requests++;label="请求模型";summary="请求 "+Text(data,"model");break;
            case "decision_context":label="模型输入";summary=Text(Get(data,"trigger"),"reason");break;
            case "offline_chat_context":label="离线聊天输入";summary=Text(data,"currentMessage");break;
            case "model_prompt_capture":label="完整请求捕获";summary="第 "+((Get(data,"round")?.GetValue<int>()??0)+1)+" 轮 · "+Text(data,"requestId");break;
            case "tool_selected":label="选择工具";summary=Text(data,"name")+" · "+Text(data,"arguments");break;
            case "tool_result":label="工具返回";summary=Text(data,"name")+" · "+Text(Get(data,"result"),"status")+" · "+Text(Get(data,"result"),"reason");break;
            case "decision_finished":label="模型交互结束";summary="本次 Think 已结束；游戏任务可能仍在执行";break;
            case "decision_cancelled":case "offline_chat_cancelled":label="决策取消";summary=Text(data,"reason");break;
            case "no_action_retry_scheduled":label="等待重新规划";summary=Text(data,"delaySeconds")+" 秒后重试";break;
            case "model_reply":
                label="模型回复";summary=Text(data,"text");
                if(Get(data,"calls") is JsonArray calls&&calls.Count>0)summary+="  → "+string.Join(", ",calls.Select(c=>Text(Get(c,"function"),"name")));
                summary+="  ["+Text(data,"elapsedMs")+" ms]";break;
            case "plan_started":_planCount++;label="计划开始";summary=Text(data,"planId")+" · "+(Get(data,"steps") as JsonArray)?.Count+" 步";break;
            case "plan_finished":_finished++;label="计划结束";summary=Text(data,"planId")+" · "+Text(data,"status")+" "+Text(data,"reason");if(Text(data,"status")!="completed")_failures++;break;
            case "task_dispatch":label="执行技能";summary=Text(data,"skill")+" · "+Text(data,"arguments")+" · 来源 "+Text(data,"source");break;
            case "task_result":
                if(Text(data,"status")=="accepted"){label="任务已接收";summary=Text(data,"skill")+" · accepted（尚未完成）";break;}
                if(Text(data,"status")=="progress"){
                    var taskId=Text(data,"taskId");var detail=Readable(data!).ToJsonString(Pretty);
                    var progressRow=new Row(Get(e,"sequence")!.GetValue<long>(),DateTimeOffset.TryParse(Text(e,"at"),out var progressAt)?progressAt.LocalDateTime.ToString("HH:mm:ss"):Text(e,"at"),"操作进度",Text(data,"skill")+" · "+Text(data,"reason")+" · 数量 "+Text(data,"completed"),detail);
                    if(_progressRows.TryGetValue(taskId,out var prior))_rows.Remove(prior);
                    _progressRows[taskId]=progressRow;_rows.Add(progressRow);TrimRows();
                    if(_follow.IsChecked==true){_grid.SelectedItem=progressRow;_grid.ScrollIntoView(progressRow);}return;
                }
                if(_progressRows.Remove(Text(data,"taskId"),out var progress)){replaceSelected=ReferenceEquals(_grid.SelectedItem,progress);_rows.Remove(progress);}
                if(Text(data,"status") is "failed" or "partial" or "rejected")_failures++;
                label="实际回执";summary=Text(data,"skill")+" · "+Text(data,"status")+" · 数量 "+Text(data,"completed")+" · "+Text(data,"reason");break;
            case "day_changed":label="自然换日";summary="进入游戏日期 "+Text(data,"day");break;
            case "tool_rejected":case "model_error":case "offline_chat_error":label="错误";summary=data?.ToJsonString()??"";_failures++;break;
            case "control":label="控制";summary=Text(data,"action");break;
            case "planning_saved":label="规划修订";summary=Text(data,"goal")+" · 版本 "+Text(data,"revision");break;
            case "player_message":label="玩家指令";summary=Text(data,"text");break;
            case "mode_changed":label="模式切换";summary=Text(data,"mode");break;
            case "trial_completed":label="测试结束";summary=data?.ToJsonString()??"";break;
            default:return;
        }
        var at=DateTimeOffset.TryParse(Text(e,"at"),out var parsed)?parsed.LocalDateTime.ToString("HH:mm:ss"):Text(e,"at");
        var row=new Row(Get(e,"sequence")!.GetValue<long>(),at,label,summary.Replace("\r"," ").Replace("\n"," "),Readable(e).ToJsonString(Pretty));
        _rows.Add(row);TrimRows();
        if(_follow.IsChecked==true||replaceSelected){_grid.SelectedItem=row;_grid.ScrollIntoView(row);}
    }
    private void TrimRows()
    {
        while(_rows.Count>1500){
            var old=_rows[0];_rows.RemoveAt(0);
            foreach(var key in _progressRows.Where(p=>ReferenceEquals(p.Value,old)).Select(p=>p.Key).ToArray())_progressRows.Remove(key);
        }
    }
    private async Task SendMessage()
    {
        var text=_message.Text.Trim();if(text.Length==0)return;
        try{
            using var response=await _http.PostAsJsonAsync(new Uri(_base,"agent/message"),new{text});
            if(!response.IsSuccessStatusCode){var body=await response.Content.ReadAsStringAsync();throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");}
            _message.Clear();_messageStatus.Text="消息已发送，将结合当前工作状态处理。";_message.Focus();
        }catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException){_messageStatus.Text="发送失败："+ex.Message;}
    }
}

internal static class MonitorJsonText
{
    internal static bool StartsWithAnyJson(this string text)=>text.StartsWith("{")||text.StartsWith("[");
}
