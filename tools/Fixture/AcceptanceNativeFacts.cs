using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace StardewCompanion.Fixture;

public sealed partial class FixtureMod
{
    // Read-only independent acceptance evidence, guarded by BusinessLabTick's
    // isolated save identity. This never selects or executes a product skill.
    private object AcceptanceNativeFacts()
    {
        var p=Game1.player;var farm=Game1.getFarm();
        var locations=Game1.locations.Concat(farm.buildings.Select(b=>b.GetIndoors()).OfType<GameLocation>()).Distinct().ToArray();
        object[] Items(IEnumerable<Item?> items)=>items.Where(i=>i!=null).Select(i=>(object)new{id=i!.QualifiedItemId,count=i.Stack,quality=i.Quality}).ToArray();
        var nativeTransportTiles=new List<object>();
        var buildingsLayer=p.currentLocation.Map.Layers.FirstOrDefault(layer=>layer.Id=="Buildings");
        if(buildingsLayer!=null)for(var x=0;x<buildingsLayer.LayerWidth;x++)for(var y=0;y<buildingsLayer.LayerHeight;y++){
            var tileIndex=p.currentLocation.getTileIndexAt(x,y,"Buildings","outdoors");
            if(tileIndex is 958 or 1057 or 1080 or 1081)nativeTransportTiles.Add(new{x,y,tileIndex,
                action=p.currentLocation.doesTileHaveProperty(x,y,"Action","Buildings"),
                checkAction=p.currentLocation.doesTileHaveProperty(x,y,"CheckAction","Buildings")??p.currentLocation.doesTileHaveProperty(x,y,"checkAction","Buildings")});
        }
        return new{
            saveId=Game1.uniqueIDForThisGame.ToString(),playerId=p.UniqueMultiplayerID,
            money=p.Money,stamina=p.Stamina,time=Game1.timeOfDay,location=p.currentLocation.NameOrUniqueName,
            canMove=p.canMove,eventUp=Game1.eventUp,swimming=p.swimming.Value,tile=new{x=(int)p.Tile.X,y=(int)p.Tile.Y},
            canAcceptDailyQuest=Game1.CanAcceptDailyQuest(),acceptedDailyQuest=p.acceptedDailyQuest.Value,inventory=Items(p.Items),siloHay=farm.piecesOfHay.Value,
            friendships=p.friendshipData.Pairs.Select(pair=>new{npc=pair.Key,points=pair.Value.Points,talkedToToday=pair.Value.TalkedToToday,giftsToday=pair.Value.GiftsToday,giftsThisWeek=pair.Value.GiftsThisWeek}).ToArray(),
            quests=p.questLog.Select(q=>new{id=q.id.Value,completed=q.completed.Value,reward=q.GetMoneyReward(),accepted=q.accepted.Value,canBeCancelled=q.canBeCancelled.Value}).ToArray(),
            dailyQuest=Game1.questOfTheDay is {} daily?new{id=daily.id.Value,title=daily.questTitle,description=daily.questDescription,completed=daily.completed.Value,accepted=daily.accepted.Value,daily=daily.dailyQuest.Value}:null,
            animals=locations.SelectMany(location=>location.animals.Values.Select(a=>new{id=a.myID.Value,location=location.NameOrUniqueName,petted=a.wasPet.Value,friendship=a.friendshipTowardFarmer.Value,fullness=a.fullness.Value,produce=a.currentProduce.Value})).ToArray(),
            animalHouses=locations.OfType<AnimalHouse>().Select(h=>new{location=h.NameOrUniqueName,objects=h.objects.Pairs.Select(pair=>new{
                x=(int)pair.Key.X,y=(int)pair.Key.Y,id=pair.Value.QualifiedItemId,
                heldObjectId=pair.Value.heldObject.Value?.QualifiedItemId,
                heldContents=pair.Value.heldObject.Value is Chest heldChest?Items(heldChest.Items):Array.Empty<object>(),
                ready=pair.Value.readyForHarvest.Value
            }).ToArray()}).ToArray(),
            pets=locations.SelectMany(location=>location.characters.OfType<Pet>().Select(pet=>new{id=pet.petId.Value,location=location.NameOrUniqueName,friendship=pet.friendshipTowardFarmer.Value,pettedToday=pet.lastPetDay.TryGetValue(p.UniqueMultiplayerID,out var day)&&day==Game1.Date.TotalDays})).ToArray(),
            looseDrops=farm.debris.Where(d=>d.item!=null||!string.IsNullOrEmpty(d.itemId.Value)).Select(d=>new{id=d.item?.QualifiedItemId??d.itemId.Value,count=d.item?.Stack??d.Chunks.Count,tiles=d.Chunks.Select(c=>new{x=(int)((c.position.X+32)/64),y=(int)((c.position.Y+32)/64)}).ToArray()}).ToArray(),
            objects=farm.objects.Pairs.Where(pair=>pair.Key.X>=50&&pair.Key.X<=76&&pair.Key.Y>=17&&pair.Key.Y<=40).Select(pair=>new{
                x=(int)pair.Key.X,y=(int)pair.Key.Y,id=pair.Value.QualifiedItemId,
                ready=pair.Value.readyForHarvest.Value,minutes=pair.Value.MinutesUntilReady,
                output=pair.Value.heldObject.Value is {} output?new{id=output.QualifiedItemId,count=output.Stack}:null,
                contents=pair.Value is Chest chest?Items(chest.GetItemsForPlayer(p.UniqueMultiplayerID)):null
            }).ToArray(),
            terrain=farm.terrainFeatures.Pairs.Where(pair=>pair.Key.X>=50&&pair.Key.X<=76&&pair.Key.Y>=17&&pair.Key.Y<=40).Select(pair=>new{
                x=(int)pair.Key.X,y=(int)pair.Key.Y,type=pair.Value.GetType().Name,
                tree=pair.Value is Tree tree?new{stage=tree.growthStage.Value,stump=tree.stump.Value}:null
            }).ToArray(),
            bowls=farm.buildings.OfType<PetBowl>().Select(b=>new{id=b.id.Value,pet=b.petId.Value,watered=b.watered.Value}).ToArray()
            ,nativeTransportTiles
        };
    }
}
