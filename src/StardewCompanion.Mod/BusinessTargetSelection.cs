using System.Text.Json;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Buildings;

namespace StardewCompanion.Mod;

internal sealed partial class BusinessExecutor
{
    private IEnumerable<WorkUnit> Machines()
    {
        var map=Map;
        if(_args.TryGetProperty("targets",out var explicitTargets)){
            foreach(var target in explicitTargets.EnumerateArray())yield return Native("machine",new{location=map,mode=Text("mode","collect"),target},true);
            yield break;
        }
        if(Game1.currentLocation.NameOrUniqueName!=map)yield return new("go_to",new{location=map});
        var location=Game1.currentLocation;var name=Text("machineName");
        var definitions=DataLoader.Machines(Game1.content);
        var machines=location.objects.Pairs.Where(p=>definitions.ContainsKey(p.Value.QualifiedItemId)).ToArray();
        if(_args.TryGetProperty("region",out var region)){
            var x=region.GetProperty("x").GetInt32();var y=region.GetProperty("y").GetInt32();
            var w=region.GetProperty("width").GetInt32();var h=region.GetProperty("height").GetInt32();
            machines=machines.Where(p=>p.Key.X>=x&&p.Key.X<x+w&&p.Key.Y>=y&&p.Key.Y<y+h).ToArray();
        }
        var selected=machines.Where(p=>name==""||p.Value.QualifiedItemId==name||p.Value.Name==name||p.Value.DisplayName==name).ToArray();
        if(name!=""&&selected.Select(p=>p.Value.QualifiedItemId).Distinct().Count()>1)throw new InvalidOperationException("machine_name_ambiguous:use_qualified_item_id");
        Stages.Add(new{skill="machine_selection",status=selected.Length>0?"selected":"failed",location=map,
            targets=selected.Select(p=>new{x=(int)p.Key.X,y=(int)p.Key.Y,itemId=p.Value.QualifiedItemId,name=p.Value.DisplayName}).ToArray(),
            candidates=selected.Length==0?machines.Select(p=>new{itemId=p.Value.QualifiedItemId,name=p.Value.DisplayName}).Distinct().ToArray():null});
        if(selected.Length==0)throw new InvalidOperationException("machine_target_not_found");
        if(selected.Length>128)throw new InvalidOperationException("machine_target_limit_exceeded:use_smaller_region");
        var remaining=selected.ToList();
        while(remaining.Count>0){
            var next=remaining.OrderBy(p=>new GridPoint((int)p.Key.X,(int)p.Key.Y).Distance(Navigation.Current)).First();remaining.Remove(next);
            var target=JsonSerializer.SerializeToElement(new{x=(int)next.Key.X,y=(int)next.Key.Y,expectedMachineId=next.Value.QualifiedItemId,inputId=Text("inputId")==""?null:Text("inputId"),quality=Num("quality"),fuelId=Text("fuelId")==""?null:Text("fuelId")});
            yield return Native("machine",new{location=map,mode=Text("mode","collect"),target},true);
        }
    }

    private IEnumerable<WorkUnit> PetCare()
    {
        var ids=_args.TryGetProperty("petIds",out var selected)?selected.EnumerateArray().Select(p=>p.GetString()!).ToHashSet():new HashSet<string>();
        var scope=Text("location");
        if(scope!=""&&Game1.getLocationFromName(scope)==null)throw new InvalidOperationException("pet_location_unknown:"+scope);
        var pets=EntityLocator.Pets().Where(p=>ids.Count>0?ids.Contains(p.Pet.petId.Value.ToString()):scope==""||scope=="Farm"||p.Location.NameOrUniqueName==scope).ToArray();
        if(ids.Any(id=>!pets.Any(p=>p.Pet.petId.Value.ToString()==id)))throw new InvalidOperationException("pet_id_not_present_in_world");
        Stages.Add(new{skill="pet_selection",status="selected",pets=pets.Select(p=>new{petId=p.Pet.petId.Value.ToString(),location=p.Location.NameOrUniqueName}).ToArray()});
        foreach(var found in pets.OrderBy(p=>p.Location==Game1.currentLocation?0:1)){
            yield return Native("pets",new{location=found.Location.NameOrUniqueName,petIds=new[]{found.Pet.petId.Value.ToString()},pet=Flag("pet"),collectGifts=Flag("collectGifts"),waterBowls=false},true);
        }
        if(Flag("waterBowls")){
            var petIds=pets.Select(p=>p.Pet.petId.Value.ToString()).ToArray();
            var maps=EntityLocator.Locations().Where(l=>l.buildings.OfType<PetBowl>().Any(b=>petIds.Contains(b.petId.Value.ToString()))).ToArray();
            if(maps.Length==0){Skipped.Add(new{skill="pet_bowl",reason="pet_bowl_not_found_for_requested_pets"});yield break;}
            foreach(var map in maps)yield return Native("pets",new{location=map.NameOrUniqueName,petIds,pet=false,collectGifts=false,waterBowls=true},true);
        }
    }
}
