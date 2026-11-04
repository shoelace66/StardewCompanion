using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;

namespace StardewCompanion.Mod;

internal sealed partial class BusinessExecutor
{
    private IEnumerable<WorkUnit> Domain()
    {
        switch(_skill){
            case "claim_starter_gift":yield return Native(_skill,_args);break;
            case "purchase_items":case "sell_items":
                if(_skill=="sell_items"&&Text("channel")=="shipping")yield return Native("shipping",_args);
                else yield return Native(_skill,_args);
                break;
            case "upgrade_tool":case "collect_upgraded_tool":
                yield return new("go_to",new{location="Blacksmith",x=3,y=13});
                yield return Native(_skill,_args);break;
            case "order_building":case "modify_building":
                if(_skill=="order_building"){
                    if(!Game1.buildingData.TryGetValue(Text("buildingType"),out var definition))throw new InvalidOperationException("building_type_unknown");
                    if(!string.IsNullOrEmpty(definition.BuildingToUpgrade))throw new InvalidOperationException("use_modify_building_for_upgrade");
                    if(definition.BuildCost>Num("maxCost")||definition.BuildCost>Game1.player.Money)throw new InvalidOperationException("building_budget_exceeded");
                    foreach(var material in IngredientSources(definition.BuildMaterials?.ToDictionary(m=>m.ItemId,m=>m.Amount)??new Dictionary<string,int>(),1,false))yield return material;
                }
                yield return new("go_to",new{location="ScienceHouse",x=8,y=18});
                yield return Native(_skill,_args);break;
            case "tend_animals":
                foreach(var u in Animals())yield return u;break;
            case "care_for_pets":
                yield return Native("pets",_args);break;
            case "manage_livestock":
                yield return Native("livestock",_args);break;
            case "collect_daily_info":
                yield return Native("daily_info",_args);break;
            case "visit_villagers":
                foreach(var visit in _args.GetProperty("visits").EnumerateArray())yield return Native("visit",visit,true);
                break;
            case "manage_quests":case "donate_bundle":case "donate_museum":case "complete_public_project":case "attend_event":
                yield return Native(_skill,_args);break;
            default:throw new InvalidOperationException("business_adapter_not_implemented:"+_skill);
        }
    }
    private IEnumerable<WorkUnit> Animals()
    {
        if(Game1.currentLocation.NameOrUniqueName!=Map)yield return new("go_to",new{location=Map,x=2,y=4});
        if(Flag("feed"))yield return Native("feed",new{location=Map},true);
        var ids=_args.GetProperty("animalIds").EnumerateArray().Select(v=>v.GetString()).ToHashSet();
        var animals=Game1.currentLocation.animals.Values.Where(a=>ids.Count==0||ids.Contains(a.myID.Value.ToString())).ToArray();
        foreach(var animal in animals.OrderBy(a=>Vector2.DistanceSquared(a.Position,Game1.player.Position))){
            if(Flag("pet"))yield return Native("pet_animal",new{location=Map,animalId=animal.myID.Value.ToString()},true);
            if(Flag("milk"))yield return Native("animal_tool",new{location=Map,animalId=animal.myID.Value.ToString(),tool="MilkPail"},true);
            if(Flag("shear"))yield return Native("animal_tool",new{location=Map,animalId=animal.myID.Value.ToString(),tool="Shears"},true);
        }
        if(Flag("collectGround"))yield return Native("collect_animal_ground",new{location=Map},true);
        if(Flag("collectGrabbers"))yield return Native("collect_grabbers",new{location=Map},true);
        if(Text("door")!="unchanged")yield return Native("animal_door",new{location=Map,door=Text("door")});
    }
    private WorkUnit Transfer(string skill,object request)
    {
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(request));var a=doc.RootElement;
        var name=a.GetProperty("chest").GetString();var item=a.GetProperty("itemId").GetString()!;
        if(!_args.TryGetProperty("_bindings",out var bindings))throw new InvalidOperationException("authorized_storage_bindings_missing");
        var binding=bindings.EnumerateArray().FirstOrDefault(b=>b.GetProperty("Name").GetString()==name);
        if(binding.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("chest_not_authorized:"+name);
        var allowed=binding.GetProperty("AllowedIds").EnumerateArray().Select(v=>v.GetString()!).ToArray();
        var protectedIds=binding.GetProperty("ProtectedIds").EnumerateArray().Select(v=>v.GetString()!).ToArray();
        if(!allowed.Contains(item)||protectedIds.Contains(item)||item.StartsWith("(T)")||item.StartsWith("(W)"))throw new InvalidOperationException("protected_or_unlisted_item:"+item);
        return new(skill,new{chestId=binding.GetProperty("Id").GetString(),location=binding.GetProperty("Location").GetString(),
            x=binding.GetProperty("X").GetInt32(),y=binding.GetProperty("Y").GetInt32(),itemId=item,count=a.GetProperty("count").GetInt32(),
            quality=a.TryGetProperty("quality",out var q)?q.GetInt32():0,allowedIds=allowed.Except(protectedIds).ToArray()});
    }
}
