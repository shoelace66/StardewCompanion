using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewCompanion.Mod;

internal static class BusinessAvailability
{
    internal static bool Lab=>Environment.GetEnvironmentVariable("SC_BUSINESS_LAB")=="1"&&SoloTestMode.Enabled;
    internal static bool Available(string name)=>HasNativeAdapter(name);
    internal static bool AllowedForDirectLabExecution(string name)=>Available(name);
    internal static CapabilityStatus[] Read()=>BusinessCapabilities.All.Select(c=>new CapabilityStatus{
        Name=c.Name,Group=c.Group,Implemented=HasNativeAdapter(c.Name),Validated=false,
        State=HasNativeAdapter(c.Name)?"available_unverified":"unavailable",Reason=HasNativeAdapter(c.Name)?"native_adapter_present_requires_gameplay_validation":"native_adapter_not_implemented"}).ToArray();
    private static bool HasNativeAdapter(string name)=>name is
        "claim_starter_gift" or "survey_area" or "cultivate_plot" or "maintain_plot" or "harvest_and_replant" or "gather_resource" or "plant_trees" or "forage_route" or
        "collect_outputs" or "service_machines" or "craft_items" or "cook_recipes" or "craft_and_place" or "install_irrigation" or
        "organize_storage" or "prepare_loadout" or "recover" or "end_day" or "travel" or "tend_animals" or "care_for_pets" or "manage_livestock" or
        "purchase_items" or "sell_items" or "upgrade_tool" or "collect_upgraded_tool" or "order_building" or "modify_building" or
        "collect_daily_info" or "visit_villagers" or "manage_quests" or "donate_bundle" or "donate_museum" or "attend_event";
    internal static object Survey(JsonElement args)
    {
        if(args.GetProperty("location").GetString()!=Game1.currentLocation.NameOrUniqueName)return new{status="blocked",reason="requires_current_observed_location"};
        var l=Game1.currentLocation;var cells=new List<object>();var x=args.GetProperty("x").GetInt32();var y=args.GetProperty("y").GetInt32();
        var width=args.GetProperty("width").GetInt32();var height=args.GetProperty("height").GetInt32();
        for(var xx=x;xx<x+width;xx++)for(var yy=y;yy<y+height;yy++){
            var tile=new Vector2(xx,yy);l.objects.TryGetValue(tile,out var obj);l.terrainFeatures.TryGetValue(tile,out var f);var dirt=f as HoeDirt;
            var type=f is Tree tree?tree.stump.Value?"small_stump":"wild_tree":f is FruitTree?"fruit_tree":obj?.Name??f?.GetType().Name;
            cells.Add(new{x=xx,y=yy,diggable=l.doesTileHaveProperty(xx,yy,"Diggable","Back")!=null,
                passable=Navigation.Passable(new(xx,yy)),obstacle=type,protectedTarget=obj!=null&&!Natural(obj)||f is FruitTree||f is Tree t&&t.tapped.Value,
                tree=f is Tree tr?new{stage=tr.growthStage.Value,stump=tr.stump.Value,tapped=tr.tapped.Value,health=tr.health.Value}:null,
                soil=dirt==null?null:new{seed=dirt.crop?.netSeedIndex.Value,crop=dirt.crop?.indexOfHarvest.Value,dead=dirt.crop?.dead.Value??false,
                    needsWater=WorldView.NeedsWater(dirt,l),watered=dirt.state.Value==1,fertilizer=dirt.fertilizer.Value},
                sprinklerCoverage=l.objects.Pairs.Where(p=>p.Value.IsSprinkler()&&p.Value.GetSprinklerTiles().Contains(tile)).Select(p=>new{x=(int)p.Key.X,y=(int)p.Key.Y}).ToArray(),
                coverageMeaning="次日原版洒水器覆盖，不代表今天已经浇水"});
        }
        return new{location=l.NameOrUniqueName,gameDay=Game1.Date.TotalDays,cells};
    }
    private static bool Natural(StardewValley.Object obj)=>obj.Name is "Stone" or "Twig" or "Weeds";
}
