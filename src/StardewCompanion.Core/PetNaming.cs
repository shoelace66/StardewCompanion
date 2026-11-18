using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private sealed class NamingWait(GameObservation state,string menuId,string petType)
    {
        public string Scope {get;}=state.SaveId+":"+state.PlayerId;
        public string Session {get;}=state.SessionId;
        public string MenuId {get;}=menuId;
        public string PetType {get;}=petType;
        public bool Attempted;
        public volatile bool Manual;
    }

    private volatile NamingWait? _namingWait;
    private const string ManualNamingPrompt="请在原版游戏命名框填写并确认名称。你也可以在聊天中问 AI 想取什么名字。";
    private static JsonObject NamingContext(GameObservation state)
    {
        try{return JsonNode.Parse(state.MenuContext) as JsonObject??new();}
        catch(JsonException){return new();}
    }

    // A menu episode owns one naming attempt. Observation pulses and the normal
    // decision retry timer do not start another request.
    private bool HandleNamingMenu(GameObservation state)
    {
        if(state.Menu!=PetNaming.Menu){
            if(_namingWait is {} previous){
                _namingWait=null;
                if(previous.Scope==_scope&&previous.Session==_session){
                    _retryAfter=0;_emptyDecisionRetries=0;
                    Trace("naming_menu_closed",new{menuId=previous.MenuId,revision=state.Revision});
                    if(_resumed){Status="命名菜单已关闭；正在观察";WakeDecision(state.Revision);}
                    Changed?.Invoke();
                }
            }
            return false;
        }

        var context=NamingContext(state);
        var menuId=context["menuId"]?.ToString()??"";
        var scope=state.SaveId+":"+state.PlayerId;
        if(scope!=_scope||state.SessionId!=_session)return true;
        var wait=_namingWait;
        if(wait==null||wait.Scope!=scope||wait.Session!=state.SessionId||wait.MenuId!=menuId){
            wait=new NamingWait(state,menuId,context["petType"]?.ToString()??"");
            _namingWait=wait;
            _retryAfter=0;
            Invalidate(false);
            Trace("naming_menu_opened",new{menuId,wait.PetType,revision=state.Revision});
        }
        var automatic=menuId!=""&&wait.PetType!=""&&state.ControlMode=="solo_test"&&state.IsHost&&ControlModePolicy.CanControl(state);
        if(!automatic||!PlanningEnabled||!_resumed||Mode=="plan_only")ShowManualNaming(wait);

        if(_thinking is {IsCompleted:false}||_planTask is {IsCompleted:false}||state.ActiveTaskId!="")return true;
        if(PlanningEnabled&&automatic&&!wait.Attempted&&_resumed&&!state.Paused&&Mode!="plan_only"){
            wait.Attempted=true;
            wait.Manual=false;
            _thinking=NamePet(wait);
        }
        else if(_messages.TryDequeue(out var queued)&&queued.Scope==scope&&queued.Session==state.SessionId)
            _thinking=AnswerNamingChat(wait,queued);
        return true;
    }

    private void ShowManualNaming(NamingWait wait)
    {
        if(!ReferenceEquals(_namingWait,wait)||_bridge.Observation?.Menu!=PetNaming.Menu)return;
        if(wait.Manual)return;
        wait.Manual=true;
        Status="等待在游戏中填写名称";
        Trace("naming_manual_required",new{menuId=wait.MenuId,wait.PetType});
        Changed?.Invoke();
    }

    private async Task NamePet(NamingWait wait)
    {
        var epoch=Interlocked.Read(ref _epoch);
        var requestId=Guid.NewGuid().ToString("N");
        _request=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var request=_request;
        request.CancelAfter(TimeSpan.FromSeconds(20));
        var token=request.Token;
        try{
            Status="AI 正在给宠物起名";Changed?.Invoke();
            var tools=new JsonArray(new JsonObject{
                ["type"]="function",["function"]=new JsonObject{
                    ["name"]=PetNaming.Skill,["description"]="提交你为开局宠物选的名字",
                    ["parameters"]=new JsonObject{["type"]="object",["additionalProperties"]=false,
                        ["properties"]=new JsonObject{["name"]=new JsonObject{["type"]="string",["minLength"]=1,["maxLength"]=PetNaming.MaximumLength}},
                        ["required"]=new JsonArray("name")}
                }
            });
            var messages=new JsonArray(
                new JsonObject{["role"]="system",["content"]="为当前宠物想一个简短、适合在游戏中显示的名字。优先使用简单拉丁字母，便于不同语言的游戏字体显示。只调用 submit_pet_name 一次。"},
                new JsonObject{["role"]="user",["content"]="宠物类型："+wait.PetType});
            Trace("model_request",new{requestId,kind="pet_naming",model=Settings.Model,menuId=wait.MenuId},requestId);
            var reply=await _model.Complete(Settings,messages,token,
                toolChoice:new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]=PetNaming.Skill}},
                toolOverrides:tools,maxTokens:512,retryTransport:false,
                capture:body=>Trace("model_prompt_capture",new{requestId,kind="pet_naming",prompt=body,menuId=wait.MenuId},requestId));
            Trace("model_reply",new{requestId,kind="pet_naming",text=reply.Text,calls=reply.Calls,elapsedMs=reply.ElapsedMs,usage=reply.Usage},requestId);
            token.ThrowIfCancellationRequested();
            if(epoch!=Interlocked.Read(ref _epoch)||!_resumed||!ReferenceEquals(_namingWait,wait)||!_bridge.Fresh||
                _bridge.Observation is not {Menu:PetNaming.Menu,Paused:false,ActiveTaskId:""} state||
                state.SessionId!=wait.Session||state.SaveId+":"+state.PlayerId!=wait.Scope||
                NamingContext(state)["menuId"]?.ToString()!=wait.MenuId)return;

            var call=reply.Calls.Count==1?reply.Calls[0]?["function"]:null;
            var args=call?["name"]?.ToString()==PetNaming.Skill&&reply.FinishReason!="length"
                ?JsonNode.Parse(call["arguments"]?.GetValue<string>()??"null") as JsonObject:null;
            if(args?.Count!=1||args["name"] is not JsonValue value||!value.TryGetValue<string>(out var name)||!PetNaming.IsValidName(name))
                throw new InvalidDataException("invalid_pet_name");
            var taskId=Guid.NewGuid().ToString("N");
            var arguments=new{menuId=wait.MenuId,name};
            _taskDecisions[taskId]=requestId;
            Trace("tool_selected",new{name=PetNaming.Skill,arguments},requestId);
            Trace("task_dispatch",new{taskId,skill=PetNaming.Skill,arguments,source="model"},requestId);
            TaskResult result;
            try{
                token.ThrowIfCancellationRequested();
                result=await _bridge.Dispatch(PetNaming.Skill,arguments,true,token,taskId);
            }
            finally{_taskDecisions.TryRemove(taskId,out _);}
            Trace("tool_result",new{name=PetNaming.Skill,result},requestId);
            if(result.Status!="completed"||result.Reason!="pet_name_verified")throw new InvalidOperationException(result.Reason);
            if(wait.Scope==_scope&&wait.Session==_session)Say($"我给它取名叫{name}。");
        }
        catch(Exception ex){
            Trace("naming_attempt_ended",new{menuId=wait.MenuId,reason=ex is OperationCanceledException?"cancelled_or_timeout":SafeError(ex)},requestId);
        }
        finally{
            ShowManualNaming(wait);
            _request=null;
        }
    }

    private async Task AnswerNamingChat(NamingWait wait,QueuedInstruction queued)
    {
        var requestId=Guid.NewGuid().ToString("N");
        _request=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var request=_request;
        request.CancelAfter(TimeSpan.FromSeconds(20));
        try{
            var messages=new JsonArray(
                new JsonObject{["role"]="system",["content"]="简短回答玩家。若玩家问你想取什么宠物名字，直接给出你选的名字。仅聊天，不宣称已经提交或完成命名。"},
                new JsonObject{["role"]="user",["content"]=queued.Text});
            Trace("model_request",new{requestId,kind="naming_chat",model=Settings.Model},requestId);
            var reply=await _model.Complete(Settings,messages,request.Token,toolOverrides:new JsonArray(),maxTokens:256,
                capture:body=>Trace("model_prompt_capture",new{requestId,kind="naming_chat",prompt=body},requestId));
            Trace("model_reply",new{requestId,kind="naming_chat",text=reply.Text,calls=reply.Calls,elapsedMs=reply.ElapsedMs,usage=reply.Usage},requestId);
            if(queued.Scope==_scope&&queued.Session==_session&&!request.IsCancellationRequested&&reply.Text!=""){
                if(queued.ConversationMessageId is {Length:>0} id)ConfirmConversationMessage(id);
                Say(reply.Text);
            }
        }
        catch(Exception ex){Trace("naming_chat_ended",new{menuId=wait.MenuId,reason=SafeError(ex)},requestId);}
        finally{_request=null;}
    }
}
