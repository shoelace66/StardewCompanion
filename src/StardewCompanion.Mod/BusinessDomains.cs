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
                yield return Native(_skill,_args);break;
            case "order_building":case "modify_building":
                if(_skill=="order_building"){
                    if(!Game1.buildingData.TryGetValue(Text("buildingType"),out var definition))throw new InvalidOperationException("building_type_unknown");
                    if(!string.IsNullOrEmpty(definition.BuildingToUpgrade))throw new InvalidOperationException("use_modify_building_for_upgrade");
                    if(!GameStateQuery.CheckConditions(definition.BuildCondition,Game1.getFarm(),Game1.player))throw new InvalidOperationException("building_not_unlocked:"+Text("buildingType"));
                    if(definition.BuildCost>Num("maxCost")||definition.BuildCost>Game1.player.Money)throw new InvalidOperationException("building_budget_exceeded");
                    foreach(var material in IngredientSources(definition.BuildMaterials?.ToDictionary(m=>m.ItemId,m=>m.Amount)??new Dictionary<string,int>(),1,false))yield return material;
                    RequireBuildingMaterials(definition.BuildMaterials?.ToDictionary(m=>m.ItemId,m=>m.Amount)??new Dictionary<string,int>());
                }
                if(_skill=="modify_building"&&Text("operation")=="upgrade"){
                    if(!Game1.buildingData.TryGetValue(Text("upgradeType"),out var upgrade))throw new InvalidOperationException("building_upgrade_type_unknown");
                    if(!GameStateQuery.CheckConditions(upgrade.BuildCondition,Game1.getFarm(),Game1.player))throw new InvalidOperationException("building_not_unlocked:"+Text("upgradeType"));
                    if(_buildingTargetAtStart==null)throw new InvalidOperationException("building_id_not_found");
                    if(string.IsNullOrEmpty(upgrade.BuildingToUpgrade)||upgrade.BuildingToUpgrade!=_buildingTargetAtStart.buildingType.Value)
                        throw new InvalidOperationException("building_upgrade_incompatible:current="+_buildingTargetAtStart.buildingType.Value+";required="+upgrade.BuildingToUpgrade+";requested="+Text("upgradeType"));
                    if(_buildingTargetAtStart.daysOfConstructionLeft.Value>0||_buildingTargetAtStart.daysUntilUpgrade.Value>0)
                        throw new InvalidOperationException("building_construction_in_progress");
                    if(upgrade.BuildCost>Num("maxCost")||upgrade.BuildCost>Game1.player.Money)throw new InvalidOperationException("building_budget_exceeded");
                    foreach(var material in IngredientSources(upgrade.BuildMaterials?.ToDictionary(m=>m.ItemId,m=>m.Amount)??new Dictionary<string,int>(),1,false))yield return material;
                    RequireBuildingMaterials(upgrade.BuildMaterials?.ToDictionary(m=>m.ItemId,m=>m.Amount)??new Dictionary<string,int>());
                }
                yield return Native(_skill,_args);break;
            case "tend_animals":
                foreach(var u in Animals())yield return u;break;
            case "care_for_pets":
                foreach(var unit in PetCare())yield return unit;break;
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
    private void RequireBuildingMaterials(IReadOnlyDictionary<string,int> requirements)
    {
        var shortages=requirements.Select(r=>new{itemId=r.Key,required=r.Value,available=Game1.player.Items.Where(item=>item!=null&&!item.modData.ContainsKey("StardewCompanion/Protected")&&CraftingRecipe.ItemMatchesForCrafting(item,r.Key)).Sum(item=>item.Stack)})
            .Where(r=>r.available<r.required).ToArray();
        if(shortages.Length==0)return;
        Stages.Add(new{skill="building_materials",status="failed",reason="building_materials_missing",shortages,completed=0});
        throw new InvalidOperationException("building_materials_missing");
    }
    private IEnumerable<WorkUnit> Animals()
    {
        var ids=_args.TryGetProperty("animalIds",out var requested)?requested.EnumerateArray().Select(v=>v.GetString()!).ToHashSet():new HashSet<string>();
        var scope=Text("location");
        if(scope!=""&&Game1.getLocationFromName(scope)==null)throw new InvalidOperationException("animal_location_unknown:"+scope);
        var animals=EntityLocator.Animals().Where(a=>ids.Count>0?ids.Contains(a.Animal.myID.Value.ToString()):scope==""||scope=="Farm"||a.Location.NameOrUniqueName==scope||a.Animal.home?.GetIndoors()?.NameOrUniqueName==scope).ToArray();
        if(ids.Any(id=>!animals.Any(a=>a.Animal.myID.Value.ToString()==id)))throw new InvalidOperationException("animal_id_not_present_in_world");
        Stages.Add(new{skill="animal_selection",status="selected",animals=animals.Select(a=>new{animalId=a.Animal.myID.Value.ToString(),location=a.Location.NameOrUniqueName}).ToArray()});
        var maps=scope==""&&ids.Count==0?EntityLocator.Locations().Where(l=>l is Farm or AnimalHouse).ToArray():animals.Select(a=>a.Location)
            .Concat(animals.Select(a=>a.Animal.home?.GetIndoors()).OfType<GameLocation>())
            .Concat(scope!=""&&Game1.getLocationFromName(scope) is {} specified?new[]{specified}:Array.Empty<GameLocation>()).Distinct().ToArray();
        if(maps.Any(l=>!EntityLocator.CanEnter(l)))throw new InvalidOperationException("animal_location_disabled");
        if(Flag("feed"))foreach(var house in maps.OfType<AnimalHouse>())yield return Native("feed",new{location=house.NameOrUniqueName},true);
        foreach(var found in animals.OrderBy(a=>a.Location==Game1.currentLocation?0:1)){
            var animalId=found.Animal.myID.Value.ToString();
            if(Flag("pet"))yield return Native("pet_animal",new{animalId},true);
            if(Flag("milk"))yield return Native("animal_tool",new{animalId,tool="MilkPail"},true);
            if(Flag("shear"))yield return Native("animal_tool",new{animalId,tool="Shears"},true);
        }
        foreach(var map in maps){
            if(Flag("collectGround"))yield return Native("collect_animal_ground",new{location=map.NameOrUniqueName},true);
            if(Flag("collectGrabbers"))yield return Native("collect_grabbers",new{location=map.NameOrUniqueName},true);
            if(Text("door")!="unchanged"&&map is AnimalHouse)yield return Native("animal_door",new{location=map.NameOrUniqueName,door=Text("door")});
        }
    }
    private JsonElement AuthorizedBinding(string? reference)
    {
        if(!_args.TryGetProperty("_bindings",out var bindings))throw new InvalidOperationException("authorized_storage_bindings_missing");
        var matches=bindings.EnumerateArray().Where(b=>b.GetProperty("Name").GetString()==reference||b.GetProperty("Id").GetString()==reference).ToArray();
        if(matches.Length!=1)throw new InvalidOperationException((matches.Length==0?"chest_not_authorized:":"authorized_chest_ambiguous:")+reference);
        return matches[0];
    }
    private WorkUnit Transfer(string skill,object request)
    {
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(request));var a=doc.RootElement;
        var name=a.GetProperty("chest").GetString();var item=a.GetProperty("itemId").GetString()!;
        var binding=AuthorizedBinding(name);
        var boundLocation=binding.GetProperty("Location").GetString()!;
        if(!ExecutionTargets.AllowedLocation(boundLocation))throw new InvalidOperationException("location_disabled:"+boundLocation);
        var allowed=binding.GetProperty("AllowedIds").EnumerateArray().Select(v=>v.GetString()!).ToArray();
        var protectedIds=binding.GetProperty("ProtectedIds").EnumerateArray().Select(v=>v.GetString()!).ToArray();
        if(!allowed.Contains(item)||protectedIds.Contains(item)||item.StartsWith("(T)")||item.StartsWith("(W)")||item is "(O)434" or "(O)326")throw new InvalidOperationException("protected_or_unlisted_item:"+item);
        return new(skill,new{chestId=binding.GetProperty("Id").GetString(),location=binding.GetProperty("Location").GetString(),
            x=binding.GetProperty("X").GetInt32(),y=binding.GetProperty("Y").GetInt32(),itemId=item,count=a.GetProperty("count").GetInt32(),
            quality=a.TryGetProperty("quality",out var q)?q.GetInt32():0,allowedIds=allowed.Except(protectedIds).ToArray()});
    }
}
