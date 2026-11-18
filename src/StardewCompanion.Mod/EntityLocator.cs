using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;

namespace StardewCompanion.Mod;

// Execution-only lookup: locate the requested entity without requiring the model
// to discover its coordinates or exposing unrelated remote world contents.
internal static class EntityLocator
{
    internal sealed record LocatedNpc(NPC Npc,GameLocation Location);
    internal sealed record LocatedAnimal(FarmAnimal Animal,GameLocation Location);
    internal sealed record LocatedPet(Pet Pet,GameLocation Location);
    internal static IEnumerable<GameLocation> Locations()=>Game1.locations
        .Concat(Game1.locations.SelectMany(l=>l.buildings.Select(b=>b.GetIndoors())).OfType<GameLocation>())
        .Append(Game1.currentLocation).Distinct();
    internal static IEnumerable<LocatedNpc> Npcs()=>Locations().SelectMany(l=>l.characters
        .Where(n=>n is not StardewValley.Monsters.Monster and not Pet and not Horse).Select(n=>new LocatedNpc(n,l)));
    internal static LocatedNpc? Npc(string name,out string? reason)
    {
        var all=Npcs().ToArray();var exact=all.Where(n=>n.Npc.Name==name).ToArray();
        var matches=exact.Length>0?exact:all.Where(n=>n.Npc.displayName==name).ToArray();
        reason=matches.Length==0?"npc_not_present_in_world":matches.Length>1?"npc_name_ambiguous":null;
        return matches.Length==1?matches[0]:null;
    }
    internal static IEnumerable<LocatedAnimal> Animals()=>Locations().Where(l=>l is Farm or AnimalHouse)
        .SelectMany(l=>l.animals.Values.Select(a=>new LocatedAnimal(a,l))).DistinctBy(a=>a.Animal.myID.Value);
    internal static IEnumerable<LocatedPet> Pets()=>Locations().SelectMany(l=>l.characters.OfType<Pet>().Select(p=>new LocatedPet(p,l)))
        .DistinctBy(p=>p.Pet.petId.Value);
    internal static bool CanEnter(GameLocation location)=>WorldView.Supported(location)&&ExecutionTargets.AllowedLocation(location.NameOrUniqueName);
}
