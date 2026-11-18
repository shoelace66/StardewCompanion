using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewCompanion.Mod;

// Game-thread only. Events survive disconnects until the core durably acknowledges intake.
internal static class GameEventTracker
{
    internal sealed class Entry
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N");
        public string Type {get;set;}="";
        public int Day {get;set;}
        public int Time {get;set;}
        public string Facts {get;set;}="{}";
    }
    internal sealed class State
    {
        public int Day {get;set;}=-1;
        public int FreeSlots {get;set;}=-1;
        public int Capacity {get;set;}=-1;
        public int PassOutDay {get;set;}=-1;
        public int SleepRequestedDay {get;set;}=-1;
        public string[] Mail {get;set;}=Array.Empty<string>();
        public string[] CapturedMail {get;set;}=Array.Empty<string>();
        public string DialogueSignature {get;set;}="";
        public string? DialogueTaskId {get;set;}
        public string DialoguePromptId {get;set;}="";
        public string DialogueHandling {get;set;}="";
        public List<Entry> Events {get;set;}=new();
    }
    private static string _directory="",_file="",_scope="";
    private static State _state=new();
    internal static long DialogueRevision {get;private set;}
    internal static (long Revision,bool Question,string Text)? LastDialogueCapture {get;private set;}
    internal static void Configure(string directory)=>_directory=Path.Combine(directory,"event-journal");
    private static bool Ensure()
    {
        if(!Context.IsWorldReady)return false;
        var scope=Game1.uniqueIDForThisGame+"-"+Game1.player.UniqueMultiplayerID;
        if(scope==_scope)return true;
        _scope=scope;Directory.CreateDirectory(_directory);_file=Path.Combine(_directory,scope+".json");
        _state=File.Exists(_file)?JsonSerializer.Deserialize<State>(File.ReadAllText(_file))??new():new();
        return true;
    }
    private static void Save()
    {
        var temp=_file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(_state));File.Move(temp,_file,true);
    }
    internal static void Add(string type,object facts)
    {
        if(!Ensure())return;
        _state.Events.Add(new Entry{Type=type,Day=(int)Game1.Date.TotalDays,Time=Game1.timeOfDay,Facts=JsonSerializer.Serialize(facts)});Save();
    }
    internal static void PassOut(Farmer who)
    {
        if(!Context.IsWorldReady||!who.IsLocalPlayer||!Ensure())return;
        _state.PassOutDay=(int)Game1.Date.TotalDays;
        Add("pass_out",new{source="native_Farmer.passOutFromTired",location=who.currentLocation?.NameOrUniqueName,stamina=who.Stamina,time=Game1.timeOfDay});
    }
    internal static void SleepRequested()
    {
        if(!Ensure())return;_state.SleepRequestedDay=(int)Game1.Date.TotalDays;Add("sleep_requested",new{source="accepted_native_Sleep_Yes"});
    }
    internal static void ObserveDialogue(string? activeTaskId,string? blockedTaskId,bool scriptWillAcknowledge)
    {
        if(!Ensure())return;
        if(Game1.activeClickableMenu is not DialogueBox dialogue){ClearDialogue();return;}
        var prompt=new{
            type=dialogue.isQuestion?"choice":"notification",
            text=dialogue.getCurrentString()??"",
            question=dialogue.isQuestion,
            questionKey=Game1.currentLocation.lastQuestionKey,
            choices=dialogue.responses.Select(r=>new{responseKey=r.responseKey,text=r.responseText}).ToArray()
        };
        var signature=JsonSerializer.Serialize(prompt);
        if(signature==_state.DialogueSignature)return;
        _state.DialogueSignature=signature;
        LastDialogueCapture=(++DialogueRevision,dialogue.isQuestion,dialogue.getCurrentString()??"");
        _state.DialogueTaskId=activeTaskId;_state.DialoguePromptId=Guid.NewGuid().ToString("N");
        _state.DialogueHandling=dialogue.isQuestion?"await_model_choice":scriptWillAcknowledge?"script_acknowledgement":"agent_dialogue_task";
        Add("story_prompt",new{
            promptId=_state.DialoguePromptId,promptRevision=DialogueRevision,
            source="native_DialogueBox",
            activeTaskId=string.IsNullOrWhiteSpace(activeTaskId)?null:activeTaskId,
            blockedTaskId=string.IsNullOrWhiteSpace(blockedTaskId)?null:blockedTaskId,
            handling=dialogue.isQuestion?"await_model_choice":scriptWillAcknowledge?"script_acknowledgement":"agent_dialogue_task",
            prompt
        });
    }
    internal static void ClearDialogue()
    {
        if(!Ensure()||_state.DialogueSignature=="")return;
        Add("story_prompt_resolved",new{promptId=_state.DialoguePromptId,promptRevision=DialogueRevision,activeTaskId=_state.DialogueTaskId,handling=_state.DialogueHandling,resolved=true,requiresResponse=false});
        _state.DialogueSignature="";_state.DialogueTaskId=null;_state.DialogueHandling="";Save();
    }
    internal static void Poll(string? activeTaskId=null)
    {
        if(!Ensure())return;
        var day=(int)Game1.Date.TotalDays;var mail=Game1.player.mailbox.ToArray();
        var capacity=Game1.player.MaxItems;var free=Math.Max(0,capacity-Game1.player.Items.Count(i=>i!=null));
        if(_state.Day>=0&&_state.Day!=day)Add("day_changed",new{previousDay=_state.Day,currentDay=day,
            cause=_state.PassOutDay==_state.Day?"pass_out":_state.SleepRequestedDay==_state.Day?"sleep":"unknown"});
        var added=mail.Except(_state.Mail).ToArray();
        var alreadyCaptured=_state.CapturedMail.ToHashSet(StringComparer.Ordinal);
        var unreadToCapture=mail.Where(id=>!alreadyCaptured.Contains(id)).ToArray();
        if(unreadToCapture.Length>0){
            var templates=Game1.content.Load<Dictionary<string,string>>("Data/mail");
            var letters=unreadToCapture.Select(id=>new{id,
                bodyAndAttachmentTemplate=templates.TryGetValue(id.Replace("%&NL&%",""),out var body)?body:null,
                source="native_Data/mail_template",meaning="只读提取正文模板和附件指令，不会打开、标记已读或领取附件"}).ToArray();
            Add("mail_arrived",new{activeTaskId=string.IsNullOrWhiteSpace(activeTaskId)?null:activeTaskId,unreadCount=mail.Length,
                canOpen=mail.Length>0,newMailIds=added,readMailIds=unreadToCapture,letters,readOnly=true,claimState="requires_native_mail_view"});
        }
        if(_state.FreeSlots>=0&&(_state.FreeSlots!=free||_state.Capacity!=capacity))Add("inventory_capacity_changed",new{capacity,freeSlots=free,previousFreeSlots=_state.FreeSlots});
        var changed=_state.Day!=day||_state.FreeSlots!=free||_state.Capacity!=capacity||!mail.SequenceEqual(_state.Mail)||!mail.SequenceEqual(_state.CapturedMail);
        _state.Day=day;_state.FreeSlots=free;_state.Capacity=capacity;_state.Mail=mail;_state.CapturedMail=mail;if(changed)Save();
    }
    internal static object[] Pending()=>_state.Events.Select(e=>(object)new{id=e.Id,type=e.Type,gameDay=e.Day,time=e.Time,facts=JsonSerializer.Deserialize<JsonElement>(e.Facts)}).ToArray();
    internal static void Acknowledge(string[] ids)
    {
        if(!Ensure())return;var set=ids.ToHashSet();if(_state.Events.RemoveAll(e=>set.Contains(e.Id))>0)Save();
    }
    internal static object Mailbox()=>new{unreadCount=Game1.player.mailbox.Count,canOpen=Game1.player.mailbox.Count>0,
        mailIds=Game1.player.mailbox.ToArray(),canClaim=(bool?)null,claimState="requires_native_mail_view",meaning="未读邮件标识来自原版邮箱；附件是否可领取需查看原版邮件界面，未自动领取"};
    internal static object ReadMailbox()
    {
        var data=Game1.content.Load<Dictionary<string,string>>("Data/mail");
        return new{state=Mailbox(),readOnly=true,letters=Game1.player.mailbox.Select(id=>new{id,
            bodyAndAttachmentTemplate=data.TryGetValue(id.Replace("%&NL&%",""),out var body)?body:null,
            source="native_Data/mail_template",meaning="原版模板含正文及%item附件指令；不表示附件已领取或邮件已标记已读"}).ToArray()};
    }
}
