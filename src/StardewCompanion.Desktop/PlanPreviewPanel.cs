using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StardewCompanion.Desktop;

internal sealed class PlanPreviewPanel:DockPanel
{
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,12)};
    private readonly TextBox _content=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new("Microsoft YaHei UI"),FontSize=14,Padding=new(12)};
    internal PlanPreviewPanel()
    {
        Margin=new Thickness(12);DockPanel.SetDock(_status,Dock.Top);Children.Add(_status);Children.Add(_content);
        Update(null);
    }
    internal void Update(JsonNode? preview)
    {
        var current=preview?["seasonCurrent"]?.GetValue<bool>()==true;
        var plan=preview?["seasonPlan"];
        var blocker=preview?["blocker"]?.ToString()??"等待连接并保存长期目标";
        _status.Text=(current?"当前计划已保存":"计划未就绪："+(plan==null?"尚未生成":"已过期，需重新生成"))
            +(string.IsNullOrEmpty(blocker)?"。季计划已就绪。":"。执行等待："+blocker);
        _status.Foreground=current?Brushes.DarkGreen:Brushes.DarkOrange;
        var text=new StringBuilder();
        text.AppendLine("玩家长期目标").AppendLine(preview?["objective"]?.ToString() is {Length:>0} goal?goal:"尚未保存，禁止自主执行").AppendLine();
        text.AppendLine("目标版本："+(preview?["goalVersion"]?.ToString()??"—"));
        if(plan==null){text.AppendLine().AppendLine("模型必须先生成并保存完整季计划。");}
        else {
            text.AppendLine("计划绑定目标版本："+plan["goalVersion"]);
            text.AppendLine($"第 {plan["year"]} 年 · {SeasonName(plan["season"]?.ToString())}");
            Section("计划目标","goal");
            Section("必须遵守的约束","constraints");Section("阶段日程与关键节点","milestones");Section("资金、体力与时间策略","resourceStrategy");
            Section("可核验的成功条件","successCriteria");
            Section("已记录的实际进度","progress");
            Section("生成时间","createdAt");
            text.AppendLine().AppendLine("计划内容不代表任务已经完成；执行回执请查看“规划与成果”或“模型交互”。");
        }
        var rendered=text.ToString();if(_content.Text!=rendered)_content.Text=rendered;
        void Section(string title,string key){text.AppendLine().AppendLine(title);if(plan?[key] is JsonArray a){if(a.Count==0)text.AppendLine("暂无记录");for(var i=0;i<a.Count;i++)text.AppendLine($"{i+1}. {a[i]}");}else text.AppendLine(plan?[key]?.ToString()??"缺失，计划需重新生成");}
    }
    private static string SeasonName(string? season)=>season switch{"spring"=>"春季","summer"=>"夏季","fall"=>"秋季","winter"=>"冬季",_=>season??"季节未知"};
}
