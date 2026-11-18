using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace StardewCompanion.Fixture;

public sealed partial class FixtureMod
{
    private string _labCommand="";
    private bool _labClockHeld;
    private int _labHeldTime;
    private string _pendingJunimoFile="";
    private string _pendingJunimoId="";
    private int _pendingJunimoArea;
    private bool _pendingJunimoUnlock;
    private int[] _pendingJunimoCompletedBundles=Array.Empty<int>();
    private string _pendingMuseumFile="";
    private string _pendingMuseumId="";
    private string _pendingMuseumItemId="";
    private int _pendingMuseumX;
    private int _pendingMuseumY;
    private string _pendingCombatFile="";
    private string _pendingCombatId="";
    private void BusinessLabTick()
    {
        if(Environment.GetEnvironmentVariable("SC_BUSINESS_LAB")!="1"||!Context.IsWorldReady||!Context.IsMainPlayer)return;
        var expectedRun=Environment.GetEnvironmentVariable("SC_SOLO_TEST_NAME");
        if(Environment.GetEnvironmentVariable("SC_SOLO_TEST")!="1"||Game1.player.Name!="SC5Host"
            ||Game1.player.farmName.Value!=expectedRun||!StardewCompanion.Protocol.ControlModePolicy.IsSoloTestRunId(expectedRun??""))return;
        if(_pendingCombatId!=""){
            if(Game1.currentLocation is StardewValley.Locations.MineShaft mine){
                foreach(var existing in mine.characters.OfType<Monster>().ToArray())mine.characters.Remove(existing);
                mine.characters.Add(new GreenSlime(Game1.player.Position+new Vector2(64f,64f),mine.mineLevel));
                File.WriteAllText(_pendingCombatFile+".result",JsonSerializer.Serialize(new{id=_pendingCombatId,scene="mine",status="ready",fixtureOnly=true,runId=expectedRun,
                    location=mine.NameOrUniqueName,targetCount=mine.characters.OfType<Monster>().Count()}));
                _pendingCombatFile="";_pendingCombatId="";
            }
            return;
        }
        if(_labClockHeld){Game1.timeOfDay=_labHeldTime;Game1.gameTimeInterval=0;}
        if(_pendingJunimoId!=""){
            if(Game1.currentLocation is not StardewValley.Locations.CommunityCenter pendingCenter)return;
            try{
                if(_pendingJunimoUnlock&&!Game1.player.hasOrWillReceiveMail("canReadJunimoText"))Game1.player.mailReceived.Add("canReadJunimoText");
                foreach(var bundleId in _pendingJunimoCompletedBundles){
                    var slots=pendingCenter.bundles[bundleId];
                    if(slots==null||slots.Length==0)throw new InvalidOperationException("Fixture bundle has no ingredient slots: "+bundleId);
                    pendingCenter.bundles[bundleId]=Enumerable.Repeat(true,slots.Length).ToArray();
                }
                if(File.Exists(_pendingJunimoFile)){
                    using var junimoRequest=JsonDocument.Parse(File.ReadAllText(_pendingJunimoFile));
                    if(junimoRequest.RootElement.TryGetProperty("incompleteBundleSlots",out var incompleteSlots))foreach(var item in incompleteSlots.EnumerateArray()){
                        var bundleId=item.GetProperty("bundleId").GetInt32();var slot=item.GetProperty("slot").GetInt32();
                        var slots=pendingCenter.bundles[bundleId];
                        if(slots==null||slot<0||slot>=slots.Length)throw new InvalidOperationException("Fixture incomplete bundle slot is invalid: "+bundleId+"/"+slot);
                        var state=Enumerable.Repeat(true,slots.Length).ToArray();state[slot]=false;pendingCenter.bundles[bundleId]=state;
                        pendingCenter.bundleRewards[bundleId]=false;
                    }
                    if(junimoRequest.RootElement.TryGetProperty("unclaimedBundleRewardIds",out var unclaimedRewards))foreach(var value in unclaimedRewards.EnumerateArray()){
                        var bundleId=value.GetInt32();
                        if(pendingCenter.bundles[bundleId]==null)throw new InvalidOperationException("Fixture reward bundle is invalid: "+bundleId);
                        pendingCenter.bundleRewards[bundleId]=true;
                    }
                }
                // checkBundle acquires a network mutex before invoking its callback.
                // In isolated multiplayer fixtures the callback can be deferred or
                // unavailable, so construct the same native menu once the warp has
                // settled; tool actions still run through the real executor/menu.
                var pendingMenu=new StardewValley.Menus.JunimoNoteMenu(_pendingJunimoArea,pendingCenter.bundlesDict());
                Game1.activeClickableMenu=pendingMenu;
                File.WriteAllText(_pendingJunimoFile+".result",JsonSerializer.Serialize(new{id=_pendingJunimoId,scene="open_junimo_note",status="ready",fixtureOnly=true,runId=expectedRun,
                    location=Game1.currentLocation.NameOrUniqueName,nativeMenu=pendingMenu.GetType().Name,junimoLanguageUnlocked=Game1.player.hasOrWillReceiveMail("canReadJunimoText"),
                    playerId=Game1.player.UniqueMultiplayerID,money=Game1.player.Money,time=Game1.timeOfDay,
                    bundleDefinitions=Game1.netWorldState.Value.BundleData.Select(pair=>new{pair.Key,pair.Value}).ToArray()}));
                _pendingJunimoFile="";_pendingJunimoId="";_pendingJunimoArea=0;_pendingJunimoUnlock=false;_pendingJunimoCompletedBundles=Array.Empty<int>();
            }catch(Exception ex){File.WriteAllText(_pendingJunimoFile+".result",JsonSerializer.Serialize(new{id=_pendingJunimoId,scene="open_junimo_note",status="failed",reason=ex.Message,exceptionType=ex.GetType().FullName}));
                _pendingJunimoFile="";_pendingJunimoId="";_pendingJunimoArea=0;_pendingJunimoUnlock=false;_pendingJunimoCompletedBundles=Array.Empty<int>();}
            return;
        }
        if(_pendingMuseumId!=""){
            if(Game1.currentLocation is not StardewValley.Locations.LibraryMuseum)return;
            try{
                if(_pendingMuseumItemId!="")Game1.netWorldState.Value.MuseumPieces[new Vector2(_pendingMuseumX,_pendingMuseumY)]=_pendingMuseumItemId;
                Game1.activeClickableMenu=new StardewValley.Menus.MuseumMenu(null);
                var menu=Game1.activeClickableMenu as StardewValley.Menus.MuseumMenu
                    ??throw new InvalidOperationException("native_museum_menu_not_opened");
                var museum=(StardewValley.Locations.LibraryMuseum)Game1.currentLocation;
                File.WriteAllText(_pendingMuseumFile+".result",JsonSerializer.Serialize(new{id=_pendingMuseumId,scene="open_museum",status="ready",fixtureOnly=true,runId=expectedRun,
                    location=Game1.currentLocation.NameOrUniqueName,nativeMenu=menu.GetType().Name,occupiedTile=_pendingMuseumItemId==""?null:new{x=_pendingMuseumX,y=_pendingMuseumY,itemId=_pendingMuseumItemId},
                    playerId=Game1.player.UniqueMultiplayerID,money=Game1.player.Money,time=Game1.timeOfDay,
                    museumPieces=museum.museumPieces.Count(),unclaimedRewards=museum.getRewardsForPlayer(Game1.player).Select(i=>new{id=i.QualifiedItemId,name=i.DisplayName,count=i.Stack}).ToArray()}));
                _pendingMuseumFile="";_pendingMuseumId="";_pendingMuseumItemId="";_pendingMuseumX=0;_pendingMuseumY=0;
            }catch(Exception ex){File.WriteAllText(_pendingMuseumFile+".result",JsonSerializer.Serialize(new{id=_pendingMuseumId,scene="open_museum",status="failed",reason=ex.Message,exceptionType=ex.GetType().FullName}));
                _pendingMuseumFile="";_pendingMuseumId="";_pendingMuseumItemId="";_pendingMuseumX=0;_pendingMuseumY=0;}
            return;
        }
        var file=Environment.GetEnvironmentVariable("SC_BUSINESS_LAB_CONTROL");
        if(string.IsNullOrEmpty(file)||!File.Exists(file))return;
        JsonDocument json;
        try{json=JsonDocument.Parse(File.ReadAllText(file));}
        catch(Exception ex)when(ex is IOException or JsonException){return;}
        using var parsedRequest=json;var request=json.RootElement;
        var id=request.GetProperty("id").GetString()!;if(id==_labCommand)return;_labCommand=id;
        var scene=request.GetProperty("scene").GetString()!;
        try
        {
            if(scene is "clock_hold" or "clock_release"){
                _labHeldTime=Game1.timeOfDay;_labClockHeld=scene=="clock_hold";
                File.WriteAllText(file+".result",JsonSerializer.Serialize(new{id,scene,status="ready",fixtureOnly=true,
                    clockControlSupported=true,nativeFactsSupported=true,clockHeld=_labClockHeld,time=Game1.timeOfDay,runId=expectedRun}));
                return;
            }
            if(scene=="snapshot"){
                File.WriteAllText(file+".result",JsonSerializer.Serialize(new{id,scene,status="ready",fixtureOnly=true,runId=expectedRun,nativeFacts=AcceptanceNativeFacts()}));
                return;
            }
            if(scene is "open_pierre_shop" or "open_pierre_shop_ambiguous"){
                if(Game1.currentLocation.NameOrUniqueName!="SeedShop")throw new InvalidOperationException("fixture_open_pierre_shop_requires_seed_shop_location");
                var opened=Utility.TryOpenShopMenu("SeedShop","Pierre",false);
                var menu=Game1.activeClickableMenu?.GetType().Name??"";
                object? injected=null;
                if(opened&&menu=="ShopMenu"&&scene=="open_pierre_shop_ambiguous"){
                    var shop=(ShopMenu)Game1.activeClickableMenu!;
                    var duplicate=ItemRegistry.Create("(O)475",1,2);
                    shop.AddForSale(duplicate,null);
                    injected=new{itemName=duplicate.DisplayName,itemId=duplicate.QualifiedItemId,quality=duplicate.Quality};
                }
                File.WriteAllText(file+".result",JsonSerializer.Serialize(new{id,scene,status=opened&&menu=="ShopMenu"?"ready":"failed",fixtureOnly=true,runId=expectedRun,opened,nativeMenu=menu,injected,reason=opened&&menu=="ShopMenu"?null:"native_shop_menu_not_opened"}));
                return;
            }
            Game1.exitActiveMenu();
            var p=Game1.player;p.MaxItems=36;while(p.Items.Count<36)p.Items.Add(null);
            for(var i=0;i<p.Items.Count;i++)if(p.Items[i] is not Tool)p.Items[i]=null;
            p.Stamina=p.MaxStamina;p.health=p.maxHealth;p.Money=request.TryGetProperty("money",out var initialMoney)?initialMoney.GetInt32():100000;Game1.timeOfDay=900;
            var farm=Game1.getFarm();
            for(var x=50;x<=76;x++)for(var y=17;y<=40;y++){
                farm.objects.Remove(new(x,y));farm.terrainFeatures.Remove(new(x,y));
            }
            void Give(string item,int count)=>p.addItemToInventoryBool(ItemRegistry.Create(item,count));
            foreach(var item in new[]{("(O)472",30),("(O)368",30),("(O)388",500),("(O)390",300),("(O)382",100),("(O)24",30),("(O)168",10)})
                Give(item.Item1,item.Item2);
            foreach(var recipe in Game1.content.Load<Dictionary<string,string>>("Data/CraftingRecipes").Keys)p.craftingRecipes[recipe]=0;
            foreach(var recipe in Game1.content.Load<Dictionary<string,string>>("Data/CookingRecipes").Keys)p.cookingRecipes[recipe]=0;
            if(scene=="trees"){
                farm.terrainFeatures[new(67,20)]=new Tree("1",5);farm.terrainFeatures[new(70,20)]=new Tree("2",5);
                farm.terrainFeatures[new(73,20)]=new Tree("3",5);
            }
            if(scene=="production"){
                farm.objects[new(68,20)]=ItemRegistry.Create<StardewValley.Object>("(BC)12");
                farm.objects[new(69,20)]=ItemRegistry.Create<StardewValley.Object>("(BC)15");
                Give("(O)309",10);Give("(O)628",3);Give("(O)599",3);
            }
            if(scene=="commerce"){Give("(O)334",20);Give("(O)335",20);}
            if(scene=="recovery"){p.Stamina=20;Give("(O)194",10);}
            Game1.warpFarmer("Farm",64,15,false);
            if(scene=="awareness"){
                p.MaxItems=12;while(p.Items.Count>12)p.Items.RemoveAt(p.Items.Count-1);
                for(var i=0;i<12;i++)if(p.Items[i] is not Tool)p.Items[i]=ItemRegistry.Create("(O)388",1);
                if(!p.mailbox.Contains("spring_2_1"))p.mailbox.Add("spring_2_1");
                var town=Game1.getLocationFromName("Town");var flower=ItemRegistry.Create<StardewValley.Object>("(O)18");
                flower.isSpawnedObject.Value=true;town.objects[new Vector2(51,31)]=flower;
                Game1.warpFarmer("Town",51,30,false);
            }
            if(scene=="awareness_passout"){
                Game1.timeOfDay=2600;
                p.startToPassOut(); // Exercise the real animation, native pass-out settlement and day change.
            }
            // Explicit test preconditions only. No business skill is invoked here.
            if(request.TryGetProperty("money",out var money))p.Money=money.GetInt32();
            if(request.TryGetProperty("time",out var time))Game1.timeOfDay=time.GetInt32();
            if(request.TryGetProperty("stamina",out var stamina))p.Stamina=stamina.GetSingle();
            if(request.TryGetProperty("maxInventorySlots",out var inventorySlots)){
                var capacity=inventorySlots.GetInt32();if(capacity<1||capacity>36)throw new InvalidOperationException("fixture_inventory_capacity_out_of_range");
                p.MaxItems=capacity;while(p.Items.Count>capacity)p.Items.RemoveAt(p.Items.Count-1);while(p.Items.Count<capacity)p.Items.Add(null);
            }
            if(request.TryGetProperty("items",out var items)){
                if(request.TryGetProperty("replaceInventory",out var replace)&&replace.GetBoolean())
                    for(var i=0;i<p.Items.Count;i++)if(p.Items[i] is not Tool)p.Items[i]=null;
                foreach(var item in items.EnumerateArray())p.addItemToInventoryBool(ItemRegistry.Create(item.GetProperty("id").GetString()!,item.GetProperty("count").GetInt32(),item.TryGetProperty("quality",out var q)?q.GetInt32():0));
            }
            if(request.TryGetProperty("removeItems",out var remove))foreach(var idValue in remove.EnumerateArray())
                for(var i=0;i<p.Items.Count;i++)if(p.Items[i]?.QualifiedItemId==idValue.GetString())p.Items[i]=null;
            if(request.TryGetProperty("soil",out var soils))foreach(var soil in soils.EnumerateArray()){
                var x=soil.GetProperty("x").GetInt32();var y=soil.GetProperty("y").GetInt32();
                var dirt=new HoeDirt(soil.TryGetProperty("watered",out var watered)&&watered.GetBoolean()?1:0,farm);
                if(soil.TryGetProperty("seed",out var seed)){
                    dirt.crop=new Crop(seed.GetString()!,x,y,farm);
                    if(soil.TryGetProperty("mature",out var mature)&&mature.GetBoolean())dirt.crop.growCompletely();
                    if(soil.TryGetProperty("dead",out var dead)&&dead.GetBoolean())dirt.crop.dead.Value=true;
                }
                farm.terrainFeatures[new(x,y)]=dirt;
            }
            if(request.TryGetProperty("objects",out var objects))foreach(var value in objects.EnumerateArray()){
                var tile=new Vector2(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32());
                var itemId=value.GetProperty("id").GetString()!;
                StardewValley.Object obj=itemId=="(BC)130"?new Chest(true):ItemRegistry.Create<StardewValley.Object>(itemId);
                if(obj is Chest chest){
                    obj.modData["StardewCompanion/ChestId"]=value.TryGetProperty("chestId",out var chestId)?chestId.GetString()!:Guid.NewGuid().ToString("N");
                    if(value.TryGetProperty("contents",out var contents))foreach(var item in contents.EnumerateArray())
                        chest.GetItemsForPlayer(p.UniqueMultiplayerID).Add(ItemRegistry.Create(item.GetProperty("id").GetString()!,item.GetProperty("count").GetInt32()));
                }
                if(value.TryGetProperty("output",out var output)){obj.heldObject.Value=ItemRegistry.Create<StardewValley.Object>(output.GetString()!);obj.readyForHarvest.Value=true;obj.MinutesUntilReady=0;}
                if(value.TryGetProperty("forage",out var forage))obj.isSpawnedObject.Value=forage.GetBoolean();
                farm.objects[tile]=obj;
            }
            if(request.TryGetProperty("mail",out var mail)){p.mailbox.Clear();foreach(var letter in mail.EnumerateArray())p.mailbox.Add(letter.GetString()!);}
            if(request.TryGetProperty("receivedMail",out var receivedMail))foreach(var letter in receivedMail.EnumerateArray())
                if(!p.mailReceived.Contains(letter.GetString()!))p.mailReceived.Add(letter.GetString()!);
            var preparedEntities=PrepareAcceptanceEntities(request,farm,p);
            if(request.TryGetProperty("locationBuildingKey",out var locationBuildingKey)){
                var key=locationBuildingKey.GetString()!;
                var building=farm.buildings.FirstOrDefault(b=>b.modData.TryGetValue("StardewCompanion.TestFixture/Key",out var existingKey)&&existingKey==key)
                    ??throw new InvalidOperationException("Fixture location building missing: "+key);
                var interior=building.GetIndoors()??throw new InvalidOperationException("Fixture location has no interior: "+key);
                Game1.warpFarmer(interior.NameOrUniqueName,request.GetProperty("x").GetInt32(),request.GetProperty("y").GetInt32(),false);
            }else if(request.TryGetProperty("location",out var location))Game1.warpFarmer(location.GetString()!,request.GetProperty("x").GetInt32(),request.GetProperty("y").GetInt32(),false);
            if(request.TryGetProperty("nearbyCombatTarget",out var nearbyCombatTarget)&&nearbyCombatTarget.GetBoolean()){
                _pendingCombatFile=file;_pendingCombatId=id;return;
            }
            if(scene=="open_junimo_note"){
                _pendingJunimoFile=file;_pendingJunimoId=id;
                _pendingJunimoArea=request.TryGetProperty("area",out var bundleArea)?bundleArea.GetInt32():0;
                _pendingJunimoUnlock=request.TryGetProperty("unlockJunimoLanguage",out var unlockLanguage)&&unlockLanguage.GetBoolean();
                _pendingJunimoCompletedBundles=request.TryGetProperty("completedBundleIds",out var completedBundleIds)
                    ?completedBundleIds.EnumerateArray().Select(value=>value.GetInt32()).ToArray()
                    :Array.Empty<int>();
                return;
            }
            if(scene=="open_museum"){
                var museumForFixture=Game1.getLocationFromName("ArchaeologyHouse") as StardewValley.Locations.LibraryMuseum
                    ??throw new InvalidOperationException("fixture_museum_not_loaded");
                if(request.TryGetProperty("clearMuseum",out var clearMuseum)&&clearMuseum.GetBoolean())Game1.netWorldState.Value.MuseumPieces.Clear();
                if(request.TryGetProperty("museumPieces",out var museumPieces)){
                    foreach(var piece in museumPieces.EnumerateArray())
                        Game1.netWorldState.Value.MuseumPieces[new Vector2(piece.GetProperty("x").GetInt32(),piece.GetProperty("y").GetInt32())]=piece.GetProperty("id").GetString()!.Replace("(O)","");
                }
                if(request.TryGetProperty("fillMuseumSlotsWithItemId",out var fillId)&&request.TryGetProperty("fillMuseumSlotsCount",out var fillCount)){
                    var targetCount=fillCount.GetInt32();var placed=0;
                    for(var x=0;x<museumForFixture.Map.Layers[0].LayerWidth&&placed<targetCount;x++)for(var y=0;y<museumForFixture.Map.Layers[0].LayerHeight&&placed<targetCount;y++)
                        if(museumForFixture.isTileSuitableForMuseumPiece(x,y)){
                            Game1.netWorldState.Value.MuseumPieces[new Vector2(x,y)]=fillId.GetString()!.Replace("(O)","");placed++;
                        }
                    if(placed<targetCount)throw new InvalidOperationException("fixture_insufficient_museum_display_tiles:"+placed);
                }
                if(request.TryGetProperty("removeMail",out var removeMail))foreach(var mailIdValue in removeMail.EnumerateArray())Game1.player.mailReceived.Remove(mailIdValue.GetString()!);
                _pendingMuseumFile=file;_pendingMuseumId=id;
                _pendingMuseumItemId=request.TryGetProperty("occupiedItemId",out var occupiedId)?occupiedId.GetString()!:"";
                _pendingMuseumX=request.TryGetProperty("occupiedTileX",out var occupiedX)?occupiedX.GetInt32():0;
                _pendingMuseumY=request.TryGetProperty("occupiedTileY",out var occupiedY)?occupiedY.GetInt32():0;
                return;
            }
            if(_labClockHeld)_labHeldTime=Game1.timeOfDay;
            object[] mapActions=Array.Empty<object>();
            object? mapConnections=null;
            if(request.TryGetProperty("includeMapActions",out var includeMapActions)&&includeMapActions.GetBoolean()){
                var current=request.TryGetProperty("location",out var scanLocation)
                    ?Game1.getLocationFromName(scanLocation.GetString()!)??Game1.currentLocation
                    :Game1.currentLocation;
                var actions=new List<object>();
                for(var x=0;x<current.Map.Layers[0].LayerWidth;x++)for(var y=0;y<current.Map.Layers[0].LayerHeight;y++){
                    var action=current.doesTileHaveProperty(x,y,"Action","Buildings");
                    if(action!=null)actions.Add(new{x,y,action,approachY=y+1});
                }
                mapActions=actions.ToArray();
                mapConnections=new{warps=current.warps.Select(w=>new{x=w.X,y=w.Y,target=w.TargetName,targetX=w.TargetX,targetY=w.TargetY}).ToArray(),
                    doors=current.doors.Pairs.Select(p=>new{x=p.Key.X,y=p.Key.Y,target=p.Value}).ToArray()};
            }
            File.WriteAllText(file+".result",JsonSerializer.Serialize(new{id,scene,status="ready",fixtureOnly=true,runId=p.farmName.Value,
                clockControlSupported=true,clockHeld=_labClockHeld,
                preparedEntities,
                mapActions,mapConnections,
                saveId=Game1.uniqueIDForThisGame.ToString(),playerId=p.UniqueMultiplayerID,
                money=p.Money,time=Game1.timeOfDay,location=p.currentLocation.NameOrUniqueName,maxItems=p.MaxItems,
                occupiedSlots=p.Items.Count(i=>i!=null),freeSpots=p.freeSpotsInInventory(),
                inventory=p.Items.Where(i=>i!=null).Select(i=>new{id=i.QualifiedItemId,name=i.DisplayName,count=i.Stack,quality=i.Quality}).ToArray(),
                warning="Prepared isolated lab conditions, not autonomous opening evidence"}));
            Monitor.Log("Business lab scene prepared: "+scene,LogLevel.Info);
        }
        catch(Exception ex){File.WriteAllText(file+".result",JsonSerializer.Serialize(new{id,scene,status="failed",reason=ex.Message,exceptionType=ex.GetType().FullName,stackTrace=ex.ToString()}));}
    }
}
