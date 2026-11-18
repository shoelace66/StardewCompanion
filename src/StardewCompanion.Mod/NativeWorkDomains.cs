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
            if(_clock.Now>=_wait){Completed=gains.Values.Sum();return End("starter_gift_inventory_gain_not_verified");}
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
            _wait=_clock.Now+10000;_interact(new((int)gift.Key.X,(int)gift.Key.Y));
        }
        return "running";
    }
    private string OpenShop(string shopId,AiInputSimulator input)=>OpenNativeShop(shopId,input);
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
        var purchasing=_action=="purchase_items";
        if(purchasing&&Text("shopId") is "ClintUpgrade" or "PetAdoption")
            return ShopBlocked(Text("shopId"),"shop_requires_specialized_service_adapter",Text("shopId")=="ClintUpgrade"?"use_upgrade_tool":"pet_adoption_adapter_not_implemented");
        if(VerifyPendingShopEffect() is {} effectStatus)return effectStatus;
        if(purchasing&&_args.GetProperty("items").EnumerateArray().Any(i=>!i.TryGetProperty("itemName",out var name)||string.IsNullOrWhiteSpace(name.GetString())))
            return ShopBlocked(Text("shopId"),"shop_purchase_requires_itemName","use_itemName_instead_of_itemId");
        var status=OpenShop(Text("shopId"),input);if(status!="ready")return status;
        var menu=(ShopMenu)Game1.activeClickableMenu;var items=_args.GetProperty("items").EnumerateArray().ToArray();
        if(_index>=items.Length){
            if(purchasing)_results.Add(new{shopId=menu.ShopId,verifiedPurchased=Completed,verifiedSpent=_totalPurchaseSpent,money=Game1.player.Money});
            menu.exitThisMenu();return Finish("native_trade_verified");
        }
        var request=items[_index];
        var wanted=request.GetProperty("count").GetInt32();
        if(_wait!=0&&_before>=wanted){_index++;_wait=0;_before=0;_shopPurchaseItem=null;return "running";}
        Item? namedItem=null;
        if(purchasing){
            namedItem=ResolveShopItem(menu,request,out var error);
            if(error!=null)return ShopItemFailure(error,menu,request);
        }
        var id=purchasing?namedItem!.QualifiedItemId:request.GetProperty("itemId").GetString()!;
        if(_wait==0){_quantityBefore=Count(id);_before=0;_moneyBefore=Game1.player.Money;_wait=1;}
        if(_action=="purchase_items"){
            var item=purchasing?namedItem:menu.forSale.FirstOrDefault(i=>i is Item it&&it.QualifiedItemId==id);
            if(item==null)return End("shop_item_unavailable:"+id);
            var stock=menu.itemPriceAndStock[item];
            if(menu.currency!=0)return ShopItemFailure("shop_currency_not_supported_by_gold_contract",menu,request);
            if(stock.TradeItem!=null)return ShopItemFailure("barter_requires_explicit_material_contract",menu,request);
            if(purchasing){
                Phase="verify_purchase_conditions";
                if(stock.Stock<=0)return ShopItemFailure("shop_item_sold_out",menu,request);
                if((long)stock.Stock*Math.Max(1,item.Stack)<wanted-_before)
                    return ShopItemFailure("shop_stock_insufficient",menu,request);
                var remainingCost=(long)stock.Price*((wanted-_before+Math.Max(1,item.Stack)-1)/Math.Max(1,item.Stack));
                if(stock.Price>request.GetProperty("maxUnitPrice").GetInt32()||remainingCost>Num("maxTotal")-_totalPurchaseSpent||remainingCost>Game1.player.Money)
                    return ShopItemFailure("purchase_budget_exceeded",menu,request);
            }
            if(item.Stack>wanted-_before)return End("shop_bundle_quantity_exceeds_request");
            if(((Item)item).IsRecipe&&wanted>1)return End("recipe_purchase_count_must_be_one");
            if(!Game1.player.couldInventoryAcceptThisItem((Item)item))return End("purchase_inventory_full");
            var n=menu.forSale.IndexOf(item);menu.currentItemIndex=Math.Min(n,Math.Max(0,menu.forSale.Count-4));
            var row=menu.forSaleButtons[n-menu.currentItemIndex].bounds.Center;var before=Count(id);
            var moneyBeforePurchase=Game1.player.Money;var staminaBeforePurchase=Game1.player.MaxStamina;
            var recipesBefore=Game1.player.craftingRecipes.Count()+Game1.player.cookingRecipes.Count();ClickMenu(menu,row.X,row.Y,true);
            var paid=moneyBeforePurchase-Game1.player.Money;_totalPurchaseSpent+=paid;
            if(id=="(O)434"){
                if(paid!=stock.Price)return ShopItemFailure("purchase_payment_not_verified",menu,request);
                _pendingShopEffect=new(id,namedItem!.DisplayName,namedItem.Quality,staminaBeforePurchase,paid,_clock.Now+20000);return "running";
            }
            if(menu.heldItem is Item held){
                var empty=Game1.player.Items.IndexOf(null);if(empty<0)empty=Game1.player.Items.ToList().FindIndex(i=>i!=null&&i.canStackWith(held)&&i.Stack+held.Stack<=i.maximumStackSize());
                if(empty<0)return End("purchase_inventory_full");
                var slot=menu.inventory.inventory[empty].bounds.Center;ClickMenu(menu,slot.X,slot.Y);
            }
            var added=Count(id)-before;if(((Item)item).IsRecipe&&Game1.player.craftingRecipes.Count()+Game1.player.cookingRecipes.Count()>recipesBefore)added=1;if(added<=0)return End("purchase_not_verified");
            _before+=added;Completed+=added;
            if(purchasing)_results.Add(new{itemName=namedItem!.DisplayName,itemId=id,quality=namedItem.Quality,added,spent=moneyBeforePurchase-Game1.player.Money,
                verifiedPurchased=Completed,verifiedSpent=_totalPurchaseSpent,money=Game1.player.Money});
            else _results.Add(new{itemId=id,added,money=Game1.player.Money});
            if(paid!=stock.Price)return ShopItemFailure("purchase_payment_not_verified",menu,request);
        }else{
            var quality=request.GetProperty("quality").GetInt32();var owned=Game1.player.Items.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
            if(menu.currency!=0)return End("shop_sale_currency_not_supported_by_gold_contract");
            if(owned<=request.GetProperty("retain").GetInt32())return End("sale_retain_limit");
            var item=Item(id,quality);if(item==null||item is Tool||item.modData.ContainsKey("StardewCompanion/Protected"))return End("sale_item_missing_or_protected");
            var slot=menu.inventory.inventory[Game1.player.Items.IndexOf(item)].bounds.Center;var before=Count(id);
            var moneyBeforeSale=Game1.player.Money;ClickMenu(menu,slot.X,slot.Y,true);var removed=before-Count(id);var income=Game1.player.Money-moneyBeforeSale;
            if(removed<=0){
                var nativeReason=GameEventTracker.LastDialogueCapture is {} refusal&&refusal.Revision>_promptRevisionBefore?refusal.Text:null;
                _results.Add(new{itemId=id,quality,requested=wanted-_before,removed=0,income=0,reason="shop_item_not_accepted",nativePrompt=nativeReason});
                return End("shop_item_not_accepted");
            }
            if(income<0||removed>wanted-_before)return End("shop_sale_not_verified");
            if(income==0){
                Completed+=removed;_results.Add(new{itemId=id,itemName=item.DisplayName,quality,removed,income,reason="sale_gold_not_verified",money=Game1.player.Money});
                return End("shop_sale_item_removed_without_gold_verification");
            }
            _results.Add(new{itemId=id,itemName=item.DisplayName,quality,removed,income,money=Game1.player.Money,settled=true});
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
        if(!menu.shippingBin)return End("unexpected_shipping_container");
        _ownedMenu=menu;var items=_args.GetProperty("items").EnumerateArray().ToArray();
        if(_index>=items.Length){menu.exitThisMenu();return Finish("items_shipped_pending_night_settlement");}
        var entry=items[_index];var id=entry.GetProperty("itemId").GetString()!;var quality=entry.GetProperty("quality").GetInt32();
        if(_before>=entry.GetProperty("count").GetInt32()){_before=0;_index++;return "running";}
        var item=Item(id,quality);var count=Game1.player.Items.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
        if(item==null||item is Tool||item.modData.ContainsKey("StardewCompanion/Protected"))return End("shipping_item_missing_or_protected");
        if(count<=entry.GetProperty("retain").GetInt32())return End("shipping_retain_limit");
        var shipped=Game1.getFarm().getShippingBin(Game1.player);
        var shippedBefore=shipped.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
        var slot=menu.inventory.inventory[Game1.player.Items.IndexOf(item)].bounds.Center;var before=Count(id);
        ClickMenu(menu,slot.X,slot.Y,true);var transferred=before-Count(id);
        var shippingGain=shipped.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack)-shippedBefore;
        if(transferred<=0||shippingGain!=transferred)return End("shipping_transfer_not_verified");
        _results.Add(new{itemId=id,quality,transferred,settled=false,meaning="in_shipping_bin_pending_night_settlement"});
        Completed+=transferred;_before+=transferred;return "running";
    }
    private bool CleanupDomain()
    {
        if(_eventTrade?.Cleanup()==false)return false;
        if(_ownedMenu!=null&&ReferenceEquals(Game1.activeClickableMenu,_ownedMenu)){
            // DialogueBox must complete through its native response/outro path;
            // exitThisMenu bypasses callbacks and can leave player control frozen.
            if(_ownedMenu is DialogueBox)return true;
            if(_ownedMenu is MenuWithInventory inventory&&inventory.heldItem is {} held){
                if(!Game1.player.addItemToInventoryBool(held))Game1.createItemDebris(held,Game1.player.Position,Game1.player.FacingDirection,Game1.currentLocation);
                inventory.heldItem=null;
            }
            if(!_ownedMenu.readyToClose())
            {
                // Some native pages (notably JunimoNoteMenu after a rejected
                // slot or a completed donation) never report readyToClose while
                // their inner page is open. Give the game's transition a short
                // window, then close the adapter-owned page so a completed or
                // failed tool cannot remain in cleanup forever.
                if(Environment.TickCount64-_cleanupStartedAt<2500)return false;
            }
            _ownedMenu.exitThisMenu();
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
        var recipesBefore=Game1.player.craftingRecipes.Count()+Game1.player.cookingRecipes.Count();
        ClickMenu(menu,click.X,click.Y);
        if(menu.heldItem is {} held){
            var slot=Game1.player.Items.ToList().FindIndex(i=>i!=null&&i.canStackWith(held)&&i.Stack+held.Stack<=i.maximumStackSize());
            if(slot<0)slot=Game1.player.Items.IndexOf(null);if(slot<0||slot>=menu.inventory.inventory.Count)return End("collection_inventory_full");
            var target=menu.inventory.inventory[slot].bounds.Center;ClickMenu(menu,target.X,target.Y);
        }
        var gained=Count(id)-before;
        if(gained>0){Completed+=gained;return "running";}
        if(item.IsRecipe&&!menu.ItemsToGrabMenu.actualInventory.Contains(item)&&Game1.player.craftingRecipes.Count()+Game1.player.cookingRecipes.Count()>recipesBefore){Completed++;return "running";}
        return End("native_collection_not_verified");
    }
}
