using System.Text.Json;
using StardewCompanion.Protocol;
using StardewValley;

namespace StardewCompanion.Mod;

// Resolve named destinations and refresh moving NPCs from native live state.
internal static class NavigationTargets
{
    internal sealed record Target(string Kind,string Id,string Name,string Location,GridPoint? Tile,string Source,int? ObservedTime=null);
    private static Target NpcTarget(EntityLocator.LocatedNpc found)=>new("npc",found.Npc.Name,found.Npc.displayName,found.Location.NameOrUniqueName,
        new(found.Npc.TilePoint.X,found.Npc.TilePoint.Y),"native_live_target_lookup",Game1.timeOfDay);

    internal static Target? Resolve(JsonElement args,out object? failure)
    {
        failure=null;
        var name=args.TryGetProperty("name",out var named)?named.GetString()?.Trim():null;
        var filter=args.TryGetProperty("location",out var located)?located.GetString():null;
        if(name==null){
            var map=Game1.getLocationFromName(filter!);
            if(map?.Map==null){failure=new{reason="navigation_location_unknown",requested=filter};return null;}
            return new("map",map.NameOrUniqueName,map.DisplayName,map.NameOrUniqueName,null,"native_map_definition");
        }
        var locations=EntityLocator.Locations().Where(EntityLocator.CanEnter);
        var candidates=locations.Select(l=>new Target("map",l.NameOrUniqueName,l.DisplayName,l.NameOrUniqueName,null,"native_map_definition"))
            .ToList();
        var npcMatches=EntityLocator.Npcs().Where(n=>n.Npc.Name==name||n.Npc.displayName==name).ToArray();
        candidates.AddRange(npcMatches.Select(NpcTarget));
        if(args.TryGetProperty("_bindings",out var bindings))foreach(var b in bindings.EnumerateArray()){
            var map=b.GetProperty("Location").GetString()!;
            if(ExecutionTargets.AllowedLocation(map))candidates.Add(new("chest",b.GetProperty("Id").GetString()!,b.GetProperty("Name").GetString()!,map,
                new(b.GetProperty("X").GetInt32(),b.GetProperty("Y").GetInt32()),"player_authorized_storage"));
        }
        var matches=candidates.Where(c=>(filter==null||c.Location==filter||c.Kind=="npc"&&npcMatches.Length==1)&&(c.Id==name||c.Name==name))
            .DistinctBy(c=>(c.Kind,c.Id,c.Location)).ToArray();
        if(matches.Length==1)return matches[0];
        var offered=matches.Length>0?matches:candidates.Where(c=>filter==null||c.Location==filter).ToArray();
        failure=new{reason=matches.Length==0?"navigation_name_unknown_or_unavailable":"navigation_name_ambiguous",requested=name,location=filter,
            candidates=offered.Take(12).ToArray(),candidateCount=offered.Length,
            releaseCondition=matches.Length==0?"use_an_observed_map_or_target_name":"choose_an_exact_identity_and_map",
            meaning="exact map names, requested live NPC identity, and authorized chest names; availability still requires native navigation"};
        return null;
    }

    internal static Target? RefreshNpc(Target target)
    {
        var found=EntityLocator.Npc(target.Id,out _);
        return found==null?null:NpcTarget(found);
    }
}
