using Microsoft.Xna.Framework;
using System.Text.Json;
using System.Collections;
using System.Reflection;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Menus;

namespace StardewCompanion.Mod;

internal static class WorldView
{
    internal const string ChestKey = "StardewCompanion/ChestId";
    internal static bool WorkChest(Chest chest) => chest.playerChest.Value && !chest.giftbox.Value
        && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest;
    internal static bool Supported(GameLocation? location) => location?.Map!=null
        &&location.GetType().Name is not ("VolcanoDungeon")
        &&location.Name is not ("BugLand" or "WitchSwamp" or "SkullCave" or "SkullCavern" or "SlimeHutch")
        &&(location is not MineShaft shaft||shaft.mineLevel is >=0 and <=120);
    internal static object Read()
    {
        var location = Game1.currentLocation;
        return new
        {
            knowledge = "observed", scope = "current_location", location = location.NameOrUniqueName,
            supported = Supported(location), observedAt = Game1.Date.TotalDays,
            maxStamina = Game1.player.MaxStamina,
            farmName=Game1.player.farmName.Value,money=Game1.player.Money,rain=location.IsRainingHere(),
            decisionFacts=DecisionFacts(),
            fishing = new
            {
                rodEquipped=Game1.player.CurrentTool is FishingRod,
                rodState=Game1.player.CurrentTool is FishingRod rod?new{rod.isFishing,rod.isNibbling,rod.isReeling,rod.isCasting,rod.fishCaught,rod.lastCatchWasJunk,rod.showingTreasure}:null,
                minigame=Game1.activeClickableMenu is BobberBar bar?new{fishY=bar.bobberPosition,barTop=bar.bobberBarPos,barHeight=bar.bobberBarHeight,barSpeed=bar.bobberBarSpeed,catchProgress=bar.distanceFromCatching,treasure=bar.treasure,treasureY=bar.treasurePosition,treasureProgress=bar.treasureCatchLevel}:null,
                note="只有当前原版FishingRod及BobberBar状态是事实；未开始抛竿时鱼种与结果未知"
            },
            combat = new
            {
                weapons=Game1.player.Items.OfType<MeleeWeapon>().Where(w=>!w.isScythe()).Select(w=>new{id=w.QualifiedItemId,name=w.DisplayName,kind=w.type.Value,damage=w.minDamage.Value==w.maxDamage.Value?w.minDamage.Value:(int?)null}).ToArray(),
                visibleMonsters=location.characters.OfType<Monster>().Where(m=>m.Health>0).Select(m=>new{name=m.Name,hp=m.Health,maxHp=m.MaxHealth,x=m.TilePoint.X,y=m.TilePoint.Y,distanceTiles=MathF.Sqrt(Vector2.DistanceSquared(m.Position,Game1.player.Position))/64f}).ToArray(),
                combatMeaning="只列当前地图可见怪物和背包武器；击杀以原版怪物状态确认"
            },
            ordinaryMine=location is MineShaft mine&&mine.mineLevel is >=0 and <=120?new
            {
                floor=mine.mineLevel,isOrdinaryMine=true,ladderSpawned=mine.ladderHasSpawned,
                ladder=mine.ladderHasSpawned?new{x=(int)mine.tileBeneathLadder.X,y=(int)mine.tileBeneathLadder.Y}:null,
                elevator=mine.tileBeneathElevator==Vector2.Zero?null:new{x=(int)mine.tileBeneathElevator.X,y=(int)mine.tileBeneathElevator.Y},
                lockedElevatorLevels="查询游戏原生电梯菜单时才确定",visibleExits=location.warps.Select(w=>new{x=w.X,y=w.Y,target=w.TargetName,targetX=w.TargetX,targetY=w.TargetY}).ToArray()
            }:null,
            water = (Game1.player.Items.FirstOrDefault(i => i is WateringCan) as WateringCan)?.WaterLeft,
            mailbox=GameEventTracker.Mailbox(),
            gameEvents=GameEventTracker.Pending(),
            inventoryState=new{capacity=Game1.player.MaxItems,occupiedSlots=Game1.player.Items.Count(i=>i!=null),freeSlots=Math.Max(0,Game1.player.MaxItems-Game1.player.Items.Count(i=>i!=null))},
            inventory = Game1.player.Items.Select((i, slot) => new { slot, id = i?.QualifiedItemId, name = i?.DisplayName, count = i?.Stack ?? 0, tool = i is Tool }),
            animals=location.animals.Values.Select(a=>new{id=a.myID.Value.ToString(),name=a.displayName,type=a.type.Value,x=a.TilePoint.X,y=a.TilePoint.Y,petted=a.wasPet.Value,homeId=a.home?.id.Value.ToString()}),
            pets=location.characters.OfType<StardewValley.Characters.Pet>().Select(p=>new{id=p.petId.Value.ToString(),name=p.displayName,x=p.TilePoint.X,y=p.TilePoint.Y,pettedToday=p.lastPetDay.TryGetValue(Game1.player.UniqueMultiplayerID,out var petDay)&&petDay==Game1.Date.TotalDays}),
            buildings=location.buildings.Select(b=>new{id=b.id.Value.ToString(),type=b.buildingType.Value,x=b.tileX.Value,y=b.tileY.Value,width=b.tilesWide.Value,height=b.tilesHigh.Value,indoors=b.GetIndoors()?.NameOrUniqueName,constructionDaysRemaining=b.daysOfConstructionLeft.Value,bowlWatered=b is StardewValley.Buildings.PetBowl bowl?(bool?)bowl.watered.Value:null}),
            toolUpgrade=Game1.player.toolBeingUpgraded.Value is {} upgrading?new{toolId=upgrading.QualifiedItemId,level=upgrading.UpgradeLevel,daysRemaining=Game1.player.daysLeftForToolUpgrade.Value}:null,
            knownRecipes=new{crafting=Game1.player.craftingRecipes.Keys.OrderBy(n=>n).ToArray(),cooking=Game1.player.cookingRecipes.Keys.OrderBy(n=>n).ToArray()},
            players = Game1.getOnlineFarmers().Select(p => new { id = p.UniqueMultiplayerID.ToString(), name = p.Name, location = p.currentLocation?.NameOrUniqueName, x = p.TilePoint.X, y = p.TilePoint.Y }),
            soil = location.terrainFeatures.Pairs.Where(p => p.Value is HoeDirt).Select(p =>
            { var h = (HoeDirt)p.Value; return new { x = (int)p.Key.X, y = (int)p.Key.Y, watered = h.state.Value == 1, crop = h.crop?.indexOfHarvest.Value,
                dead=h.crop?.dead.Value??false,needsWater=NeedsWater(h,location),seed=h.crop?.netSeedIndex.Value,phase=h.crop?.currentPhase.Value,phaseDay=h.crop?.dayOfCurrentPhase.Value,ready = Ready(h.crop), scythe = h.crop?.GetHarvestMethod() == StardewValley.GameData.Crops.HarvestMethod.Scythe }; }),
            forage = location.objects.Pairs.Where(p => p.Value.IsSpawnedObject && p.Value.isForage()).Select(p => new { x = (int)p.Key.X, y = (int)p.Key.Y, id = p.Value.QualifiedItemId, name = p.Value.DisplayName,canAccept=Game1.player.couldInventoryAcceptThisItem(p.Value) }),
            looseDrops=location.debris.Where(d=>d.item!=null||!string.IsNullOrEmpty(d.itemId.Value)).Select(d=>new{
                id=d.item?.QualifiedItemId??d.itemId.Value,name=d.item?.DisplayName,count=d.item?.Stack??d.Chunks.Count,
                tiles=d.Chunks.Select(c=>new{x=(int)((c.position.X+32)/64),y=(int)((c.position.Y+32)/64)}).ToArray()
            }).Take(128),
            npcs = location.characters.Select(c=>new{name=c.Name,x=c.TilePoint.X,y=c.TilePoint.Y}),
            chairs = Chairs(location),
            naturalObstacles = NaturalObstacles(location),
            chests = location.objects.Pairs.Where(p => p.Value is Chest).Select(p => new { x = (int)p.Key.X, y = (int)p.Key.Y, id = p.Value.modData.TryGetValue(ChestKey, out var id) ? id : null, itemId = p.Value.QualifiedItemId, knowledge = "contents_require_inspection" }),
            routes = Navigation.Edges().Where(e => e.From == location.NameOrUniqueName).Select(e => new {
                fromMap=e.From,toMap=e.To,exitTile=new{x=e.Tile.X,y=e.Tile.Y},arrivalTile=e.ArrivalTile is {} arrival?new{x=arrival.X,y=arrival.Y}:null,
                interact=e.Interact,coordinateMeaning="exitTile属于fromMap；arrivalTile属于toMap，仅在原版入口可确定时提供"
            }),
            remoteContents = "unknown_until_observed", home = Utility.getHomeOfFarmer(Game1.player)?.NameOrUniqueName
        };
    }
    private static object[] Chairs(GameLocation location)=>FindChairs(location).Select(c=>new{name=c.Name,type=c.Type,x=c.X,y=c.Y}).Cast<object>().ToArray();
    private static ChairSpot[] FindChairs(GameLocation location)
    {
        var member=location.GetType().GetProperty("furniture",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(location)
            ??location.GetType().GetField("furniture",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(location);
        if(member is not IEnumerable items)return Array.Empty<ChairSpot>();
        var result=new List<ChairSpot>();
        foreach(var item in items)
        {
            if(item==null)continue;
            var capacity=item.GetType().GetMethod("GetSeatCapacity",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.Invoke(item,null) as int?;
            if(capacity is null||capacity<=0)continue;
            var raw=ReadMember(ReadMember(item,"tileLocation"),"Value")??ReadMember(item,"tileLocation");
            if(raw is not Vector2 tile)continue;
            result.Add(new ChairSpot(ReadMember(item,"Name")?.ToString()??"seat",$"seat_capacity_{capacity}",(int)tile.X,(int)tile.Y));
        }
        return result.ToArray();
    }
    internal static bool IsChairAt(GameLocation location,int x,int y)=>FindChairs(location).Any(c=>c.X==x&&c.Y==y);
    private sealed record ChairSpot(string Name,string Type,int X,int Y);
    private static object[] NaturalObstacles(GameLocation location)
    {
        var player=Game1.player.TilePoint;var result=new List<object>();
        foreach(var pair in location.objects.Pairs)
        {
            var type=pair.Value.Name;
            if(type is not ("Stone" or "Twig" or "Weeds"))continue;
            var x=(int)pair.Key.X;var y=(int)pair.Key.Y;if(Math.Abs(x-player.X)+Math.Abs(y-player.Y)>12)continue;
            result.Add(new{x,y,type,breakable=true,passable=Navigation.Passable(new(x,y)),requires=type=="Stone"?"Pickaxe":"Axe"});
        }
        foreach(var pair in location.terrainFeatures.Pairs)
        {
            var x=(int)pair.Key.X;var y=(int)pair.Key.Y;if(Math.Abs(x-player.X)+Math.Abs(y-player.Y)>12)continue;
            if(pair.Value is Tree tree&&!tree.tapped.Value)
                result.Add(new{x,y,type=tree.stump.Value?"tree_stump":"wild_tree",breakable=true,passable=Navigation.Passable(new(x,y)),requires="Axe"});
            else if(pair.Value is Grass)
                result.Add(new{x,y,type="grass",breakable=true,passable=Navigation.Passable(new(x,y)),requires="Scythe"});
        }
        return result.Take(64).ToArray();
    }
    private static object? ReadMember(object? source,string name)
    {
        if(source==null)return null;
        return source.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(source)
            ??source.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(source);
    }
    internal static object DecisionFacts()
    {
        var l=Game1.currentLocation;var p=Game1.player;var cap=l.GetLocationContext().MaxPassOutCost;
        var safe=l is FarmHouse or IslandFarmHouse or Cellar||l.HasMapPropertyWithValue("PassOutSafe");
        var minutes=Game1.timeOfDay/100*60+Game1.timeOfDay%100;
        var wood=p.Items.Where(i=>i?.QualifiedItemId=="(O)388").Sum(i=>i.Stack);
        var farm=Game1.getLocationFromName("Farm");var farmhouse=Utility.getHomeOfFarmer(p);
        object FarmState(GameLocation? location)=>location==null?new{chests=Array.Empty<object>(),healthyCrops=0,readyCrops=0,wateredCrops=0,cropsNeedingWater=0}:
            new{chests=location.objects.Pairs.Where(o=>o.Value is Chest c&&WorkChest(c)).Select(o=>(object)new{x=(int)o.Key.X,y=(int)o.Key.Y,bound=o.Value.modData.ContainsKey(ChestKey),id=o.Value.modData.TryGetValue(ChestKey,out var id)?id:null}).ToArray(),
                healthyCrops=location.terrainFeatures.Values.OfType<HoeDirt>().Count(d=>d.crop!=null&&!d.crop.dead.Value),
                readyCrops=location.terrainFeatures.Values.OfType<HoeDirt>().Count(d=>Ready(d.crop)),
                wateredCrops=location.terrainFeatures.Values.OfType<HoeDirt>().Count(d=>d.crop!=null&&!d.crop.dead.Value&&d.state.Value==1),
                cropsNeedingWater=location.terrainFeatures.Values.OfType<HoeDirt>().Count(d=>NeedsWater(d,location))};
        return new{
            autonomy="LLM决定是否继续工作、返家或睡觉，本地无体力/时间接管",
            calendar=new{season=Game1.currentSeason,dayOfSeason=Game1.dayOfMonth,dayOfWeek=new[]{"Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"}[Math.Clamp((Game1.dayOfMonth-1)%7,0,6)],timeOfDayHHMM=Game1.timeOfDay,clockMeaning="HHMM-style; 1320 means 13:20"},
            stamina=p.Stamina,maxStamina=p.MaxStamina,exhausted=p.exhausted.Value,health=p.health,maxHealth=p.maxHealth,
            nativePassOutStaminaThreshold=-15,staminaUntilNativePassOut=Math.Max(0,p.Stamina+15),
            nativeForcedPassOutTime=2600,gameMinutesUntil2AM=Math.Max(0,26*60-minutes),
            consequences=new{
                exhaustedAtOrBelow=0,exhaustionEffect="原版疲劳会影响移动及次日恢复",
                currentLocationPassOutFeeExempt=safe,
                currentLocationMoneyLossUpperBound=safe?0:Math.Min(cap<0?int.MaxValue:cap,p.Money/10),
                feeMeaning="根据当前地点和持有金币估算的扣款上界，原版邮件规则可能降低费用；换地点后需重算",
                nextDayEffect="原版疲劳会降低次日体力恢复；午夜后睡觉亦可能减少恢复。准确结算依照原版，不保证全部恢复",
                nativeRuleSource="本机1.6.15 Game1和Farmer原版逻辑；不是自动停工指令"},
            crafting=new{chest=new{recipeKnown=p.craftingRecipes.ContainsKey("Chest"),recipeWoodCost=50,woodAvailable=wood,woodShortfallToOneRecipe=Math.Max(0,50-wood),inventoryHasOutputCapacity=p.Items.Any(i=>i==null||i.QualifiedItemId=="(BC)130"&&i.Stack<i.maximumStackSize())}},
            knownFarmProgress=new{farm=FarmState(farm),farmhouse=FarmState(farmhouse),starterGiftPresent=farmhouse?.objects.Values.OfType<Chest>().Any(c=>c.giftbox.Value&&c.giftboxIsStarterGift.Value)??false,
                starterSeedsInBag=p.Items.Where(i=>i?.QualifiedItemId=="(O)472").Sum(i=>i.Stack),meaning="current native observed state; combine with confirmedCompletedBatches for milestones no longer visible in the current inventory"},
            starterSeeds=new{requiredLocation=Utility.getHomeOfFarmer(p)?.NameOrUniqueName,
                giftPresentHere=l.objects.Values.OfType<Chest>().Any(c=>c.giftbox.Value&&c.giftboxIsStarterGift.Value),
                seedsInBag=p.Items.Where(i=>i?.QualifiedItemId=="(O)472").Sum(i=>i.Stack)},
            toolCost=new{meaning="初始工具典型每次约2体力，技能/升级/附魔会改变；镰刀通常不耗体力。实际消耗见步骤回执",estimatedBaseToolStamina=2},
            resources=l.objects.Pairs.Where(o=>o.Value.Name=="Twig").Select(o=>new{x=(int)o.Key.X,y=(int)o.Key.Y,type="twig",produces="(O)388",
                distanceEstimate=Math.Abs(o.Key.X-p.TilePoint.X)+Math.Abs(o.Key.Y-p.TilePoint.Y)})
                .OrderBy(o=>o.distanceEstimate).Take(20).ToArray(),
            trees=l.terrainFeatures.Pairs.Where(o=>o.Value is Tree t&&!t.tapped.Value&&t.growthStage.Value>=5)
                .Select(o=>new{x=(int)o.Key.X,y=(int)o.Key.Y,type=((Tree)o.Value).stump.Value?"stump":"tree",health=((Tree)o.Value).health.Value,
                    produces="(O)388",distanceEstimate=Math.Abs(o.Key.X-p.TilePoint.X)+Math.Abs(o.Key.Y-p.TilePoint.Y),
                    tool="Axe",yieldMeaning="掉落和实际拾取数量以原版及回执为准，砍树数量不等于木材数量"})
                .OrderBy(o=>o.distanceEstimate).Take(20).ToArray(),
            unknown="未观察地图、未验证路线和未来随机收益仍未知"};
    }
    internal static bool Ready(Crop? crop) => crop != null && !crop.dead.Value && crop.currentPhase.Value >= crop.phaseDays.Count - 1 && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
    internal static bool NeedsWater(HoeDirt dirt,GameLocation location)=>dirt.crop!=null&&!dirt.crop.dead.Value&&dirt.state.Value!=1&&!location.IsRainingHere();
    internal static object Inspect(int x, int y)
    {
        if(x<0||y<0||x>=Game1.currentLocation.Map.Layers[0].LayerWidth||y>=Game1.currentLocation.Map.Layers[0].LayerHeight)return new{knowledge="out_of_bounds",location=Game1.currentLocation.NameOrUniqueName,x,y};
        var tile = new Vector2(x, y);
        Game1.currentLocation.objects.TryGetValue(tile, out var obj);
        var soil=Game1.currentLocation.terrainFeatures.TryGetValue(tile,out var feature)?feature as HoeDirt:null;
        return new { knowledge = "observed", location = Game1.currentLocation.NameOrUniqueName, x, y,
            diggable=Game1.currentLocation.doesTileHaveProperty(x,y,"Diggable","Back")!=null,
            passable=Navigation.Passable(new(x,y)),
            itemId = obj?.QualifiedItemId, name = obj?.DisplayName,
            chestId = obj?.modData.TryGetValue(ChestKey, out var id) == true ? id : null,
            soil=soil==null?null:new{crop=soil.crop?.indexOfHarvest.Value,dead=soil.crop?.dead.Value??false,watered=soil.state.Value==1,
                needsWater=NeedsWater(soil,Game1.currentLocation),ready=Ready(soil.crop)},
            storage = obj is Chest container ? new { capacity=container.GetActualCapacity(),occupiedSlots=container.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Count(i=>i!=null),freeSlots=Math.Max(0,container.GetActualCapacity()-container.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Count(i=>i!=null)) } : null,
            items = obj is Chest chest ? chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(i=>i!=null).Select(i => new { id = i.QualifiedItemId, count = i.Stack, quality=i.Quality }).ToArray() : null };
    }
    internal static object InspectMany(JsonElement args)
    {
        var tiles=args.GetProperty("tiles").EnumerateArray().ToArray();
        if(tiles.Length is <1 or >32)throw new JsonException("Expected 1 to 32 tiles");
        return tiles.Select(t=>Inspect(t.GetProperty("x").GetInt32(),t.GetProperty("y").GetInt32())).ToArray();
    }
}
