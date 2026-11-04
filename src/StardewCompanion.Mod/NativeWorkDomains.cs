using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private int _index;
    private int _moneyBefore;
    private int _quantityBefore;
    private int _totalPurchaseSpent;
    private IClickableMenu? _ownedMenu;
    private readonly Queue<string> _responses=new();
    private bool _dialogueResponsesLoaded;
    private Dictionary<string,int>? _starterGiftExpected;
    private Dictionary<string,int>? _starterGiftBefore;
    private static object? Read(object? source,string name)=>source?.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(source)
        ??(source==null?null:source.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(source));
    private static void Set(object source,string name,object? value)
    {
        var field=source.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if(field!=null)field.SetValue(source,value);
        else source.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.SetValue(source,value);
    }
    private static object? Call(object source,string name,params object?[] args)
    {
        var method=source.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).FirstOrDefault(m=>m.Name==name&&m.GetParameters().Length==args.Length)
            ??throw new InvalidOperationException("native_api_unavailable:"+name);
        return method.Invoke(source,args);
    }
    private string Domain(AiInputSimulator input)=>_action switch{
        "claim_starter_gift"=>ClaimStarterGift(input),
        "purchase_items" or "sell_items"=>Trade(input),
        "shipping"=>Ship(input),
        "pet_animal" or "animal_tool" or "feed" or "collect_animal_ground" or "collect_grabbers" or "animal_door" or "pets"=>Animals(input),
        "visit"=>Visit(input),
        "daily_info"=>Daily(input),
        "upgrade_tool" or "collect_upgraded_tool"=>ToolService(input),
        "order_building" or "modify_building"=>Buildings(input),
        "livestock"=>Livestock(input),
        "manage_quests"=>Quests(input),
        "donate_bundle"=>DonateBundle(input),
        "donate_museum"=>Museum(input),
        "complete_public_project" or "attend_event"=>PublicAction(input),
        "travel"=>Transit(input),
        _=>End("native_adapter_missing:"+_action)};
    private string ClaimStarterGift(AiInputSimulator input)
    {
        var home=Utility.getHomeOfFarmer(Game1.player);
        if(home is not FarmHouse house||!house.IsOwnedByCurrentPlayer)return End("starter_gift_home_not_owned");
        if(Game1.currentLocation!=home){var move=_nav.Move(home.NameOrUniqueName,new(4,4),input,_interact);return move is "blocked" or "unsupported"?End("starter_gift_home_unreachable:"+_nav.Reason):"running";}
        if(_starterGiftExpected!=null){
            var gains=_starterGiftExpected.ToDictionary(p=>p.Key,p=>Math.Max(0,Count(p.Key)-_starterGiftBefore!.GetValueOrDefault(p.Key)));
            if(_starterGiftExpected.All(p=>gains[p.Key]>=p.Value)){
                Completed=gains.Values.Sum();_results.Add(new{itemsAdded=gains,source="native_starter_gift"});return Finish("starter_gift_received");
            }
            if(Environment.TickCount64>=_wait){Completed=gains.Values.Sum();return End("starter_gift_inventory_gain_not_verified");}
            return "running";
        }
        var gift=home.objects.Pairs.FirstOrDefault(p=>p.Value is Chest c&&c.giftbox.Value&&c.giftboxIsStarterGift.Value);
        if(gift.Value is not Chest chest)return Finish("starter_gift_already_absent");
        if(chest.Items.Any(i=>i!=null&&!Game1.player.couldInventoryAcceptThisItem(i)))return End("starter_gift_inventory_full");
        var near=Near(home.NameOrUniqueName,new((int)gift.Key.X,(int)gift.Key.Y),input);
        if(near is "blocked" or "unsupported")return End("starter_gift_unreachable:"+_nav.Reason);
        if(near=="reached"){
            _starterGiftBefore=BusinessExecutor.Inventory();_starterGiftExpected=chest.Items.Where(i=>i!=null).GroupBy(i=>i.QualifiedItemId).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.Stack));
            if(_starterGiftExpected.Count==0)return End("starter_gift_empty");
            _wait=Environment.TickCount64+10000;_interact(new((int)gift.Key.X,(int)gift.Key.Y));
        }
        return "running";
    }
    private string OpenShop(string shopId,AiInputSimulator input)
    {
        var shopMap=shopId=="SeedShop"?"SeedShop":Map;
        var closedForDay=shopId=="SeedShop"&&Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)=="Wed"&&!Utility.HasAnyPlayerSeenEvent("191393")&&!Game1.player.HasTownKey;
        if(Game1.activeClickableMenu is ShopMenu shop){
            if(shop.ShopId!=shopId)return End("unexpected_shop:"+shop.ShopId);
            _ownedMenu=shop;return shop.safetyTimer>0?"running":"ready";
        }
        if(Game1.activeClickableMenu is DialogueBox dialogue)
        {
            var text=dialogue.getCurrentString()??"";
            _results.Add(new{shopDialogue=text,shopId,closedForDay,dayOfSeason=Game1.dayOfMonth,dayOfWeek=Weekday(Game1.dayOfMonth),season=Game1.currentSeason,timeOfDayHHMM=Game1.timeOfDay,availability="shop approach or counter interaction did not open the requested shop",nextDecision=closedForDay?"shop is closed for the rest of this day; choose another activity":"read dialogue and current calendar; choose whether to wait or revise plan"});
            return End(dialogue.isQuestion?"shop_dialogue_requires_model_choice":closedForDay?"shop_closed_for_day":"shop_closed_or_unavailable_dialogue_requires_review");
        }
        if(Game1.activeClickableMenu!=null)return End("unexpected_shop_menu");
        if(shopId=="SeedShop"&&Game1.currentLocation.NameOrUniqueName=="Town"&&Game1.timeOfDay<900&&!closedForDay)
        {
            var entrance=FindAction(a=>a.Contains("SeedShop",StringComparison.OrdinalIgnoreCase));
            if(entrance==null)return End("shop_entrance_not_observed");
            var approach=Near("Town",entrance.Value,input);
            if(approach is "blocked" or "unsupported")return End("shop_entrance_unreachable:"+_nav.Reason);
            if(approach=="reached"){_nav.Suspend();_started=Environment.TickCount64;Phase="waiting_for_shop_open";}
            return "running";
        }
        if(Game1.currentLocation.NameOrUniqueName!=shopMap){
            var moved=_nav.Move(shopMap,new(3,8),input,_interact);
            if(moved is "blocked" or "unsupported"){
                _results.Add(new{shopRouteUnavailable=true,shopId,from=Game1.currentLocation.NameOrUniqueName,to=shopMap,navigationReason=_nav.Reason,dayOfSeason=Game1.dayOfMonth,timeOfDayHHMM=Game1.timeOfDay});
                return End("shop_route_unavailable:"+_nav.Reason);
            }
            return "running";
        }
        if(_shopInteractionAttempted&&Environment.TickCount64-_started>4000)
        {
            _results.Add(new{shopAvailability="shop menu did not open",shopId,dayOfSeason=Game1.dayOfMonth,dayOfWeek=Weekday(Game1.dayOfMonth),season=Game1.currentSeason,timeOfDayHHMM=Game1.timeOfDay});
            return End("shop_closed_or_refresh_not_ready:"+shopId);
        }
        var tile=FindAction(a=>{
            var parts=a.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length<2)return false;
            if(parts[0]=="OpenShop")return parts[1]==shopId;
            return parts[0]=="Buy"&&(shopId=="SeedShop"&&Game1.currentLocation is SeedShop
                ||shopId=="FishShop"&&parts[1]=="Fish"||shopId=="Sandy"&&Game1.currentLocation.NameOrUniqueName=="SandyHouse");
        });
        if(tile==null){_results.Add(new{shopAvailability="counter_not_observed",shopId,dayOfSeason=Game1.dayOfMonth,dayOfWeek=Weekday(Game1.dayOfMonth),season=Game1.currentSeason,timeOfDayHHMM=Game1.timeOfDay});return End("shop_closed_or_counter_not_observed:"+shopId);}
        var move=Near(shopMap,tile.Value,input);if(move is "blocked" or "unsupported")return End("shop_counter_unreachable:"+_nav.Reason);
        if(move=="reached")
        {
            if(!_shopInteractionAttempted)_started=Environment.TickCount64;
            _shopInteractionAttempted=true;_interact(tile.Value);
        }
        return "running";
    }
    private static string Weekday(int day)=>new[]{"Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"}[Math.Clamp((day-1)%7,0,6)];
    private GridPoint? FindAction(Func<string,bool> predicate)
    {
        var l=Game1.currentLocation;
        GridPoint? first=null,best=null;var distance=int.MaxValue;
        for(var x=0;x<l.Map.Layers[0].LayerWidth;x++)for(var y=0;y<l.Map.Layers[0].LayerHeight;y++){
            var action=l.doesTileHaveProperty(x,y,"Action","Buildings");
            if(action==null||!predicate(action))continue;
            var tile=new GridPoint(x,y);first??=tile;
            if(_nav.BestAdjacent(tile) is not {} approach)continue;
            var cost=approach.Distance(Navigation.Current);
            if(cost<distance){best=tile;distance=cost;}
        }
        return best??first;
    }
    private string Dialogue(DialogueBox dialogue)
    {
        if(!ReferenceEquals(_ownedMenu,dialogue))_results.Add(new{dialogue=dialogue.getCurrentString()});
        _ownedMenu=dialogue;
        if(dialogue.transitioning||dialogue.safetyTimer>0)return "running";
        if(dialogue.characterIndexInDialogue<dialogue.getCurrentString().Length-1){dialogue.receiveLeftClick(dialogue.x+64,dialogue.y+64);return "running";}
        if(dialogue.isQuestion){
            if(!_dialogueResponsesLoaded&&_args.TryGetProperty("dialogueResponses",out var values)){foreach(var r in values.EnumerateArray())_responses.Enqueue(r.GetString()!);_dialogueResponsesLoaded=true;}
            if(_responses.Count==0)return End("dialogue_choice_required:"+string.Join(",",dialogue.responses.Select(r=>r.responseKey)));
            var response=_responses.Peek();var index=Array.FindIndex(dialogue.responses,r=>r.responseKey==response);
            if(index<0)return End("requested_dialogue_option_unavailable:"+response);
            dialogue.selectedResponse=index;dialogue.receiveLeftClick(0,0);
            if(dialogue.transitioning||!ReferenceEquals(Game1.activeClickableMenu,dialogue)||dialogue.selectedResponse==-1)_responses.Dequeue();
            return "running";
        }
        dialogue.receiveLeftClick(dialogue.x+64,dialogue.y+64);return "running";
    }
    private string Trade(AiInputSimulator input)
    {
        var status=OpenShop(Text("shopId"),input);if(status!="ready")return status;
        var menu=(ShopMenu)Game1.activeClickableMenu;var items=_args.GetProperty("items").EnumerateArray().ToArray();
        if(_index>=items.Length){menu.exitThisMenu();return Finish("native_trade_verified");}
        var request=items[_index];var id=request.GetProperty("itemId").GetString()!;
        var wanted=request.GetProperty("count").GetInt32();
        if(_wait==0){_quantityBefore=Count(id);_before=0;_moneyBefore=Game1.player.Money;_wait=1;}
        if(_before>=wanted){_index++;_wait=0;return "running";}
        if(_action=="purchase_items"){
            var item=menu.forSale.FirstOrDefault(i=>i is Item it&&it.QualifiedItemId==id);
            if(item==null)return End("shop_item_unavailable:"+id);
            var stock=menu.itemPriceAndStock[item];
            if(menu.currency!=0)return End("shop_currency_not_supported_by_gold_contract");
            if(stock.Price>request.GetProperty("maxUnitPrice").GetInt32()||stock.Price>Num("maxTotal")-_totalPurchaseSpent||stock.Price>Game1.player.Money)return End("purchase_budget_exceeded");
            if(stock.TradeItem!=null)return End("barter_requires_explicit_material_contract");
            if(item.Stack>wanted-_before)return End("shop_bundle_quantity_exceeds_request");
            if(((Item)item).IsRecipe&&wanted>1)return End("recipe_purchase_count_must_be_one");
            if(!Game1.player.couldInventoryAcceptThisItem((Item)item))return End("purchase_inventory_full");
            var n=menu.forSale.IndexOf(item);menu.currentItemIndex=Math.Min(n,Math.Max(0,menu.forSale.Count-4));
            var row=menu.forSaleButtons[n-menu.currentItemIndex].bounds.Center;var before=Count(id);
            var moneyBeforePurchase=Game1.player.Money;var recipesBefore=Game1.player.craftingRecipes.Count()+Game1.player.cookingRecipes.Count();menu.receiveRightClick(row.X,row.Y);_totalPurchaseSpent+=moneyBeforePurchase-Game1.player.Money;
            if(menu.heldItem is Item held){
                var empty=Game1.player.Items.IndexOf(null);if(empty<0)empty=Game1.player.Items.ToList().FindIndex(i=>i!=null&&i.canStackWith(held)&&i.Stack+held.Stack<=i.maximumStackSize());
                if(empty<0)return End("purchase_inventory_full");
                var slot=menu.inventory.inventory[empty].bounds.Center;menu.receiveLeftClick(slot.X,slot.Y);
            }
            var added=Count(id)-before;if(((Item)item).IsRecipe&&Game1.player.craftingRecipes.Count()+Game1.player.cookingRecipes.Count()>recipesBefore)added=1;if(added<=0)return End("purchase_not_verified");
            _before+=added;Completed+=added;_results.Add(new{itemId=id,added,money=Game1.player.Money});
        }else{
            var quality=request.GetProperty("quality").GetInt32();var owned=Game1.player.Items.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
            if(owned<=request.GetProperty("retain").GetInt32())return End("sale_retain_limit");
            var item=Item(id,quality);if(item==null||item is Tool||item.modData.ContainsKey("StardewCompanion/Protected"))return End("sale_item_missing_or_protected");
            var slot=menu.inventory.inventory[Game1.player.Items.IndexOf(item)].bounds.Center;var before=Count(id);
            menu.receiveRightClick(slot.X,slot.Y);var removed=before-Count(id);
            if(removed<=0)return End("shop_did_not_accept_sale");
            _before+=removed;Completed+=removed;
        }
        return "running";
    }
    private string Ship(AiInputSimulator input)
    {
        if(Game1.currentLocation is not Farm){var move=_nav.Move("Farm",new(71,14),input,_interact);return move is "blocked" or "unsupported"?End("shipping_route_unavailable"):"running";}
        var bin=Game1.currentLocation.buildings.FirstOrDefault(b=>b.buildingType.Value=="Shipping Bin");
        var tile=bin==null?new GridPoint(71,14):new GridPoint(bin.tileX.Value,bin.tileY.Value);
        var near=Near("Farm",tile,input);if(near is "blocked" or "unsupported")return End("shipping_bin_unreachable");if(near!="reached")return "running";
        if(Game1.activeClickableMenu is not ItemGrabMenu menu){_interact(tile);return "running";}
        _ownedMenu=menu;var items=_args.GetProperty("items").EnumerateArray().ToArray();
        if(_index>=items.Length){menu.exitThisMenu();return Finish("items_shipped_pending_night_settlement");}
        var entry=items[_index];var id=entry.GetProperty("itemId").GetString()!;var quality=entry.GetProperty("quality").GetInt32();
        if(_before>=entry.GetProperty("count").GetInt32()){_before=0;_index++;return "running";}
        var item=Item(id,quality);var count=Game1.player.Items.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
        if(item==null||item is Tool||item.modData.ContainsKey("StardewCompanion/Protected"))return End("shipping_item_missing_or_protected");
        if(count<=entry.GetProperty("retain").GetInt32())return End("shipping_retain_limit");
        var slot=menu.inventory.inventory[Game1.player.Items.IndexOf(item)].bounds.Center;var before=Count(id);
        menu.receiveRightClick(slot.X,slot.Y);var transferred=before-Count(id);
        if(transferred<=0)return End("shipping_transfer_not_verified");
        Completed+=transferred;_before+=transferred;return "running";
    }
    private bool CleanupDomain()
    {
        if(_ownedMenu!=null&&ReferenceEquals(Game1.activeClickableMenu,_ownedMenu)){
            // DialogueBox must complete through its native response/outro path;
            // exitThisMenu bypasses callbacks and can leave player control frozen.
            if(_ownedMenu is DialogueBox)return true;
            if(_ownedMenu is MenuWithInventory inventory&&inventory.heldItem is {} held){
                if(!Game1.player.addItemToInventoryBool(held))Game1.createItemDebris(held,Game1.player.Position,Game1.player.FacingDirection,Game1.currentLocation);
                inventory.heldItem=null;
            }
            if(!_ownedMenu.readyToClose())return false;_ownedMenu.exitThisMenu();
        }
        return true;
    }
    private string CollectNativeMenu(ItemGrabMenu menu)
    {
        _ownedMenu=menu;
        var index=menu.ItemsToGrabMenu.actualInventory.ToList().FindIndex(i=>i!=null);
        if(index<0){menu.exitThisMenu();return "completed";}
        if(index>=menu.ItemsToGrabMenu.inventory.Count)return End("native_collection_slot_not_visible");
        var item=menu.ItemsToGrabMenu.actualInventory[index];
        if(!Game1.player.couldInventoryAcceptThisItem(item))return End("collection_inventory_full");
        var id=item.QualifiedItemId;var before=Count(id);var click=menu.ItemsToGrabMenu.inventory[index].bounds.Center;
        menu.receiveLeftClick(click.X,click.Y);
        if(menu.heldItem is {} held){
            var slot=Game1.player.Items.ToList().FindIndex(i=>i!=null&&i.canStackWith(held)&&i.Stack+held.Stack<=i.maximumStackSize());
            if(slot<0)slot=Game1.player.Items.IndexOf(null);if(slot<0||slot>=menu.inventory.inventory.Count)return End("collection_inventory_full");
            var target=menu.inventory.inventory[slot].bounds.Center;menu.receiveLeftClick(target.X,target.Y);
        }
        var gained=Count(id)-before;
        if(gained>0){Completed+=gained;return "running";}
        if(item.IsRecipe&&!menu.ItemsToGrabMenu.actualInventory.Contains(item)){Completed++;return "running";}
        return End("native_collection_not_verified");
    }
}
