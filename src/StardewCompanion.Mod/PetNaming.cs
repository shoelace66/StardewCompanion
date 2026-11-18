using System.Text.Json;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Menus;

namespace StardewCompanion.Mod;

public sealed partial class ModEntry
{
    private sealed record NamingSubmission(string TaskId,string MenuId,NamingMenu Menu,string Name,
        HashSet<string> PriorPets,long Deadline);

    private NamingMenu? _observedNamingMenu;
    private string _namingMenuId="";
    private string _submittedNamingMenuId="";
    private NamingSubmission? _namingSubmission;

    private string TrackNamingMenu(NamingMenu? menu)
    {
        if(!ReferenceEquals(menu,_observedNamingMenu)){
            _observedNamingMenu=menu;
            _namingMenuId=menu==null?"":Guid.NewGuid().ToString("N");
        }
        return _namingMenuId;
    }

    private static bool IsStarterPetMenu(NamingMenu menu)=>SoloTestMode.Enabled&&
        menu.doneNaming is {Target:Event owner} callback&&ReferenceEquals(owner,Game1.CurrentEvent)&&
        callback.Method.Name=="namePet";

    private object ObserveNamingMenu(NamingMenu menu)
    {
        var menuId=TrackNamingMenu(menu);
        var supported=IsStarterPetMenu(menu);
        return new{
            type=PetNaming.Menu,menuId,petType=supported?Game1.player.whichPetType:null,
            text=supported?"原版开局宠物命名":"请在原版游戏命名框填写并确认名称",
            question=false,choices=Array.Empty<object>(),playerInputRequired=!supported
        };
    }

    private void SubmitPetName(TaskCommand command,JsonElement args)
    {
        void Reject(string reason)=>Emit(command.TaskId,"rejected",reason,skill:PetNaming.Skill);
        if(_paused||_active!=null||_cleanup!=null||_sleepLockedDay==Game1.Date.TotalDays){
            Reject("naming_control_not_ready");return;
        }
        if(Game1.activeClickableMenu is not NamingMenu menu||!IsStarterPetMenu(menu)){
            Reject("starter_pet_naming_menu_not_open");return;
        }
        var menuId=TrackNamingMenu(menu);
        if(args.GetProperty("menuId").GetString()!=menuId){
            Reject("naming_menu_changed");return;
        }
        if(_namingSubmission!=null||_submittedNamingMenuId==menuId){
            Reject("naming_already_submitted");return;
        }
        var name=args.GetProperty("name").GetString()??"";
        var box=menu.textBox;
        if(!PetNaming.IsValidName(name)||name.Length<menu.minLength||
            box.textLimit>=0&&name.Length>box.textLimit||
            box.limitWidth&&Game1.dialogueFont.MeasureString(name).X>box.Width-21||
            menu.FilterInput&&Utility.FilterDirtyWords(name)!=name){
            Reject("invalid_pet_name");return;
        }
        var pets=Utility.getAllPets().Select(p=>p.petId.Value.ToString()).ToHashSet();
        _namingSubmission=new(command.TaskId,menuId,menu,name,pets,Environment.TickCount64+5000);
        _submittedNamingMenuId=menuId;
        Emit(command.TaskId,"accepted","native_pet_naming",skill:PetNaming.Skill);
        try{
            box.Text=name;
            menu.textBoxEnter(box);
        }
        catch(Exception ex){
            Monitor.Log("Native pet naming confirmation failed: "+ex,StardewModdingAPI.LogLevel.Warn);
            VerifyPetName(force:true);
        }
    }

    private void VerifyPetName(bool force=false)
    {
        if(_namingSubmission is not {} pending)return;
        var pet=ReferenceEquals(Game1.activeClickableMenu,pending.Menu)?null:
            Utility.getAllPets().FirstOrDefault(p=>!pending.PriorPets.Contains(p.petId.Value.ToString())&&p.Name==pending.Name);
        if(pet==null&&!force&&Environment.TickCount64<pending.Deadline)return;

        _namingSubmission=null;
        _bridge?.Send("observation",_session,Observe());
        Emit(pending.TaskId,pet==null?"failed":"completed",pet==null?"pet_name_not_verified":"pet_name_verified",
            JsonSerializer.Serialize(new{menuId=pending.MenuId,name=pending.Name,petId=pet?.petId.Value.ToString()}),
            completed:pet==null?0:1,skill:PetNaming.Skill);
    }
}
