using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewCompanion.Protocol;

namespace StardewCompanion.Fixture;

public sealed partial class FixtureMod
{
    private bool _shopMapLogged;
    private void SoloTick()
    {
        var name=Environment.GetEnvironmentVariable("SC_SOLO_TEST_NAME")??"";
        BusinessLabTick();
        if(Environment.GetEnvironmentVariable("SC_SOLO_TEST")!="1"||!ControlModePolicy.IsSoloTestRunId(name))return;
        var resume=Environment.GetEnvironmentVariable("SC_SOLO_RESUME_SAVE");
        if(!string.IsNullOrEmpty(resume))ResumeSoloSave(name,resume);
        else if(!_started&&_ticks>240&&Game1.activeClickableMenu is TitleMenu title)
        {
            _started=true;
            // This mode only creates a new save. It never loads or overwrites an existing player save.
            if(Directory.GetDirectories(Program.GetSavesFolder(),name+"_*").Length>0)throw new InvalidOperationException("Solo test save already exists");
            Game1.player.Name="SC5Host";Game1.player.displayName="SC5Host";Game1.player.farmName.Value=name;
            Game1.player.favoriteThing.Value="Autonomous farm";Game1.player.isCustomized.Value=true;
            Game1.player.ConvertClothingOverrideToClothesItems();Game1.whichFarm=0;Game1.startingCabins=0;Game1.multiplayerMode=0;
            title.createdNewCharacter(true);
            Monitor.Log("Created a NEW solo test farm through the normal new-game path: "+name,LogLevel.Info);
        }
        if(_ticks%60==0&&Context.IsWorldReady&&Context.IsMainPlayer&&Game1.player.farmName.Value==name)
        {
            if(!_shopMapLogged){
                _shopMapLogged=true;var shop=Game1.getLocationFromName("SeedShop");var actions=new List<object>();
                for(var x=0;x<shop.Map.Layers[0].LayerWidth;x++)for(var y=0;y<shop.Map.Layers[0].LayerHeight;y++){
                    var action=shop.doesTileHaveProperty(x,y,"Action","Buildings");
                    if(action!=null&&(action.StartsWith("Buy ",StringComparison.Ordinal)||action.StartsWith("OpenShop ",StringComparison.Ordinal)))actions.Add(new{x,y,action});
                }
                Monitor.Log("Read-only shop interaction diagnostics: "+JsonSerializer.Serialize(actions),LogLevel.Debug);
            }
            var output=Environment.GetEnvironmentVariable("SC_SOLO_TEST_EVIDENCE");
            if(!string.IsNullOrEmpty(output))File.WriteAllText(output,JsonSerializer.Serialize(new{
                saveId=Game1.uniqueIDForThisGame.ToString(),runId=name,player=Game1.player.Name,day=Game1.Date.TotalDays,
                folder=Path.Combine(Program.GetSavesFolder(),SaveGame.FilterFileName(Game1.GetSaveGameName())+"_"+Game1.uniqueIDForThisGame),
                method=string.IsNullOrEmpty(resume)?"normal new game; no sample items, crops, stamina or date modifications":"normal LoadGameMenu existing-save activation; no sample items, crops, stamina or date modifications"}));
        }
    }
    private void ResumeSoloSave(string runId,string folder)
    {
        if(!_started&&_ticks>240&&Game1.activeClickableMenu is TitleMenu){
            if(Path.GetFileName(folder)!=folder||!Directory.Exists(Path.Combine(Program.GetSavesFolder(),folder)))
                throw new InvalidOperationException("Requested existing solo save was not found");
            _started=true;TitleMenu.subMenu=new LoadGameMenu();
            Monitor.Log("Opening the native save selection for the requested solo test save.",LogLevel.Info);
        }
        if(_joined||_ticks%30!=0||Game1.activeClickableMenu is not TitleMenu||TitleMenu.subMenu is not LoadGameMenu menu)return;
        var slot=menu.MenuSlots.OfType<LoadGameMenu.SaveFileSlot>().FirstOrDefault(s=>s.Farmer.slotName==folder
            &&s.Farmer.Name=="SC5Host"&&s.Farmer.farmName.Value==runId);
        if(slot==null)return;
        _joined=true;Game1.multiplayerMode=0;slot.Activate();
        Monitor.Log("Resumed the existing isolated farm through its native save slot: "+folder,LogLevel.Info);
    }
}
