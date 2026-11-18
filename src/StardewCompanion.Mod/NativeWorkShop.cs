using System.Text.Json;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.GameData.Shops;
using StardewValley.Internal;
using StardewValley.Menus;
using StardewValley.Locations;

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private sealed record ShopCounter(GridPoint Tile,string[] Action);
    private sealed record ShopEntrance(string Map,GridPoint Tile,int? Open,int? Close);
    private long _shopInteractionAt;
    private int _shopInteractionAttempts;
    private string? _shopLastDialogue;
    private Item? _shopPurchaseItem;
    private string? _resolvedShopMap;
    private NativeWorkExecutor? _eventTrade;
    private bool _festivalShop;
    private long _shopPromptRevision;
    private ShopCounter? _lastShopCounter;
    private bool InRequestedFestivalShop=>_festivalShop&&Game1.currentLocation.currentEvent?.isFestival==true;
    private sealed record PendingShopEffect(string ItemId,string ItemName,int Quality,float MaxStaminaBefore,int Spent,long Deadline);
    private PendingShopEffect? _pendingShopEffect;

    private string? VerifyPendingShopEffect()
    {
        if(_pendingShopEffect is not {} pending)return null;
        Phase="verify_native_purchase_effect";
        if(Game1.player.MaxStamina<=pending.MaxStaminaBefore)
            return _clock.Now<pending.Deadline?"running":ShopBlocked(Text("shopId"),"purchase_effect_not_verified","observe_native_item_effect_before_retry");
        Completed++;_before++;
        _results.Add(new{itemId=pending.ItemId,itemName=pending.ItemName,quality=pending.Quality,added=1,spent=pending.Spent,
            receivedAs="native_permanent_stamina_increase",maxStaminaBefore=pending.MaxStaminaBefore,maxStaminaAfter=Game1.player.MaxStamina,
            verifiedPurchased=Completed,verifiedSpent=_totalPurchaseSpent});
        _pendingShopEffect=null;
        var items=_args.GetProperty("items");
        if(_before>=items[_index].GetProperty("count").GetInt32()){_index++;_wait=0;_before=0;_shopPurchaseItem=null;}
        return _index>=items.GetArrayLength()?Finish("native_trade_verified"):null;
    }

    private static bool MatchesShopCounter(string shopId,GameLocation location,string[] action)
    {
        if(action.Length==0)return false;
        if(action[0]=="OpenShop")return action.Length>=2&&action[1]==shopId;
        if(action[0]=="Buy"&&action.Length>=2)return shopId switch{
            "SeedShop"=>location.NameOrUniqueName=="SeedShop"&&action[1]!="Fish",
            "FishShop"=>action[1]=="Fish",
            "Sandy"=>location.NameOrUniqueName=="SandyHouse",
            _=>false
        };
        return shopId switch{
            "Blacksmith" or "ClintUpgrade"=>action[0]=="Blacksmith",
            "Carpenter"=>action[0]=="Carpenter",
            "AnimalShop"=>action[0]=="AnimalShop",
            "Saloon"=>action[0]=="Saloon",
            "Joja"=>action[0]=="JojaShop",
            "Hospital"=>action[0]=="HospitalShop",
            "IceCreamStand"=>action[0]=="IceCreamStand",
            "AdventureShop"=>action[0]=="AdventureShop",
            "Casino"=>action[0]=="ClubShop",
            "Bookseller" or "BooksellerTrade"=>action[0]=="Bookseller",
            "BoxOffice"=>action[0]=="Theater_BoxOffice",
            "QiGemShop"=>action[0]=="QiGemShop",
            _=>false
        };
    }

    private static IEnumerable<ShopCounter> ShopCounters(string shopId,GameLocation? location)
    {
        if(location?.Map==null)yield break;
        for(var x=0;x<location.Map.Layers[0].LayerWidth;x++)
        for(var y=0;y<location.Map.Layers[0].LayerHeight;y++){
            var action=location.doesTileHaveProperty(x,y,"Action","Buildings")?.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(action!=null&&MatchesShopCounter(shopId,location,action))yield return new(new(x,y),action);
            if(shopId=="HatMouse"&&location is Forest&&location.getTileIndexAt(x,y,"Buildings","outdoors")==1972)
                yield return new(new(x,y),new[]{"NativeTileShop",shopId});
            if(shopId=="IslandTrade"&&location is IslandNorth&&location.getTileIndexAt(x,y,"Buildings","untitled tile sheet") is >=2074 and <=2078)
                yield return new(new(x,y),new[]{"NativeTileShop",shopId});
            if(location is BeachNightMarket market){
                var tileIndex=location.getTileIndexAt(x,y,"Buildings","night market");
                if(shopId=="Festival_NightMarket_DecorationBoat"&&tileIndex==595||shopId=="Traveler"&&tileIndex==399
                    ||shopId=="Festival_NightMarket_MagicBoat_Day"+market.getDayOfNightMarket()&&tileIndex==70)
                    yield return new(new(x,y),new[]{"NativeTileShop",shopId});
            }
        }
        if(shopId=="Traveler"&&location is Forest forest){
            var cart=forest.GetTravelingMerchantCartTile();yield return new(new(cart.X+4,cart.Y+1),new[]{"NativeTileShop",shopId});
        }
        if(shopId=="DesertTrade"&&location is Desert){
            yield return new(new(41,24),new[]{"NativeTileShop",shopId});yield return new(new(42,24),new[]{"NativeTileShop",shopId});
        }
        if(shopId=="ResortBar"&&location is IslandSouth)yield return new(new(14,22),new[]{"NativeTileShop",shopId});
        if(shopId=="ShadowShop"&&ReferenceEquals(location,Game1.currentLocation))foreach(var npc in location.characters.Where(n=>n.Name=="Krobus"))
            yield return new(new(npc.TilePoint.X,npc.TilePoint.Y),new[]{"NativeNpcShop",shopId});
    }

    private string? ShopMap(string shopId)
    {
        if(_resolvedShopMap!=null)return _resolvedShopMap;
        var current=Game1.currentLocation;
        if(current.NameOrUniqueName==Map&&WorldView.Supported(current)
            &&ExecutionTargets.AllowedLocation(current.NameOrUniqueName)&&ShopCounters(shopId,current).Any())
            return _resolvedShopMap=current.NameOrUniqueName;
        var requested=Game1.getLocationFromName(Map);
        if(WorldView.Supported(requested)&&ExecutionTargets.AllowedLocation(requested!.NameOrUniqueName)&&ShopCounters(shopId,requested).Any())return _resolvedShopMap=requested.NameOrUniqueName;
        var candidates=Game1.locations.Where(WorldView.Supported).Where(l=>ExecutionTargets.AllowedLocation(l.NameOrUniqueName)&&ShopCounters(shopId,l).Any()).ToArray();
        if(candidates.Length==0&&shopId=="ShadowShop"&&Game1.getLocationFromName("Sewer") is {} sewer&&WorldView.Supported(sewer))return _resolvedShopMap=sewer.NameOrUniqueName;
        return candidates.Length==1?_resolvedShopMap=candidates[0].NameOrUniqueName:null;
    }

    private static ShopEntrance? EntranceTo(string destination)
    {
        var choices=new List<ShopEntrance>();
        foreach(var location in Game1.locations.Where(WorldView.Supported))foreach(var door in location.doors.Pairs){
            if(door.Value!=destination)continue;
            var action=location.doesTileHaveProperty(door.Key.X,door.Key.Y,"Action","Buildings")?.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            int? open=null,close=null;
            if(action is {Length:>=6}&&action[0]=="LockedDoorWarp"){
                if(int.TryParse(action[4],out var from))open=from;
                if(int.TryParse(action[5],out var until))close=until;
            }
            choices.Add(new(location.NameOrUniqueName,new(door.Key.X,door.Key.Y),open,close));
        }
        return choices.OrderBy(c=>c.Map==Game1.currentLocation.NameOrUniqueName?0:Navigation.Route(Game1.currentLocation.NameOrUniqueName,c.Map)?.Count??int.MaxValue)
            .ThenBy(c=>c.Tile.Distance(Navigation.Current)).FirstOrDefault();
    }

    private (int? Open,int? Close,string Source) ShopHours(string shopId,string? map)
    {
        if(shopId=="Traveler"&&(map==null||Game1.getLocationFromName(map) is Forest))return (600,2000,"native_forest_traveling_cart_hours");
        var usesBuyAction=false;
        foreach(var counter in ShopCounters(shopId,map==null?null:Game1.getLocationFromName(map))){
            var a=counter.Action;usesBuyAction|=a[0]=="Buy";
            if(a[0]=="OpenShop"){
                int? open=a.Length>=4&&int.TryParse(a[3],out var from)&&from>=0?from:null;
                int? close=a.Length>=5&&int.TryParse(a[4],out var until)&&until>=0?until:null;
                if(open.HasValue||close.HasValue)return (open,close,"native_counter_action");
            }
        }
        if(shopId=="SeedShop"&&usesBuyAction)return (900,1700,"vanilla_pierre_service_hours");
        var entrance=map==null?null:EntranceTo(map);
        if(entrance?.Open!=null||entrance?.Close!=null){
            if(Game1.player.HasTownKey&&map!=null&&Game1.getLocationFromName(map).InValleyContext())return (null,null,"town_key_bypasses_door_hours; native_service_hours_unknown");
            var open=shopId=="FishShop"&&Game1.player.mailReceived.Contains("willyHours")?800:entrance.Open;
            return (open,entrance.Close,"native_entrance_hours; service_still_requires_native_menu");
        }
        return (null,null,"native_hours_unknown");
    }

    private string ShopBlocked(string shopId,string reason,string releaseCondition,int? retryAtTimeOfDayHHMM=null,object? extra=null)
    {
        var hours=ShopHours(shopId,_resolvedShopMap);
        if(_shopLastDialogue==null&&GameEventTracker.LastDialogueCapture is {} capture&&capture.Revision>_promptRevisionBefore)_shopLastDialogue=capture.Text;
        _results.Add(new{
            shopId,reason,phase=Phase,day=(int)Game1.Date.TotalDays,dayOfSeason=Game1.dayOfMonth,
            dayOfWeek=Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth),season=Game1.currentSeason,timeOfDayHHMM=Game1.timeOfDay,
            openTimeOfDayHHMM=hours.Open,closeTimeOfDayHHMM=hours.Close,hoursSource=hours.Source,
            shopDialogue=_shopLastDialogue,releaseCondition,retryAtTimeOfDayHHMM,retryTimeKnown=retryAtTimeOfDayHHMM.HasValue,
            verifiedPurchased=Completed,verifiedSpent=_totalPurchaseSpent,
            shopCurrency=Game1.activeClickableMenu is ShopMenu currentShop?(int?)currentShop.currency:null,
            knownShopIds=reason=="shop_id_unknown"?DataLoader.Shops(Game1.content).Keys.ToArray():null,
            knownShopIdsMeaning=reason=="shop_id_unknown"?"definition identifiers only; accessibility and stock require native observation":null,
            extra
        });
        _failureRecorded=true; // This receipt already contains the relevant shop evidence.
        return End(reason);
    }

    private static bool PierreClosedWednesday()=>Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)=="Wed"
        &&!Utility.HasAnyPlayerSeenEvent("191393")&&!Game1.player.HasTownKey;

    private string OpenNativeShop(string shopId,AiInputSimulator input)
    {
        if(Game1.activeClickableMenu is ShopMenu shop){
            if(shop.ShopId!=shopId){
                if(shop.heldItem!=null)return ShopBlocked(shopId,"unexpected_shop_with_held_item:"+shop.ShopId,"resolve_held_item_before_switching_shops");
                Phase="close_unrelated_idle_shop";
                if(shop.readyToClose()){_results.Add(new{closedUnrelatedShop=shop.ShopId,requestedShop=shopId});shop.exitThisMenu();}
                return "running";
            }
            _shopInteractionAt=0;_shopInteractionAttempts=0;
            _ownedMenu=shop;Phase="shop_menu_ready";return shop.safetyTimer>0?"running":"ready";
        }
        Phase="check_shop_availability";
        if(Game1.activeClickableMenu is DialogueBox dialogue){
            _shopLastDialogue=dialogue.getCurrentString();
            if(dialogue.isQuestion&&_responses.Count==0){
                var entry=shopId switch{"AnimalShop"=>"Supplies","Bookseller"=>"Buy","BooksellerTrade"=>"Trade",_=>"Shop"};
                if(!dialogue.responses.Any(r=>r.responseKey==entry))return ShopBlocked(shopId,"shop_dialogue_requires_model_choice","respond_to_observed_choices");
                _responses.Enqueue(entry);
            }
            var status=Dialogue(dialogue);if(status!="running")return status;
            Phase="acknowledging_shop_notification";return "running";
        }
        if(Game1.activeClickableMenu!=null)return ShopBlocked(shopId,"unexpected_shop_menu","close_unrelated_menu");
        if(!DataLoader.Shops(Game1.content).ContainsKey(shopId))return ShopBlocked(shopId,"shop_id_unknown","use_observed_shop_id");
        return NavigateToShopCounter(shopId,input);
    }

    private string NavigateToShopCounter(string shopId,AiInputSimulator input)
    {
        if(shopId=="HatMouse"&&Game1.player.achievements.Count==0)return ShopBlocked(shopId,"shop_not_unlocked","player_achievement_earned");
        if(shopId is "Bookseller" or "BooksellerTrade"&&!Utility.getDaysOfBooksellerThisSeason().Contains(Game1.dayOfMonth))
            return ShopBlocked(shopId,"shop_closed_for_day","native_bookseller_day_reached",extra:new{daysThisSeason=Utility.getDaysOfBooksellerThisSeason()});
        var map=ShopMap(shopId);
        if(map==null)return ShopBlocked(shopId,"shop_counter_location_unknown_or_ambiguous","specify_location_with_matching_native_counter");
        // Some native interactions (notably Carpenter) switch currentLocation while
        // their menu is opening. Keep waiting for that menu instead of interpreting
        // the temporary location as a failed cross-map route.
        if(_shopInteractionAt!=0&&Game1.currentLocation.NameOrUniqueName!=map){
            Phase="waiting_for_shop_menu";_nav.Suspend();input.Clear();
            if(_clock.Now-_shopInteractionAt<=4000)return "running";
            if(_shopInteractionAttempts>=2)return DiagnoseShopInteractionFailure(shopId);
            _shopInteractionAt=0;_nav.Reset();
        }
        var location=Game1.getLocationFromName(map);
        if(shopId=="Traveler"&&location is Forest forest&&!forest.ShouldTravelingMerchantVisitToday()&&!forest.travelingMerchantDay)
            return ShopBlocked(shopId,"shop_closed_for_day","native_traveling_merchant_day_reached");
        if(!InRequestedFestivalShop&&GameLocation.AreStoresClosedForFestival()&&location.InValleyContext())
            return ShopBlocked(shopId,"shop_closed_for_festival","day_changed_recheck_native_festival_conditions");
        if(shopId=="SeedShop"&&PierreClosedWednesday())return ShopBlocked(shopId,"shop_closed_for_day","day_or_native_shop_access_changed");
        var hours=ShopHours(shopId,map);
        if(hours.Close is {} close&&Game1.timeOfDay>=close)return ShopBlocked(shopId,"shop_closed_for_today","day_changed_recheck_opening_time");
        if(hours.Open is {} open&&Game1.timeOfDay<open){
            var remaining=Utility.ConvertTimeToMinutes(open)-Utility.ConvertTimeToMinutes(Game1.timeOfDay);
            if(remaining>30)return ShopBlocked(shopId,"shop_not_open_yet","reach_short_wait_window_or_choose_other_work",Utility.ModifyTime(open,-30),new{minutesUntilOpen=remaining});
            if(Game1.currentLocation.NameOrUniqueName!=map&&EntranceTo(map) is {} entrance){
                Phase="travel_to_shop_entrance";
                var approach=Game1.currentLocation.NameOrUniqueName==entrance.Map?Near(entrance.Map,entrance.Tile,input):_nav.Move(entrance.Map,entrance.Tile,input,_interact);
                if(approach is "blocked" or "unsupported")return ShopBlocked(shopId,"shop_entrance_unreachable:"+_nav.Reason,"route_changed");
                if(approach!="reached")return "running";
            }
            _nav.Suspend();input.Clear();Phase="waiting_for_shop_open";return "running";
        }
        if(Game1.currentLocation.NameOrUniqueName!=map){
            Phase="travel_to_shop";
            var counter=ShopCounters(shopId,location).Select(c=>(Counter:c,Approach:RemoteShopApproach(c,location)))
                .Where(c=>c.Approach.HasValue).OrderBy(c=>Navigation.Route(Game1.currentLocation.NameOrUniqueName,map)?.Count??int.MaxValue)
                .ThenBy(c=>c.Counter.Tile.Distance(new GridPoint(3,8))).FirstOrDefault();
            if(counter.Counter==null||counter.Approach==null)return ShopBlocked(shopId,"shop_counter_unreachable","route_changed");
            var travel=_nav.Move(map,counter.Approach.Value,input,_interact);
            return travel is "blocked" or "unsupported"?ShopBlocked(shopId,"shop_route_unavailable:"+_nav.Reason,"route_changed",extra:new{
                currentMap=Game1.currentLocation.NameOrUniqueName,requestedMap=Map,resolvedShopMap=map,
                requestedMapHasCounter=ShopCounters(shopId,Game1.getLocationFromName(Map)).Any(),resolvedMapHasCounter=ShopCounters(shopId,location).Any()
            }):"running";
        }
        return ApproachShopCounter(shopId,input);
    }

    private string ApproachShopCounter(string shopId,AiInputSimulator input)
    {
        if(_shopInteractionAt!=0){
            Phase="waiting_for_shop_menu";_nav.Suspend();input.Clear();
            if(_clock.Now-_shopInteractionAt<=4000)return "running";
            if(_shopInteractionAttempts>=2)return DiagnoseShopInteractionFailure(shopId);
            _shopInteractionAt=0;_nav.Reset();
        }
        var counters=ShopCounters(shopId,Game1.currentLocation).ToArray();
        if(counters.Length==0)return ShopBlocked(shopId,shopId=="ShadowShop"?"shop_owner_unavailable":"shop_counter_not_observed","counter_or_owner_observed");
        var counter=counters.OrderBy(c=>c.Tile.Distance(Navigation.Current)).FirstOrDefault(c=>ShopApproach(c)!=null);
        if(counter==null)return ShopBlocked(shopId,"shop_counter_unreachable","route_changed");
        var target=ShopApproach(counter)!.Value;Phase="approach_shop_counter";
        var moved=_nav.Move(Game1.currentLocation.NameOrUniqueName,target,input,_interact);
        if(moved is "blocked" or "unsupported")return ShopBlocked(shopId,"shop_counter_unreachable:"+_nav.Reason,"route_changed");
        if(moved=="reached"){
            _shopInteractionAt=_clock.Now;_shopInteractionAttempts++;_shopPromptRevision=GameEventTracker.DialogueRevision;
            _lastShopCounter=counter;
            _nav.Suspend();input.Clear();Game1.player.CurrentToolIndex=EmptyHand();_interact(counter.Tile);Phase="opening_shop_menu";
        }
        return "running";
    }

    private GridPoint? ShopApproach(ShopCounter counter)
    {
        var tile=counter.Tile;
        var direction=counter.Action[0] is "Buy" or "Blacksmith" or "Carpenter" or "AnimalShop" or "Saloon" or "HospitalShop" or "NativeTileShop"?"down"
            :counter.Action[0]=="OpenShop"?counter.Action.ElementAtOrDefault(2):null;
        var approach=direction switch{
            "down"=>new GridPoint(tile.X,tile.Y+1),"up"=>new GridPoint(tile.X,tile.Y-1),
            "left"=>new GridPoint(tile.X-1,tile.Y),"right"=>new GridPoint(tile.X+1,tile.Y),_=>_nav.BestAdjacent(tile)
        };
        if(approach is not {} point||!Navigation.Passable(point)&&!point.Equals(Navigation.Current))return null;
        return Pathfinding.AStar(Navigation.Current,point,p=>Navigation.Passable(p)?1:(int?)null)!=null?point:null;
    }

    private static GridPoint? RemoteShopApproach(ShopCounter counter,GameLocation location)
    {
        var tile=counter.Tile;
        var direction=counter.Action[0] is "Buy" or "Blacksmith" or "Carpenter" or "AnimalShop" or "Saloon" or "HospitalShop" or "NativeTileShop"?"down"
            :counter.Action[0]=="OpenShop"?counter.Action.ElementAtOrDefault(2):null;
        IEnumerable<GridPoint> candidates=direction switch{
            "down"=>new[]{new GridPoint(tile.X,tile.Y+1)},"up"=>new[]{new GridPoint(tile.X,tile.Y-1)},
            "left"=>new[]{new GridPoint(tile.X-1,tile.Y)},"right"=>new[]{new GridPoint(tile.X+1,tile.Y)},
            _=>tile.Neighbors()};
        foreach(var point in candidates)if(point.X>=0&&point.Y>=0&&point.X<location.Map.Layers[0].LayerWidth&&point.Y<location.Map.Layers[0].LayerHeight)return point;
        return null;
    }

    private string DiagnoseShopInteractionFailure(string shopId)
    {
        if(GameEventTracker.LastDialogueCapture is {} capture&&capture.Revision>_shopPromptRevision)_shopLastDialogue=capture.Text;
        if(DataLoader.Shops(Game1.content).TryGetValue(shopId,out var data)){
            var owners=ShopBuilder.GetCurrentOwners(data).ToArray();
            if(owners.Length==0)return ShopBlocked(shopId,"shop_unavailable_native_condition","native_shop_conditions_changed");
            var ownerless=owners.Any(o=>o.Type is ShopOwnerType.AnyOrNone or ShopOwnerType.None);
            var ownerPresent=owners.Any(o=>Game1.currentLocation.characters.Any(n=>o.IsValid(n.Name)));
            var closed=owners.FirstOrDefault(o=>o.ClosedMessage!=null&&(o.Type is ShopOwnerType.AnyOrNone or ShopOwnerType.None||Game1.currentLocation.characters.Any(n=>o.IsValid(n.Name))));
            if(closed!=null)return ShopBlocked(shopId,"shop_unavailable_native_condition","native_shop_conditions_changed",extra:new{nativeClosedMessage=StardewValley.TokenizableStrings.TokenParser.ParseText(closed.ClosedMessage)});
            var action=_lastShopCounter?.Action;
            var requiresOwner=action is {Length:>0}&&(action[0] is "Buy" or "Blacksmith" or "Carpenter" or "AnimalShop" or "Saloon" or "HospitalShop" or "IceCreamStand" or "NativeNpcShop"
                ||action[0]=="OpenShop"&&action.Length>=9||action[0]=="NativeTileShop"&&shopId is "ResortBar" or "DesertTrade");
            var islandOwner=shopId switch{"SeedShop"=>"Pierre","Saloon"=>"Gus","Carpenter"=>"Robin","AnimalShop"=>"Marnie",_=>null};
            var selfService=islandOwner!=null&&Game1.IsVisitingIslandToday(islandOwner)||shopId=="AnimalShop"&&Game1.player.stats.Get("Book_AnimalCatalogue")!=0;
            if(requiresOwner&&!ownerless&&!ownerPresent&&!selfService)
                return ShopBlocked(shopId,"shop_owner_unavailable","native_owner_availability_changed",extra:new{interactionAttempts=_shopInteractionAttempts});
            if(requiresOwner&&!ownerless&&ownerPresent&&!selfService&&_lastShopCounter is {} counter){
                var nearestOwner=Game1.currentLocation.characters.Where(n=>owners.Any(o=>o.IsValid(n.Name)))
                    .OrderBy(n=>new GridPoint(n.TilePoint.X,n.TilePoint.Y).Distance(counter.Tile)).FirstOrDefault();
                if(nearestOwner!=null&&new GridPoint(nearestOwner.TilePoint.X,nearestOwner.TilePoint.Y).Distance(counter.Tile)>2)
                    return ShopBlocked(shopId,"shop_owner_not_at_counter","wait_for_owner_to_reach_native_counter",extra:new{
                        interactionAttempts=_shopInteractionAttempts,owner=new{name=nearestOwner.Name,x=nearestOwner.TilePoint.X,y=nearestOwner.TilePoint.Y},
                        counterTile=counter.Tile,distanceTiles=new GridPoint(nearestOwner.TilePoint.X,nearestOwner.TilePoint.Y).Distance(counter.Tile)
                    });
            }
        }
        var observedLocation=Game1.currentLocation;
        var ownerNames=data?.Owners?.Select(o=>o.Name).ToArray()??Array.Empty<string>();
        var observedOwners=observedLocation.characters.Where(n=>ownerNames.Contains(n.Name,StringComparer.OrdinalIgnoreCase))
            .Select(n=>new{name=n.Name,x=n.TilePoint.X,y=n.TilePoint.Y,canSocialize=n.CanSocialize,invisible=n.IsInvisible,movingOnPath=n.isMovingOnPathFindPath.Value}).ToArray();
        return ShopBlocked(shopId,"shop_interaction_failed","counter_position_menu_or_native_availability_changed",extra:new{
            interactionAttempts=_shopInteractionAttempts,currentMap=observedLocation.NameOrUniqueName,
            playerTile=new{Game1.player.TilePoint.X,Game1.player.TilePoint.Y},facing=Game1.player.FacingDirection,
            counterTile=_lastShopCounter?.Tile,counterAction=_lastShopCounter?.Action,ownerNames,observedOwners,
            lastInteractionTile=ModEntry.LastNativeInteractionTile,lastInteractionResult=ModEntry.LastNativeInteractionResult,
            playerCanMove=Game1.player.CanMove,playerOnBridge=Game1.player.onBridge.Value,
            activeMenu=Game1.activeClickableMenu?.GetType().Name,isFading=Game1.IsFading()
        });
    }

    private static object[] ShopCatalog(ShopMenu menu)=>menu.forSale.OfType<Item>().Select(item=>{
        var stock=menu.itemPriceAndStock[item];
        return (object)new{itemName=item.DisplayName,internalName=item.Name,quality=item.Quality,unitPrice=stock.Price,
            stock=stock.Stock==int.MaxValue?(int?)null:stock.Stock,unlimitedStock=stock.Stock==int.MaxValue,bundleSize=item.Stack,tradeItem=stock.TradeItem};
    }).ToArray();

    private string ShopItemFailure(string reason,ShopMenu menu,JsonElement request)
        =>ShopBlocked(menu.ShopId,reason,"revise_names_quantities_or_budget_using_observed_catalog",extra:new{
            requested=request,remainingCount=Math.Max(0,request.GetProperty("count").GetInt32()-_before),catalog=ShopCatalog(menu),
            catalogMeaning="actual_current_shop_menu; absent name may be incorrect or not sold today; do not infer nonexistence or season restriction"
        });

    private Item? ResolveShopItem(ShopMenu menu,JsonElement request,out string? error)
    {
        error=null;
        if(!request.TryGetProperty("itemName",out var nameValue)||string.IsNullOrWhiteSpace(nameValue.GetString())){error="shop_purchase_requires_itemName";return null;}
        if(_shopPurchaseItem!=null){
            if(menu.forSale.Contains(_shopPurchaseItem))return _shopPurchaseItem;
            error="shop_item_no_longer_available";return null;
        }
        var name=nameValue.GetString()!.Trim();
        var matches=menu.forSale.OfType<Item>().Where(i=>string.Equals(i.DisplayName,name,StringComparison.Ordinal)||string.Equals(i.Name,name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(request.TryGetProperty("quality",out var quality)){
            var exact=matches.Where(i=>i.Quality==quality.GetInt32()).ToArray();
            if(matches.Length>0&&exact.Length==0){error="shop_item_quality_not_in_catalog";return null;}
            matches=exact;
        }
        if(matches.Length!=1){error=matches.Length==0?"shop_item_name_not_in_catalog":"shop_item_name_ambiguous";return null;}
        return _shopPurchaseItem=matches[0];
    }
}
