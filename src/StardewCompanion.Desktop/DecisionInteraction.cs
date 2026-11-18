using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("DesktopChecks")]

namespace StardewCompanion.Desktop;

internal static class InteractionJson
{
    internal static JsonNode? Get(JsonNode? node,string key)=>node is JsonObject obj?obj.FirstOrDefault(p=>p.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).Value:null;
    internal static string Text(JsonNode? node,string key)=>Get(node,key)?.ToString()??"";
    internal static int Number(JsonNode? node,string key)=>int.TryParse(Text(node,key),out var value)?value:0;
    internal static readonly JsonSerializerOptions Pretty=new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    internal static string Format(JsonNode? node)=>node?.ToJsonString(Pretty)??"暂无数据";
    internal static JsonNode? Decode(JsonNode? node)
    {
        if(node is JsonValue v&&v.TryGetValue<string>(out var text))try{return JsonNode.Parse(text);}catch(JsonException){}
        return node;
    }
}

internal sealed class InteractionRequest
{
    internal required string Id {get;init;}
    internal required int Round {get;init;}
    internal required long Sequence {get;init;}
    internal required JsonNode Prompt {get;set;}
    internal JsonNode? Reply {get;set;}
    internal int Attempts {get;set;}=1;
    internal string Model=>InteractionJson.Text(Prompt,"model");
    public string Label=>$"第 {Round+1} 轮 · {Model} · "+(Reply==null?"等待回复":$"{InteractionJson.Text(Reply,"elapsedMs")} ms")+(Attempts>1?$" · 发送 {Attempts} 次":"");
}

