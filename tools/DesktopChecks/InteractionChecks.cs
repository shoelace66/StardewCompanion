using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StardewCompanion.Desktop;

internal static class InteractionChecks
{
    internal static void Run(string folder)
    {
        Directory.CreateDirectory(folder);long sequence=0;
        JsonObject Event(string kind,JsonObject data,string id="decision-a",string session="session-a")=>new(){
            ["sequence"]=++sequence,["at"]="2026-11-07T13:30:00+08:00",["scope"]="示例农场:1",["session"]=session,["decisionId"]=id,["kind"]=kind,["data"]=data};
        JsonObject Context(int money)=>new(){
            ["trigger"]=new JsonObject{["kind"]="task_terminal",["reason"]="收获已完成，继续照料作物"},
            ["playerGoal"]=new JsonObject{["objective"]="照料春季作物，并为升级农舍存钱",["restrictions"]=new JsonArray("不出售稀有物")},
            ["conversation"]=new JsonObject{["newMessages"]=new JsonArray("先检查南侧田地")},
            ["agentState"]=new JsonObject{["currentObjective"]="完成南侧作物浇水",["confirmedMilestones"]=new JsonArray("收获12格作物"),["phase"]="deciding"},
            ["world"]=new JsonObject{["location"]="Farm",["money"]=money,["time"]="08:20",["farmingPlan"]=new JsonObject{["waterableCount"]=8}},
            ["activeProjects"]=new JsonArray(new JsonObject{["goal"]="升级农舍",["nextStep"]="积累木材与资金"}),
            ["candidateActions"]=new JsonArray(new JsonObject{["skill"]="water_area",["location"]="Farm",["x"]=10,["y"]=15}),
            ["recentEvents"]=new JsonArray(),["relevantMemories"]=new JsonArray("先核对现有作物"),
            ["decisionScope"]=new JsonObject{["availableTools"]=new JsonArray("survey_area","group_farm_management")}};
        JsonObject Call(string id,string name)=>new(){["id"]=id,["type"]="function",["function"]=new JsonObject{["name"]=name,["arguments"]="{\"location\":\"Farm\",\"x\":10,\"y\":15,\"width\":4,\"height\":2}"}};
        JsonObject Prompt(int money,bool followup=false){
            var messages=new JsonArray(new JsonObject{["role"]="system",["content"]="依据真实观察自主选择行动；执行和结果核验由本地完成。"},new JsonObject{["role"]="user",["content"]=Context(money).ToJsonString()});
            if(followup){messages.Add(new JsonObject{["role"]="assistant",["content"]="先核对田地情况。",["tool_calls"]=new JsonArray(Call("inspect-1","survey_area"))});messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]="inspect-1",["content"]="{\"status\":\"completed\",\"waterableCount\":8}"});}
            return new(){["model"]="configured-model",["messages"]=messages,["tools"]=new JsonArray(new JsonObject{["function"]=new JsonObject{["name"]="group_farm_management"}}),["max_tokens"]=2400,["tool_choice"]="auto"};
        }
        JsonObject Capture(string id,int round,JsonObject prompt)=>new(){["requestId"]=id,["round"]=round,["kind"]="daily_action",["prompt"]=prompt};
        var view=new DecisionInteractionView();var context=Event("decision_context",Context(500));view.Accept(context);
        var first=Event("model_prompt_capture",Capture("request-1",0,Prompt(500)));view.Accept(first);
        // Captures must be detached from subsequent source mutation.
        first["data"]!["prompt"]!["model"]="mutated-after-intake";
        view.Accept(Event("model_reply",new(){["requestId"]="request-1",["text"]="先核对田地情况。",["calls"]=new JsonArray(Call("inspect-1","survey_area")),["elapsedMs"]=825,["usage"]="{\"prompt_tokens\":1200,\"completion_tokens\":88}"}));
        view.Accept(Event("tool_result",new(){["name"]="survey_area",["result"]=new JsonObject{["status"]="completed",["waterableCount"]=8}}));view.Refresh();
        var decision=view.Selected!;
        Require(decision.Requests.Count==1&&decision.Model=="configured-model","request snapshot is immutable after intake");
        var repeated=Event("model_prompt_capture",Capture("request-1",0,Prompt(500)));view.Accept(repeated);view.Refresh();
        Require(decision.Requests.Count==1&&decision.Requests[0].Attempts==2,"transport retry remains one request with attempt count");
        view.Accept(Event("model_prompt_capture",Capture("request-2",1,Prompt(450,true))));view.Refresh();
        Require(view.SelectedRequest!.Id=="request-2","following advances to the newly captured request");
        var selector=Descendants(view).OfType<ComboBox>().Single(c=>c.Name=="RequestSelector");selector.SelectedIndex=0;
        view.Accept(Event("model_reply",new(){["requestId"]="request-2",["text"]="南侧8格作物需要浇水，提交一项明确批次。",["calls"]=new JsonArray(Call("water-1","group_farm_management")),["elapsedMs"]=1120,["usage"]="{\"prompt_tokens\":1680,\"completion_tokens\":142}"}));
        view.Accept(Event("tool_result",new(){["name"]="group_farm_management",["result"]=new JsonObject{["status"]="accepted",["planId"]="water-south",["steps"]=1}}));
        view.Accept(Event("plan_started",new(){["planId"]="water-south",["steps"]=new JsonArray("water_area")}));
        view.Accept(Event("task_dispatch",new(){["skill"]="water_area",["taskId"]="task-water",["planId"]="water-south"}));
        view.Accept(Event("task_result",new(){["skill"]="water_area",["taskId"]="task-water",["status"]="accepted"}));
        view.Accept(Event("decision_finished",new()));view.Refresh();
        Require(view.SelectedRequest!.Id=="request-1"&&decision.Status=="批次执行中","history request stays selected while accepted work is still executing");
        Require(Descendants(view).OfType<TextBox>().Any(t=>t.Text.Contains("\"money\": 500")),"historical input shows that request's world snapshot rather than the latest world");
        Require(decision.Requests[0].Reply!["text"]!.ToString()=="先核对田地情况。"&&decision.Requests[1].Reply!["text"]!.ToString().Contains("8格"),"replies are matched by request ID across rounds");
        Require(decision.ReturnsFor(decision.Requests[0]).All(e=>InteractionJson.Text(InteractionJson.Get(e,"data"),"name")!="group_farm_management"),"tool returns are limited to the selected request round");
        selector.SelectedIndex=1;
        Require(Descendants(view).OfType<TextBox>().Any(t=>t.Text.Contains("\"money\": 450")),"changing rounds selects the captured input from the corresponding request");
        Require(Descendants(view).OfType<TextBox>().Any(t=>t.Text=="南侧8格作物需要浇水，提交一项明确批次。"),"current public reply text is rendered from model_reply.text");
        Require(Descendants(view).OfType<TextBox>().Any(t=>t.Text.Contains("inspect-1"))&&Descendants(view).OfType<TextBlock>().Any(t=>t.Text.Contains("工具 → 模型")),"tool call IDs and tool-to-model feedback are visible");
        view.Accept(Event("task_result",new(){["skill"]="water_area",["taskId"]="task-water",["status"]="progress",["completed"]=4,["reason"]="watering"}));
        view.Accept(Event("task_result",new(){["skill"]="water_area",["taskId"]="task-water",["status"]="completed",["completed"]=8,["reason"]="area_completed"}));
        view.Accept(Event("plan_finished",new(){["planId"]="water-south",["status"]="completed",["reason"]="all_steps_verified"}));view.Refresh();
        Require(decision.Status=="批次已完成"&&!decision.HasIssue,"only native completion and plan summary finish the execution stage");
        var idBefore=view.Selected!.Id;
        var list=Descendants(view).OfType<ListBox>().Single(c=>c.Name=="DecisionList");list.SelectedItem=decision;
        // Selecting a historical item explicitly pins it.
        var follow=Descendants(view).OfType<CheckBox>().Single(c=>Equals(c.Content,"跟随最新决策"));follow.IsChecked=false;
        var blockedContext=Context(600);blockedContext["trigger"]!["reason"]="重复浇水请求被拒绝";
        view.Accept(Event("decision_context",blockedContext,"decision-b"));view.Refresh();
        Require(view.Selected!.Id==idBefore,"new decisions do not replace a pinned historical selection");
        view.Accept(Event("tool_rejected",new(){["name"]="water_area",["reason"]="unchanged_target_previously_failed"},"decision-b"));view.Refresh();
        var filter=Descendants(view).OfType<ComboBox>().Single(c=>c!=selector);filter.SelectedIndex=1;
        Require(list.Items.Count==1&&((DecisionInteraction)list.Items[0]).HasIssue,"issue filter selects decisions containing rejection evidence");
        filter.SelectedIndex=0;list.SelectedItem=decision;selector.SelectedIndex=1;
        InteractionChecks.Capture(view,Path.Combine(folder,"llm-interaction-input.png"));
        var tabs=Descendants(view).OfType<TabControl>().Single();tabs.SelectedIndex=1;
        InteractionChecks.Capture(view,Path.Combine(folder,"llm-interaction-conversation.png"));
        tabs.SelectedIndex=2;InteractionChecks.Capture(view,Path.Combine(folder,"llm-interaction-execution.png"));

        var isolation=new DecisionInteractionLog();isolation.Add(Event("decision_context",Context(1),"same-id","one"));isolation.Add(Event("decision_context",Context(2),"same-id","two"));
        Require(isolation.Decisions.Count==2,"session identity prevents unrelated decision IDs from merging");
        for(var i=0;i<100;i++)isolation.Add(Event("decision_context",Context(i),"bounded-"+i));
        Require(isolation.Decisions.Count==80,"decision history is bounded to 80 groups");
        var offline=new DecisionInteractionView();
        offline.Accept(Event("offline_chat_context",new(){["currentMessage"]="昨天做到哪里了？"},"offline-decision"));
        var offlinePrompt=new JsonObject{["model"]="offline-model",["messages"]=new JsonArray(new JsonObject{["role"]="system",["content"]="离线只聊天"},new JsonObject{["role"]="user",["content"]="玩家问题：昨天做到哪里了？"})};
        offline.Accept(Event("model_prompt_capture",Capture("offline-request",0,offlinePrompt),"offline-decision"));
        offline.Accept(Event("model_reply",new(){["requestId"]="offline-request",["text"]="连接后核对南侧作物。",["elapsedMs"]=120},"offline-decision"));
        offline.Accept(Event("decision_finished",new(),"offline-decision"));offline.Refresh();
        Require(offline.Selected!.Title.StartsWith("离线聊天")&&offline.Selected.Status=="模型交互结束","offline decisions finish without falsely implying native execution");
        Require(Descendants(offline).OfType<TextBox>().Any(t=>t.Text=="玩家问题：昨天做到哪里了？")&&Descendants(offline).OfType<TextBox>().Any(t=>t.Text=="连接后核对南侧作物。"),"offline view renders the actual plain-text prompt and public reply");
        var sessionFile=Path.Combine(folder,"session.json");File.WriteAllText(sessionFile,"{\"endpoint\":\"ws://127.0.0.1:27841/game\",\"token\":\"fixture-only\"}");
        var monitor=new DecisionMonitorWindow(sessionFile);
        try{
            var add=typeof(DecisionMonitorWindow).GetMethod("Add",BindingFlags.Instance|BindingFlags.NonPublic)!;
            add.Invoke(monitor,new object[]{Event("task_result",new(){["taskId"]="p",["skill"]="water_area",["status"]="progress",["completed"]=2})});
            add.Invoke(monitor,new object[]{Event("task_result",new(){["taskId"]="p",["skill"]="water_area",["status"]="completed",["completed"]=8})});
            var rows=(IEnumerable)typeof(DecisionMonitorWindow).GetField("_rows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(monitor)!;
            Require(rows.Cast<object>().Count()==1,"terminal raw receipt replaces the previous progress row");
        }finally{monitor.Close();}
        Console.WriteLine("INTERACTION PREVIEWS: "+folder);
    }
    private static void Require(bool ok,string message){if(!ok)throw new Exception("FAIL: "+message);Console.WriteLine("PASS interaction: "+message);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()){
            yield return child;foreach(var descendant in Descendants(child))yield return descendant;
        }
    }
    private static void Capture(FrameworkElement view,string path)
    {
        var frame=new Border{Background=new SolidColorBrush(Color.FromRgb(241,245,249)),Child=view};
        frame.Measure(new Size(1320,900));frame.Arrange(new Rect(0,0,1320,900));frame.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1320,900,96,96,PixelFormats.Pbgra32);bitmap.Render(frame);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);
        frame.Child=null;
    }
}
