using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using static StardewCompanion.Desktop.InteractionJson;

namespace StardewCompanion.Desktop;

internal sealed class DecisionInteractionView:UserControl
{
    internal DecisionInteractionLog Log {get;}=new();
    private readonly ListBox _decisions=new(){Name="DecisionList",BorderThickness=new(0),HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private readonly ComboBox _request=new(){Name="RequestSelector",MinWidth=240,DisplayMemberPath="Label",Margin=new(0,0,10,0)};
    private readonly ComboBox _filter=new(){Width=150,Margin=new(8,0,0,0)};
    private readonly TextBox _search=new(){Width=200,Margin=new(8,0,0,0),ToolTip="搜索触发原因、模型、决策ID或工具名称"};
    private readonly CheckBox _follow=new(){Content="跟随最新决策",IsChecked=true,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _title=new(){Text="等待首次模型交互",FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _meta=new(){Text="连接后会按决策归组；选择一轮请求可查看当时实际发送的内容。",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0),Foreground=Brushes.DimGray};
    private readonly TextBlock _flow=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0),FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(25,85,125))};
    private readonly TextBlock _notice=new(){Foreground=Brushes.DarkOrange,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,4)};
    private readonly StackPanel _input=new(),_conversation=new(),_execution=new();
    private readonly TextBox _raw=JsonBox("尚未捕获模型请求");
    private readonly TabControl _tabs=new(){Margin=new(0,8,0,0)};
    private readonly System.ComponentModel.ICollectionView _items;
    private bool _refreshing,_dirty;
    private bool _followRequest=true;
    private string? _renderedKey;
    private long _renderedSequence;
    internal DecisionInteraction? Selected=>_decisions.SelectedItem as DecisionInteraction;
    internal InteractionRequest? SelectedRequest=>_request.SelectedItem as InteractionRequest;

    internal DecisionInteractionView()
    {
        FontFamily=new("Microsoft YaHei UI");FontSize=13;
        var root=new DockPanel{Margin=new(12)};Content=root;
        var toolbar=new WrapPanel{Margin=new(0,0,0,10)};
        toolbar.Children.Add(_follow);
        _filter.ItemsSource=new[]{"全部决策","含失败 / 拒绝","季节维护"};_filter.SelectedIndex=0;toolbar.Children.Add(_filter);
        toolbar.Children.Add(new TextBlock{Text="搜索",VerticalAlignment=VerticalAlignment.Center,Margin=new(12,0,0,0)});toolbar.Children.Add(_search);
        var reset=new Button{Content="回到最新",Margin=new(8,0,0,0),Padding=new(10,3,10,3)};
        reset.Click+=(_,_)=>{_search.Clear();_filter.SelectedIndex=0;_follow.IsChecked=true;_dirty=true;Refresh();};toolbar.Children.Add(reset);
        DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new(310),MinWidth=240});body.ColumnDefinitions.Add(new(){Width=new(8)});body.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star),MinWidth=450});root.Children.Add(body);
        var left=new DockPanel{Background=Brushes.White};
        var hint=new TextBlock{Text="决策列表 · 最新在上\n点击历史决策后停止自动跟随",Foreground=Brushes.DimGray,Margin=new(10),TextWrapping=TextWrapping.Wrap};DockPanel.SetDock(hint,Dock.Top);left.Children.Add(hint);
        _items=CollectionViewSource.GetDefaultView(Log.Decisions);_items.Filter=item=>Matches((DecisionInteraction)item);_decisions.ItemsSource=_items;
        _decisions.ItemTemplate=DecisionTemplate();
        var itemStyle=new Style(typeof(ListBoxItem));itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));itemStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));itemStyle.Setters.Add(new Setter(Control.MarginProperty,new Thickness(0,0,0,4)));
        var selected=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(221,237,250))));selected.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));itemStyle.Triggers.Add(selected);_decisions.ItemContainerStyle=itemStyle;
        left.Children.Add(_decisions);body.Children.Add(left);
        var splitter=new GridSplitter{Width=5,HorizontalAlignment=HorizontalAlignment.Center,Background=Brushes.LightGray};Grid.SetColumn(splitter,1);body.Children.Add(splitter);
        var detail=new DockPanel{Background=Brushes.White,Margin=new(4,0,0,0)};Grid.SetColumn(detail,2);body.Children.Add(detail);
        var header=new StackPanel{Margin=new(14,12,14,0)};header.Children.Add(_title);header.Children.Add(_meta);header.Children.Add(_flow);header.Children.Add(_notice);
        var requestBar=new WrapPanel{Margin=new(0,8,0,0)};requestBar.Children.Add(_request);
        var copy=new Button{Content="复制完整请求",Padding=new(10,3,10,3)};copy.Click+=(_,_)=>{
            if(SelectedRequest==null)return;
            try{Clipboard.SetText(Format(SelectedRequest.Prompt));_notice.Text="已复制所选轮次的完整请求 JSON。";}
            catch(System.Runtime.InteropServices.COMException){_notice.Text="剪贴板暂时忙碌，请重试。";}
        };requestBar.Children.Add(copy);header.Children.Add(requestBar);DockPanel.SetDock(header,Dock.Top);detail.Children.Add(header);
        _tabs.Items.Add(new TabItem{Header="① 发送给模型",Content=Scroll(_input)});
        _tabs.Items.Add(new TabItem{Header="② 回复与工具",Content=Scroll(_conversation)});
        _tabs.Items.Add(new TabItem{Header="③ 实际执行",Content=Scroll(_execution)});
        _tabs.Items.Add(new TabItem{Header="原始请求 JSON",Content=_raw});detail.Children.Add(_tabs);
        _decisions.SelectionChanged+=(_,_)=>{if(!_refreshing){_follow.IsChecked=false;RenderDecision();}};
        _request.SelectionChanged+=(_,_)=>{if(!_refreshing){_followRequest=false;RenderRequest();}};
        _search.TextChanged+=(_,_)=>{_dirty=true;Refresh();};_filter.SelectionChanged+=(_,_)=>{_dirty=true;Refresh();};
    }

    private bool Matches(DecisionInteraction decision)=>(_filter.SelectedIndex!=1||decision.HasIssue)&&(_filter.SelectedIndex!=2||decision.Maintenance)
        &&(_search.Text.Trim()==""||decision.SearchText.Contains(_search.Text.Trim(),StringComparison.OrdinalIgnoreCase));
    private static DataTemplate DecisionTemplate()
    {
        var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(MarginProperty,new Thickness(10));
        foreach(var (path,size,bold) in new[]{("Time",12.0,false),("Title",14.0,true),("Focus",12.0,false),("Status",13.0,true),("Metrics",12.0,false),("Model",12.0,false)}){
            var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(path));text.SetValue(TextBlock.FontSizeProperty,size);
            text.SetValue(TextBlock.TextWrappingProperty,path=="Focus"?TextWrapping.NoWrap:TextWrapping.Wrap);text.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);text.SetValue(MarginProperty,new Thickness(0,0,0,4));if(bold)text.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);panel.AppendChild(text);
        }
        return new DataTemplate{VisualTree=panel};
    }
    private static ScrollViewer Scroll(UIElement content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new(14)};
    private static TextBox JsonBox(string text,double maxHeight=double.PositiveInfinity)=>new(){Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Consolas"),FontSize=12,Padding=new(9),MaxHeight=maxHeight,BorderBrush=new SolidColorBrush(Color.FromRgb(219,227,235)),Background=new SolidColorBrush(Color.FromRgb(247,250,253))};
    private static void Note(Panel target,string text)=>target.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray,Margin=new(0,0,0,10)});
    private static void Section(Panel target,string title,string content,bool expanded=false)=>target.Children.Add(new Expander{Header=title,IsExpanded=expanded,Margin=new(0,0,0,10),Content=JsonBox(content,300)});
    private static void Card(Panel target,string title,string content,string? raw=null)
    {
        var panel=new StackPanel{Margin=new(12)};panel.Children.Add(new TextBlock{Text=title,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(24,79,116)),TextWrapping=TextWrapping.Wrap});
        if(content!="")panel.Children.Add(new TextBox{Text=content,IsReadOnly=true,BorderThickness=new(0),TextWrapping=TextWrapping.Wrap,Background=Brushes.Transparent,Margin=new(0,7,0,0)});
        if(raw!=null)Section(panel,"展开参数 / 完整数据",raw);
        target.Children.Add(new Border{BorderBrush=new SolidColorBrush(Color.FromRgb(219,227,235)),BorderThickness=new(1),CornerRadius=new(6),Margin=new(0,0,0,10),Child=panel});
    }

    internal void Accept(JsonNode entry){Log.Add(entry);_dirty=true;}
    internal void Refresh()
    {
        if(!_dirty)return;_dirty=false;
        var before=Selected;_refreshing=true;
        try{
            _items.Refresh();
            if(_follow.IsChecked==true||Selected==null)_decisions.SelectedItem=_items.Cast<DecisionInteraction>().FirstOrDefault();
        }finally{_refreshing=false;}
        var current=Selected;var sequence=current?.Events.LastOrDefault() is {} e?long.Parse(Text(e,"sequence")):0;
        if(current!=before||current?.Key!=_renderedKey||sequence!=_renderedSequence)RenderDecision();
    }
    private void RenderDecision()
    {
        var decision=Selected;if(_renderedKey!=decision?.Key)_followRequest=true;
        _renderedKey=decision?.Key;_renderedSequence=decision?.Events.LastOrDefault() is {} e?long.Parse(Text(e,"sequence")):0;
        if(decision==null){_title.Text="没有匹配的决策";_meta.Text="可清空搜索或切换筛选。";_flow.Text="";_notice.Text="";_request.ItemsSource=null;_input.Children.Clear();_conversation.Children.Clear();_execution.Children.Clear();_raw.Text="暂无请求";return;}
        _title.Text=decision.Title;
        _meta.Text=$"{decision.Status} · {decision.Model} · {decision.Metrics}"+(decision.Focus==""?"":"\n当前目标："+decision.Focus)+$"\n决策 {decision.Id}\n存档 {decision.Scope} · 会话 {decision.Session}";
        _flow.Text=$"输入 {(decision.Requests.Count>0?"已捕获":"待捕获")}  →  模型回复 {decision.Requests.Count(r=>r.Reply!=null)} 次  →  工具调用 {decision.ToolCount} 个  →  {decision.Status}";
        var partial=Log.HistoryGap||!decision.HasContext||decision.EventsTruncated;
        _notice.Foreground=partial?Brushes.DarkOrange:Brushes.DimGray;
        _notice.Text=partial?"当前记录可能从中途载入或部分已归档；缺失内容不代表模型没有收到。":"所选轮次来自实际请求捕获，可与原始 JSON 逐项核对。";
        var previous=SelectedRequest?.Id;_refreshing=true;
        try{
            _request.ItemsSource=null;_request.ItemsSource=decision.Requests;
            _request.SelectedItem=(_followRequest?null:decision.Requests.FirstOrDefault(r=>r.Id==previous))??decision.Requests.LastOrDefault();
        }finally{_refreshing=false;}
        _execution.Children.Clear();
        Note(_execution,"这里展示原生执行与批次汇总。模型回复、工具 accepted 和 decision_finished 都不等于游戏动作已完成。");
        var execution=decision.Events.Where(e=>Text(e,"kind") is "task_dispatch" or "task_result" or "plan_started" or "plan_finished" or "permission_allowed").ToArray();
        // Keep the latest receipt for each task; progress is replaced by its terminal receipt.
        var lastReceipt=execution.Where(e=>Text(e,"kind")=="task_result").GroupBy(e=>Text(Get(e,"data"),"taskId")).ToDictionary(g=>g.Key,g=>g.Last());
        foreach(var item in execution){
            var kind=Text(item,"kind");var data=Get(item,"data");
            if(kind=="task_result"&&lastReceipt[Text(data,"taskId")]!=item)continue;
            var label=kind switch{"task_dispatch"=>"派发原生动作","task_result"=>"原生回执","plan_started"=>"批次开始","plan_finished"=>"批次结束",_=>"权限检查通过"};
            var identity=string.Join("  ",new[]{Text(data,"taskId")==""?"":"任务 "+Text(data,"taskId"),Text(data,"planId")==""?"":"批次 "+Text(data,"planId")}.Where(s=>s!=""));
            Card(_execution,string.Join(" · ",new[]{label,Text(data,"skill"),Text(data,"status")}.Where(s=>s!="")),
                identity+"\n"+Text(data,"reason")+(kind=="task_result"?$"\n实际数量 {Text(data,"completed")}":""),Format(data));
        }
        if(execution.Length==0)Note(_execution,"本次尚无原生动作记录；可能仅查询、等待、提问，或记录尚未载入。");
        RenderRequest();
    }
    private void RenderRequest()
    {
        _input.Children.Clear();_conversation.Children.Clear();var request=SelectedRequest;var decision=Selected;
        if(request==null||decision==null){Note(_input,"尚无此决策的完整请求捕获。可在“原始事件”中检查已有上下文和错误。");Note(_conversation,"尚无模型请求记录。");_raw.Text="尚未捕获完整请求";return;}
        _raw.Text=Format(request.Prompt);
        Note(_input,$"所选请求 {request.Id} · 第 {request.Round+1} 轮 · 发送 {request.Attempts} 次。此处读取该次请求捕获，不使用不断变化的“最近模型输入”。");
        var messages=Get(request.Prompt,"messages") as JsonArray??new();
        foreach(var message in messages.Where(m=>Text(m,"role")=="system"))Section(_input,"System · 固定指令",Text(message,"content"));
        var user=messages.FirstOrDefault(m=>Text(m,"role")=="user");var context=Decode(Get(user,"content"));
        if(context is JsonObject parts){
            var names=new Dictionary<string,string>{{"trigger","触发原因"},{"playerGoal","玩家目标与授权"},{"conversation","玩家消息与待答问题"},{"agentState","当前工作、成果与阻塞"},{"world","当前世界与预计算摘要"},{"recentEvents","最近事件"},{"activeProjects","长期项目"},{"candidateActions","候选行动"},{"relevantMemories","相关记忆与约定"},{"decisionScope","本轮技能与查询范围"}};
            foreach(var (key,value) in parts)Section(_input,(names.GetValueOrDefault(key)??key)+" · "+key,Format(value),key is "trigger" or "playerGoal");
        }else Section(_input,"User · 输入内容",Text(user,"content"),true);
        var tools=Get(request.Prompt,"tools") as JsonArray??new();
        Section(_input,$"本次提供的工具 · {tools.Count} 项",Format(tools));
        Note(_conversation,"先展示本轮公开回复及本地工具返回；实际发出的消息序列可在底部展开。游戏结果以“实际执行”页为准。");
        var callNames=new Dictionary<string,string>();
        void Message(JsonNode? message,Panel target,bool response=false){
            var role=Text(message,"role");var content=Text(message,response?"text":"content");
            if(role=="system"){Card(target,"Core → 模型 · System","固定指令（在“发送给模型”页展开）。");return;}
            if(role=="user"){Card(target,"Core → 模型 · User",Decode(Get(message,"content")) is JsonObject?"本轮结构化上下文（在“发送给模型”页逐部分查看）。":content);return;}
            var calls=Get(message,response?"calls":"tool_calls") as JsonArray??new();
            if(role=="tool"){
                var id=Text(message,"tool_call_id");Card(target,"工具 → 模型 · "+callNames.GetValueOrDefault(id,"工具返回"),"调用 "+id,Format(Decode(Get(message,"content"))));return;
            }
            Card(target,response?"模型 → Core · 本轮公开回复":"历史消息 · "+role,content==""?"（无文本，直接选择工具）":content);
            foreach(var call in calls){var id=Text(call,"id");var f=Get(call,"function");var name=Text(f,"name");callNames[id]=name;
                Card(target,"模型请求调用 · "+name,"调用 "+id,Format(Decode(Get(f,"arguments"))));}
        }
        var history=new StackPanel();foreach(var message in messages)Message(message,history);
        if(request.Reply!=null){
            var reply=(JsonObject)request.Reply.DeepClone();reply["role"]="assistant";Message(reply,_conversation,true);
            var usage=Decode(Get(reply,"usage"));Note(_conversation,$"本轮响应耗时 {Text(reply,"elapsedMs")} ms · usage: {Format(usage)}");
        }else Note(_conversation,"此请求尚无已载入的模型回复。");
        foreach(var returned in decision.ReturnsFor(request).Where(e=>Text(e,"kind")!="tool_selected")){
            var data=Get(returned,"data");var result=Get(data,"result");
            var kind=Text(returned,"kind");
            Card(_conversation,kind switch{"tool_rejected"=>"Core 拒绝调用","model_error" or "offline_chat_error"=>"模型请求失败","decision_cancelled" or "offline_chat_cancelled"=>"决策已取消","no_action_retry_scheduled"=>"未选择动作 · 已安排重试",_=>"Core 本地工具返回"},
                Text(data,"name")+" · "+Text(result,"status")+" "+Text(data,"reason"),Format(data));
        }
        _conversation.Children.Add(new Expander{Header=$"本次实际发出的消息序列 · {messages.Count} 条（含历史工具反馈）",Content=history,Margin=new(0,8,0,0)});
    }
}