internal sealed class DecisionInteraction:INotifyPropertyChanged
{
    internal required string Key {get;init;}
    public required string Id {get;init;}
    public required string Scope {get;init;}
    public required string Session {get;init;}
    public required string Time {get;init;}
    public string Title {get;private set;}="等待决策上下文";
    public string Status {get;private set;}="接收中";
    public string Model {get;private set;}="尚无模型请求";
    public string Focus {get;private set;}="";
    public string Metrics=>$"{Requests.Count} 次模型请求 · {ToolCount} 个工具调用";
    public bool HasIssue {get;private set;}
    public bool Maintenance {get;private set;}
    public string SearchText=>string.Join(" ",Id,Scope,Session,Title,Focus,Model,string.Join(" ",Events.Where(e=>InteractionJson.Text(e,"kind") is "tool_selected" or "task_dispatch").Select(e=>InteractionJson.Format(InteractionJson.Get(e,"data")))));
    internal List<JsonNode> Events {get;}=new();
    internal List<InteractionRequest> Requests {get;}=new();
    internal int ToolCount {get;private set;}
    internal bool LoopFinished {get;private set;}
    internal bool RunningPlan {get;private set;}
    internal HashSet<string> ActiveTasks {get;}=new();
    internal bool Cancelled {get;private set;}
    internal bool HasContext {get;private set;}
    internal bool EventsTruncated {get;private set;}
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Add(JsonNode entry)
    {
        var e=entry.DeepClone();Events.Add(e);
        if(Events.Count>300){Events.RemoveAt(0);EventsTruncated=true;}
        var kind=InteractionJson.Text(e,"kind");var data=InteractionJson.Get(e,"data");
        switch(kind){
            case "offline_chat_context":HasContext=true;Title="离线聊天 · "+InteractionJson.Text(data,"currentMessage");break;
            case "decision_context":
                HasContext=true;
                Title=InteractionJson.Text(InteractionJson.Get(data,"trigger"),"reason");
                if(Title=="")Title=InteractionJson.Text(data,"instruction");
                if(Title=="")Title="自主决策";
                Focus=InteractionJson.Text(InteractionJson.Get(data,"agentState"),"currentObjective");
                if(Focus=="")Focus=InteractionJson.Text(InteractionJson.Get(data,"playerGoal"),"objective");
                break;
            case "model_request":Model=InteractionJson.Text(data,"model");Status="等待模型";break;
            case "model_prompt_capture":
                var id=InteractionJson.Text(data,"requestId");var prompt=InteractionJson.Get(data,"prompt");
                if(prompt==null)break;
                var existing=Requests.FirstOrDefault(r=>r.Id==id);
                if(existing!=null){existing.Attempts++;existing.Prompt=prompt.DeepClone();}
                else Requests.Add(new(){Id=id,Round=InteractionJson.Number(data,"round"),Sequence=long.Parse(InteractionJson.Text(e,"sequence")),Prompt=prompt.DeepClone()});
                Model=InteractionJson.Text(prompt,"model");Maintenance=InteractionJson.Text(data,"kind")=="sleep_maintenance";
                if(!HasContext)Title=Maintenance?"季节计划维护":"决策日志（从中途载入）";
                Status="等待模型";
                break;
            case "model_reply":
                var request=Requests.LastOrDefault(r=>r.Id==InteractionJson.Text(data,"requestId"));
                if(request!=null){request.Reply=data?.DeepClone();}
                ToolCount+=(InteractionJson.Get(data,"calls") as JsonArray)?.Count??0;Status="处理工具";break;
            case "tool_rejected":case "model_error":case "offline_chat_error":HasIssue=true;Status=kind=="tool_rejected"?"工具被拒绝":"模型错误";break;
            case "decision_cancelled":case "offline_chat_cancelled":Cancelled=true;Status="决策已取消";break;
            case "plan_started":RunningPlan=true;Status="批次执行中";break;
            case "task_dispatch":ActiveTasks.Add(InteractionJson.Text(data,"taskId"));Status="本地执行中";break;
            case "task_result":
                var result=InteractionJson.Text(data,"status");
                if(result is "completed" or "partial" or "failed" or "rejected" or "cancelled"){
                    ActiveTasks.Remove(InteractionJson.Text(data,"taskId"));
                    if(result is "partial" or "failed" or "rejected")HasIssue=true;
                    Status=result=="completed"?"执行已完成":result=="cancelled"?"执行已取消":"执行受阻";
                }
                break;
            case "plan_finished":
                RunningPlan=false;
                var planStatus=InteractionJson.Text(data,"status");
                if(planStatus is not ("completed" or "cancelled"))HasIssue=true;
                Status=planStatus=="completed"?"批次已完成":planStatus=="cancelled"?"批次已取消":"批次受阻";break;
            case "tool_result":
                var name=InteractionJson.Text(data,"name");var toolResult=InteractionJson.Get(data,"result");
                var toolStatus=InteractionJson.Text(toolResult,"status");
                if(toolStatus=="rejected")HasIssue=true;
                if(name=="wait_for_change"&&toolStatus=="waiting")Status="等待条件变化";
                if(name=="ask_user"&&toolStatus=="pending")Status="等待玩家回答";
                break;
            case "no_action_retry_scheduled":Status="未选择动作 · 已安排重试";break;
            case "decision_finished":
                LoopFinished=true;
                if(RunningPlan)Status="批次执行中";
                else if(ActiveTasks.Count>0)Status="本地执行中";
                else if(Status is "等待模型" or "处理工具" or "接收中")Status=HasIssue?"交互结束 · 含拒绝或错误":"模型交互结束";
                break;
        }
        PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));
    }

    internal IEnumerable<JsonNode> ReturnsFor(InteractionRequest request)
    {
        var next=Requests.Where(r=>r.Sequence>request.Sequence).Select(r=>r.Sequence).DefaultIfEmpty(long.MaxValue).Min();
        return Events.Where(e=>{
            var sequence=long.Parse(InteractionJson.Text(e,"sequence"));
            return sequence>request.Sequence&&sequence<next&&InteractionJson.Text(e,"kind") is "tool_selected" or "tool_result" or "tool_rejected" or "model_error" or "decision_cancelled" or "no_action_retry_scheduled" or "offline_chat_cancelled" or "offline_chat_error";
        });
    }
}

internal sealed class DecisionInteractionLog
{
    internal ObservableCollection<DecisionInteraction> Decisions {get;}=new();
    private readonly Dictionary<string,DecisionInteraction> _byKey=new();
    internal bool HistoryGap {get;set;}
    internal DecisionInteraction? Add(JsonNode entry)
    {
        var id=InteractionJson.Text(entry,"decisionId");if(id=="")return null;
        var scope=InteractionJson.Text(entry,"scope");var session=InteractionJson.Text(entry,"session");var key=scope+"\n"+session+"\n"+id;
        if(!_byKey.TryGetValue(key,out var decision)){
            var time=DateTimeOffset.TryParse(InteractionJson.Text(entry,"at"),out var at)?at.ToLocalTime().ToString("HH:mm:ss"):"";
            decision=new(){Key=key,Id=id,Scope=scope,Session=session,Time=time};_byKey[key]=decision;Decisions.Insert(0,decision);
            if(Decisions.Count>80){var old=Decisions[^1];Decisions.RemoveAt(Decisions.Count-1);_byKey.Remove(old.Key);}
        }
        decision.Add(entry);return decision;
    }
}
