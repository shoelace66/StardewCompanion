using System.Text.Json;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Quests;
using StardewValley.Tools;
using StardewValley.TerrainFeatures;

namespace StardewCompanion.Fixture;

public sealed partial class FixtureMod
{
    private const string AcceptanceKey="StardewCompanion.TestFixture/Key";
    private object PrepareAcceptanceEntities(JsonElement request,Farm farm,Farmer player)
    {
        var prepared=new List<object>();
        if(request.TryGetProperty("siloHay",out var siloHay)){
            farm.piecesOfHay.Value=siloHay.GetInt32();
            prepared.Add(new{kind="silo_hay",count=farm.piecesOfHay.Value,fixtureOnly=true});
        }
        if(request.TryGetProperty("terrain",out var terrain))foreach(var value in terrain.EnumerateArray()){
            var tile=new Vector2(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32());
            var type=value.GetProperty("type").GetString();
            if(type=="grass")farm.terrainFeatures[tile]=new Grass(1,value.TryGetProperty("weeds",out var weeds)?weeds.GetInt32():4);
            else if(type=="tree"){
                var tree=new Tree(value.TryGetProperty("treeType",out var treeType)?treeType.GetString()!:"1",value.TryGetProperty("stage",out var stage)?stage.GetInt32():5);
                tree.stump.Value=value.TryGetProperty("stump",out var stump)&&stump.GetBoolean();farm.terrainFeatures[tile]=tree;
            }else throw new InvalidOperationException("Unknown acceptance terrain: "+type);
            prepared.Add(new{kind="terrain",type,x=(int)tile.X,y=(int)tile.Y,fixtureOnly=true});
        }
        if(request.TryGetProperty("calendar",out var calendar)){
            Game1.year=calendar.GetProperty("year").GetInt32();
            Game1.season=Enum.Parse<Season>(calendar.GetProperty("season").GetString()!,true);
            Game1.dayOfMonth=calendar.GetProperty("day").GetInt32();
            Game1.stats.DaysPlayed=(uint)(Game1.Date.TotalDays+1);
            prepared.Add(new{kind="calendar",year=Game1.year,season=Game1.currentSeason,day=Game1.dayOfMonth,fixtureOnly=true});
        }
        if(request.TryGetProperty("mailReceived",out var received))foreach(var value in received.EnumerateArray())player.mailReceived.Add(value.GetString()!);
        if(request.TryGetProperty("eventsSeen",out var events))foreach(var value in events.EnumerateArray())player.eventsSeen.Add(value.GetString()!);
        foreach(var pair in new[]{("craftingRecipes",player.craftingRecipes),("cookingRecipes",player.cookingRecipes)})
            if(request.TryGetProperty(pair.Item1,out var recipes)){pair.Item2.Clear();foreach(var recipe in recipes.EnumerateArray())pair.Item2[recipe.GetString()!]=0;}
        if(request.TryGetProperty("wateringCanWater",out var water))foreach(var can in player.Items.OfType<WateringCan>())can.WaterLeft=water.GetInt32();
        if(request.TryGetProperty("toolLevels",out var levels))foreach(var level in levels.EnumerateObject())
            foreach(var tool in player.Items.OfType<Tool>().Where(t=>t.QualifiedItemId==level.Name))tool.UpgradeLevel=level.Value.GetInt32();
        if(request.TryGetProperty("protectedItems",out var protectedItems))foreach(var value in protectedItems.EnumerateArray())
            foreach(var item in player.Items.Where(i=>i?.QualifiedItemId==value.GetString()))item.modData["StardewCompanion/Protected"]="true";
        if(request.TryGetProperty("inventoryCapacity",out var capacity)){
            player.MaxItems=capacity.GetInt32();while(player.Items.Count>player.MaxItems)player.Items.RemoveAt(player.Items.Count-1);
        }
        if(request.TryGetProperty("fillInventory",out var fill)&&fill.GetBoolean())
            for(var i=0;i<player.Items.Count;i++)if(player.Items[i]==null)player.Items[i]=ItemRegistry.Create("(O)168");
        if(request.TryGetProperty("houseUpgrade",out var houseUpgrade)){
            var house=Utility.getHomeOfFarmer(player);house.upgradeLevel=houseUpgrade.GetInt32();house.updateFarmLayout();
            prepared.Add(new{kind="kitchen",level=house.upgradeLevel,location=house.NameOrUniqueName,fixtureOnly=true});
        }
        if(request.TryGetProperty("buildings",out var buildings))foreach(var value in buildings.EnumerateArray()){
            var key=value.GetProperty("key").GetString()!;
            var existing=farm.buildings.FirstOrDefault(b=>b.modData.TryGetValue(AcceptanceKey,out var k)&&k==key);
            if(existing!=null){farm.buildings.Remove(existing);}
            var building=new Building(value.GetProperty("type").GetString()!,new Vector2(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32()));
            building.modData[AcceptanceKey]=key;building.owner.Value=player.UniqueMultiplayerID;
            farm.buildings.Add(building);building.load();
            building.daysOfConstructionLeft.Value=value.TryGetProperty("constructionDays",out var days)?days.GetInt32():0;
            prepared.Add(new{kind="building",key,id=building.id.Value,type=building.buildingType.Value,indoors=building.GetIndoors()?.NameOrUniqueName,fixtureOnly=true});
        }
        // Optional test-only objects inside a fixture animal building, needed to prepare native coop/barn machine states.
        if(request.TryGetProperty("interiorObjects",out var interiorObjects))foreach(var value in interiorObjects.EnumerateArray()){
            var key=value.GetProperty("buildingKey").GetString()!;
            var building=farm.buildings.FirstOrDefault(b=>b.modData.TryGetValue(AcceptanceKey,out var existingKey)&&existingKey==key)
                ??throw new InvalidOperationException("Fixture interior building missing: "+key);
            var interior=building.GetIndoors()??throw new InvalidOperationException("Fixture building has no interior: "+key);
            var tile=new Vector2(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32());
            var itemId=value.GetProperty("id").GetString()!;
            var obj=ItemRegistry.Create<StardewValley.Object>(itemId);
            if(value.TryGetProperty("output",out var output)){
                if(obj.QualifiedItemId=="(BC)165"){
                    var contents=new Chest(true);
                    contents.Items.Add(ItemRegistry.Create(output.GetString()!));
                    obj.heldObject.Value=contents;
                }else{
                    obj.heldObject.Value=ItemRegistry.Create<StardewValley.Object>(output.GetString()!);
                }
                obj.readyForHarvest.Value=true;
                obj.MinutesUntilReady=0;
            }
            interior.objects[tile]=obj;
            var heldContainer=obj.heldObject.Value;
            var containerContents=heldContainer is Chest heldChest
                ?heldChest.Items.Where(item=>item!=null).Select(item=>new{id=item!.QualifiedItemId,count=item.Stack}).ToArray()
                :Array.Empty<object>();
            prepared.Add(new{kind="interior_object",buildingKey=key,location=interior.NameOrUniqueName,x=(int)tile.X,y=(int)tile.Y,itemId=obj.QualifiedItemId,name=obj.Name,heldObjectId=heldContainer?.QualifiedItemId,containerContents,ready=obj.readyForHarvest.Value,fixtureOnly=true});
        }
        if(request.TryGetProperty("animals",out var animals))foreach(var value in animals.EnumerateArray()){
            var id=value.GetProperty("id").GetInt64();var homeKey=value.GetProperty("home").GetString()!;
            var building=farm.buildings.Single(b=>b.modData.TryGetValue(AcceptanceKey,out var k)&&k==homeKey);
            var house=building.GetIndoors() as AnimalHouse??throw new InvalidOperationException("Fixture building has no animal house");
            foreach(var location in Game1.locations.Concat(farm.buildings.Select(b=>b.GetIndoors()).OfType<GameLocation>())){
                location.animals.Remove(id);if(location is AnimalHouse previous)previous.animalsThatLiveHere.Remove(id);
            }
            var animal=new FarmAnimal(value.GetProperty("type").GetString()!,id,player.UniqueMultiplayerID);
            house.adoptAnimal(animal);
            animal.age.Value=value.TryGetProperty("age",out var age)?age.GetInt32():30;
            animal.friendshipTowardFarmer.Value=value.TryGetProperty("friendship",out var friendship)?friendship.GetInt32():100;
            animal.fullness.Value=value.TryGetProperty("fullness",out var fullness)?fullness.GetInt32():0;
            animal.happiness.Value=200;animal.wasPet.Value=false;
            if(value.TryGetProperty("produce",out var produce))animal.currentProduce.Value=produce.GetString();
            animal.Position=new Vector2(value.TryGetProperty("x",out var x)?x.GetInt32():3,value.TryGetProperty("y",out var y)?y.GetInt32():4)*64;
            prepared.Add(new{kind="animal",id,location=house.NameOrUniqueName,fixtureOnly=true});
        }
        if(request.TryGetProperty("pets",out var pets))foreach(var value in pets.EnumerateArray()){
            var key=value.GetProperty("key").GetString()!;
            foreach(var location in Game1.locations)foreach(var previous in location.characters.OfType<Pet>().Where(p=>p.modData.TryGetValue(AcceptanceKey,out var k)&&k==key).ToArray())location.characters.Remove(previous);
            var pet=new Pet(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32(),"0",value.GetProperty("type").GetString()!);
            pet.modData[AcceptanceKey]=key;pet.friendshipTowardFarmer.Value=value.TryGetProperty("friendship",out var f)?f.GetInt32():0;
            farm.characters.Add(pet);pet.currentLocation=farm;
            if(value.TryGetProperty("assignBowl",out var assignBowl)&&assignBowl.GetBoolean()){
                var bowl=farm.buildings.OfType<PetBowl>().FirstOrDefault(b=>!b.HasPet())??throw new InvalidOperationException("Fixture requires an unassigned native PetBowl");
                bowl.AssignPet(pet);bowl.watered.Value=false;
            }
            prepared.Add(new{kind="pet",key,id=pet.petId.Value,fixtureOnly=true});
        }
        if(request.TryGetProperty("npcs",out var npcs))foreach(var value in npcs.EnumerateArray()){
            var npc=Game1.getCharacterFromName(value.GetProperty("name").GetString()!)??throw new InvalidOperationException("Fixture NPC missing");
            var location=Game1.getLocationFromName(value.GetProperty("location").GetString()!)??throw new InvalidOperationException("Fixture NPC map missing");
            npc.currentLocation?.characters.Remove(npc);location.characters.Add(npc);npc.currentLocation=location;
            npc.Position=new Vector2(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32())*64;npc.controller=null;
            if(value.TryGetProperty("friendship",out var points))player.friendshipData[npc.Name]=new Friendship(points.GetInt32());
            prepared.Add(new{kind="npc",name=npc.Name,location=location.NameOrUniqueName,fixtureOnly=true});
        }
        if(request.TryGetProperty("deliveryQuests",out var quests))foreach(var value in quests.EnumerateArray()){
            var id=value.GetProperty("id").GetString()!;
            foreach(var existing in player.questLog.Where(q=>q.id.Value==id).ToArray())player.questLog.Remove(existing);
            var quest=new ItemDeliveryQuest(value.GetProperty("npc").GetString()!,value.GetProperty("itemId").GetString()!);
            quest.id.Value=id;quest.number.Value=value.GetProperty("count").GetInt32();quest.moneyReward.Value=value.GetProperty("reward").GetInt32();
            if(value.TryGetProperty("description",out var description))quest.questDescription=description.GetString()??"";
            else if(value.TryGetProperty("onBoard",out var boardQuest)&&boardQuest.GetBoolean())quest.questDescription="Fixture daily quest for acceptance verification.";
            if(value.TryGetProperty("onBoard",out var onBoardQuest)&&onBoardQuest.GetBoolean())
                typeof(Quest).GetField("_loadedDescription",BindingFlags.Instance|BindingFlags.NonPublic)?.SetValue(quest,true);
            quest.completed.Value=value.TryGetProperty("completed",out var completed)&&completed.GetBoolean();player.questLog.Add(quest);
            quest.accepted.Value=value.TryGetProperty("accepted",out var accepted)&&accepted.GetBoolean();
            quest.canBeCancelled.Value=value.TryGetProperty("canBeCancelled",out var cancel)&&cancel.GetBoolean();
            quest.dailyQuest.Value=value.TryGetProperty("daily",out var daily)&&daily.GetBoolean();
            if(value.TryGetProperty("onBoard",out var onBoard)&&onBoard.GetBoolean()){
                player.questLog.Remove(quest);Game1.netWorldState.Value.SetQuestOfTheDay(quest);
                // Set the description on the board instance after it has been
                // installed; the native board may resolve its quest object lazily.
                if(Game1.questOfTheDay is {} boardQuest){
                    boardQuest.questDescription=value.TryGetProperty("description",out var boardDescription)
                        ?boardDescription.GetString()??""
                        :"Fixture daily quest for acceptance verification.";
                    typeof(Quest).GetField("_loadedDescription",BindingFlags.Instance|BindingFlags.NonPublic)?.SetValue(boardQuest,true);
                }
            }
            prepared.Add(new{kind="quest",id,fixtureOnly=true});
        }
        if(request.TryGetProperty("refreshDailyQuest",out var refreshDailyQuest)&&refreshDailyQuest.GetBoolean()){
            Game1.RefreshQuestOfTheDay();
            prepared.Add(new{kind="daily_quest_refresh",id=Game1.questOfTheDay?.id.Value,fixtureOnly=true});
        }
        return prepared;
    }
}
