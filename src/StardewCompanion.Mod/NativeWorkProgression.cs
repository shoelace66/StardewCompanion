using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private bool _eventExitRequested;
    private long _eventShopOpenedAt;
    private bool _eventDialogueObserved;
    private string? _eventObservedId;
    private bool _bundleSlotVerified;
    private bool _bundleDonationRecorded;
    private bool _bundleRewardRequested;
    private bool _bundlePageCloseRequested;
    private long _bundlePageCloseAt;
    private bool _museumRewardRequested;
    private long _museumCounterInteractionAt;
    private int _museumCounterInteractions;
    private bool _museumIntroDialogueObserved;
    private int? _travelMoneyBefore;
    private string? _travelExpectedMap;
    private long _travelInteractionAt;
    private int _travelAttempts;
    private string? _travelItemId;
    private int _travelItemBefore;
    private bool _travelItemVerified;
    private bool _travelItemPressSent;
    private bool _travelItemPressReleased;
    private long _travelPromptRevision;
    private bool _travelRepairPrompt;
    private bool _travelRepairDeclined;
    private int _travelBatteryBefore;
    private GridPoint? FindCheckAction(Func<string,bool> predicate)
    {
        var location=Game1.currentLocation;var layer=location.Map.Layers.FirstOrDefault(l=>l.Id=="Buildings");
        if(layer==null)return null;
        GridPoint? first=null,best=null;var bestDistance=int.MaxValue;
        for(var x=0;x<layer.LayerWidth;x++)for(var y=0;y<layer.LayerHeight;y++){
            var value=location.doesTileHaveProperty(x,y,"checkAction","Buildings")
                ??location.doesTileHaveProperty(x,y,"CheckAction","Buildings");
            if(value==null||!predicate(value))continue;
            var tile=new GridPoint(x,y);first??=tile;
            if(_nav.BestAdjacent(tile) is not {} approach)continue;
            var distance=approach.Distance(Navigation.Current);if(distance<bestDistance){best=tile;bestDistance=distance;}
        }
        return best??first;
    }
    private GridPoint? FindTransportEntrance(string method,Func<string,bool> predicate)
    {
        var location=Game1.currentLocation;
        int[] tileIndexes=method switch
        {
            "bus" when location is StardewValley.Locations.BusStop=>new[]{1057},
            "minecart" when location is StardewValley.Locations.BusStop=>new[]{958,1080,1081},
            // The minecart hub is also at Mountain; its vanilla handler uses the same Buildings tile indexes as BusStop.
            "minecart" when location is StardewValley.Locations.Mountain=>new[]{958,1080,1081},
            _=>Array.Empty<int>()
        };
        if(tileIndexes.Length==0)return FindCheckAction(predicate);
        GridPoint? first=null,best=null;var bestDistance=int.MaxValue;
        foreach(var tileIndex in tileIndexes)
            for(var x=0;x<location.Map.Layers[0].LayerWidth;x++)for(var y=0;y<location.Map.Layers[0].LayerHeight;y++)
            {
                if(location.getTileIndexAt(x,y,"Buildings","outdoors")!=tileIndex)continue;
                var tile=new GridPoint(x,y);first??=tile;
                if(_nav.BestAdjacent(tile) is not {} approach)continue;
                var distance=approach.Distance(Navigation.Current);if(distance<bestDistance){best=tile;bestDistance=distance;}
            }
        return best??first??FindCheckAction(predicate);
    }
    private string DonateBundle(AiInputSimulator input)
    {
        if(Game1.activeClickableMenu is ItemGrabMenu rewards){
            Phase="bundle_reward_collection";
            if(!_bundleRewardRequested)return End("unexpected_bundle_reward_container");
            var collected=CollectNativeMenu(rewards);return collected=="completed"?Finish("bundle_rewards_collected"):collected;
        }
        if(Game1.currentLocation is not CommunityCenter center){
            Phase="bundle_route_to_community_center";
            var move=_nav.Move("CommunityCenter",new(20,17),input,_interact);return move is "blocked" or "unsupported"?End("community_center_unreachable"):"running";
        }
        if(Game1.activeClickableMenu is not JunimoNoteMenu menu){
            Phase="bundle_open_native_menu";
            bool IsRequestedScroll(string action)=>action.Split(' ',StringSplitOptions.RemoveEmptyEntries) is var parts
                &&parts.Length>=2&&parts[0]=="JunimoNote"&&parts[1]==Num("area").ToString();
            var tile=FindAction(IsRequestedScroll)??FindCheckAction(IsRequestedScroll);
            if(tile==null)return End("requested_bundle_scroll_not_observed");
            var near=Near("CommunityCenter",tile.Value,input);if(near is "blocked" or "unsupported")return End("bundle_scroll_unreachable");if(near=="reached")_interact(tile.Value);return "running";
        }
        _ownedMenu=menu;
        Phase="bundle_native_menu_ready";
        if(menu.scrambledText)return End("junimo_language_not_unlocked");
        if(_bundleSlotVerified){
            Phase="bundle_verify_reward";
            if(!Flag("claimReward"))return Finish("bundle_donation_verified");
            if(menu.specificBundlePage){
                if(menu.currentPageBundle.completionTimer>0)return "running";
                Phase="bundle_close_completed_page";
                if(!_bundlePageCloseRequested){
                    _bundlePageCloseRequested=true;_bundlePageCloseAt=_clock.Now;menu.takeDownBundleSpecificPage();return "running";
                }
                if(_clock.Now-_bundlePageCloseAt<2500)return "running";
                return End("bundle_completed_page_did_not_close");
            }
            _bundlePageCloseRequested=false;_bundlePageCloseAt=0;
            if(menu.presentButton==null)return Finish("bundle_slot_verified_no_unclaimed_reward");
            _bundleRewardRequested=true;var reward=menu.presentButton.bounds.Center;ClickMenu(menu,reward.X,reward.Y);return "running";
        }
        var complete=center.bundles[Num("bundleId")];
        var slot=Num("slot");if(slot<0||slot>=complete.Length)return End("invalid_bundle_slot");
        // A completed page is closed by the vanilla menu update. Check the
        // requested slot on the area overview first so reward-only requests
        // don't reopen that page every tick and starve the reward button.
        if(complete[slot]){
            if(_index>0){
                // The vanilla network bundle array can update one tick after
                // the menu consumes the item. Treat this as our verified
                // donation, not as a pre-existing completed slot.
                RecordBundleDonation(slot);_bundleSlotVerified=true;return "running";
            }
            if(!Flag("claimReward"))return End("bundle_slot_already_complete");
            _bundleSlotVerified=true;
            _results.Add(new{bundleId=Num("bundleId"),slot,itemId=Text("itemId"),quality=Num("quality"),donated=0,alreadyComplete=true,verified=true});
            return "running";
        }
        if(!menu.specificBundlePage){
            Phase="bundle_select_requested_page";
            var bundle=menu.bundles.FirstOrDefault(b=>b.bundleIndex==Num("bundleId"));
            if(bundle==null)return End("bundle_not_in_requested_area");
            Call(menu,"setUpBundleSpecificPage",bundle);return "running";
        }
        Phase="bundle_verify_requested_slot";
        if(menu.currentPageBundle.bundleIndex!=Num("bundleId"))return End("unexpected_bundle");
        if(slot>=menu.ingredientSlots.Count)return End("invalid_bundle_slot");
        var id=Text("itemId");var item=menu.heldItem??Item(id,Num("quality"));if(item==null)return End("bundle_item_or_quality_missing");
        if(item.QualifiedItemId!=id||item.Quality!=Num("quality"))return End("bundle_held_item_identity_mismatch");
        if(item.modData.ContainsKey("StardewCompanion/Protected"))return End("bundle_item_protected");
        if(_index>=Num("count"))return End("bundle_partial_quantity_not_committed");
        if(menu.heldItem==null){
            var click=menu.inventory.inventory[Game1.player.Items.IndexOf(item)].bounds.Center;
            ClickMenu(menu,click.X,click.Y,true);
        }
        if(menu.heldItem==null)return End("bundle_item_not_selected");
        // Vanilla compacts already-completed ingredients into the first visual
        // rows, so ingredientSlots[requestedSlot] is not the requested bundle
        // slot once any earlier requirement is complete. Find an empty native
        // slot that accepts the exact held item instead.
        var targetSlot=menu.ingredientSlots.FirstOrDefault(candidate=>candidate.item==null&&menu.currentPageBundle.canAcceptThisItem(item,candidate));
        if(targetSlot==null)return End("bundle_requested_item_not_accepted_by_vanilla_bundle");
        var target=targetSlot.bounds.Center;var before=Count(id)+(menu.heldItem?.Stack??0);ClickMenu(menu,target.X,target.Y);
        var consumed=before-Count(id)-(menu.heldItem?.Stack??0);
        if(consumed<=0){
            _results.Add(new{bundleId=Num("bundleId"),slot,itemId=id,quality=Num("quality"),inventoryCount=Count(id),heldItemId=menu.heldItem?.QualifiedItemId,heldItemCount=menu.heldItem?.Stack,slotItemId=targetSlot.item?.QualifiedItemId,slotItemCount=targetSlot.item?.Stack,slotBounds=targetSlot.bounds.ToString(),slotAcceptsItem=menu.currentPageBundle.canAcceptThisItem(item,targetSlot),pageComplete=menu.currentPageBundle.complete,clickEnabled=JunimoNoteMenu.canClick});
            return End("bundle_requested_item_not_accepted");
        }
        _index+=consumed;
        if(slot<complete.Length&&complete[slot]){
            RecordBundleDonation(slot);_bundleSlotVerified=true;return "running";
        }
        return "running";
    }
    private void RecordBundleDonation(int slot)
    {
        Completed=Math.Max(Completed,_index);
        if(_bundleDonationRecorded)return;
        _bundleDonationRecorded=true;
        _results.Add(new{bundleId=Num("bundleId"),slot,itemId=Text("itemId"),quality=Num("quality"),donated=_index,verified=true});
    }
    private string Museum(AiInputSimulator input)
    {
        if(Game1.currentLocation is not LibraryMuseum museum){
            var move=_nav.Move("ArchaeologyHouse",new(3,8),input,_interact);return move is "blocked" or "unsupported"?End("museum_unreachable"):"running";
        }
        var items=_args.GetProperty("items").EnumerateArray().ToArray();
        if(_index>=items.Length){
            if(!Flag("claimRewards"))return Finish("specified_museum_items_donated");
            if(Game1.activeClickableMenu is ItemGrabMenu rewards){
                if(!_museumRewardRequested)return End("unexpected_museum_reward_container");
                var collected=CollectNativeMenu(rewards);return collected=="completed"?Finish("museum_rewards_collected"):collected;
            }
            if(Game1.activeClickableMenu is MuseumMenu donation){donation.exitThisMenu();return "running";}
            if(museum.getRewardsForPlayer(Game1.player).Count==0)return Finish("museum_donations_verified_no_unclaimed_rewards");
            if(Game1.activeClickableMenu is DialogueBox rewardDialogue){
                if(rewardDialogue.isQuestion&&_responses.Count==0&&rewardDialogue.responses.Any(r=>r.responseKey=="Collect")){_responses.Enqueue("Collect");_museumRewardRequested=true;}
                return Dialogue(rewardDialogue);
            }
            var rewardCounter=FindAction(a=>a.Contains("Gunther"));if(rewardCounter==null)return End("museum_counter_not_observed");
            var approach=Near(Map,rewardCounter.Value,input);if(approach is "blocked" or "unsupported")return End("museum_counter_unreachable");if(approach=="reached")_interact(rewardCounter.Value);return "running";
        }
        var entry=items[_index];var id=entry.GetProperty("itemId").GetString()!;var target=new Vector2(entry.GetProperty("x").GetInt32(),entry.GetProperty("y").GetInt32());
        if(museum.museumPieces.Values.Contains(ItemRegistry.GetDataOrErrorItem(id).ItemId)){_index++;return "running";}
        if(Game1.activeClickableMenu is not MuseumMenu menu){
            if(Game1.activeClickableMenu is DialogueBox dialogue){
                _museumIntroDialogueObserved=true;
                if(_responses.Count==0&&dialogue.isQuestion&&dialogue.responses.Any(r=>r.responseKey=="Donate"))_responses.Enqueue("Donate");
                return Dialogue(dialogue);
            }
            // Gunther's first-visit greeting is a non-question dialogue. Once
            // the script has advanced it, route to the same native museum menu
            // opened by his Donate dialogue option instead of greeting him again.
            if(_museumIntroDialogueObserved&&Game1.activeClickableMenu==null){
                Game1.activeClickableMenu=new MuseumMenu(null);_ownedMenu=Game1.activeClickableMenu;_museumIntroDialogueObserved=false;Phase="museum_native_menu_opened_after_intro";return "running";
            }
            // A first visit can route the Museum action through Gunther's
            // introductory dialogue. Do not click the counter again while
            // that dialogue is being advanced; otherwise each completed line
            // reopens the greeting and the donation menu is starved.
            if(_museumCounterInteractionAt!=0){
                if(_clock.Now-_museumCounterInteractionAt<1500)return "running";
                if(_museumCounterInteractions>=2)return End("museum_menu_not_opened_after_counter_interactions");
            }
            // Prefer the museum donation counter action. The nearby Gunther
            // NPC action can open his greeting repeatedly without opening the
            // native MuseumMenu needed for item donation.
            var counter=FindAction(a=>a.Contains("Museum",StringComparison.OrdinalIgnoreCase))
                ??FindAction(a=>a.Contains("Gunther",StringComparison.OrdinalIgnoreCase));
            if(counter==null)return End("museum_counter_not_observed");
            var near=Near(museum.NameOrUniqueName,counter.Value,input);if(near is "blocked" or "unsupported")return End("museum_counter_unreachable");if(near=="reached"){
                _museumCounterInteractions++;_museumCounterInteractionAt=_clock.Now;Phase="museum_counter_interaction_"+_museumCounterInteractions;_interact(counter.Value);
            }return "running";
        }
        _ownedMenu=menu;
        var item=menu.heldItem??Item(id);if(item==null)return End("museum_donation_item_missing");
        if(item.QualifiedItemId!=id)return End("museum_held_item_identity_mismatch");
        if(item.modData.ContainsKey("StardewCompanion/Protected"))return End("museum_donation_item_protected");
        if(!museum.isItemSuitableForDonation(item)||!museum.isTileSuitableForMuseumPiece((int)target.X,(int)target.Y))return End("museum_item_or_display_tile_invalid");
        if(menu.heldItem==null){var slot=menu.inventory.inventory[Game1.player.Items.IndexOf(item)].bounds.Center;menu.receiveLeftClick(slot.X,slot.Y);return "running";}
        var click=ScreenTile((int)target.X,(int)target.Y);ClickWorldTile(menu,click);
        if(!museum.museumPieces.TryGetValue(target,out var donated)||donated!=item.ItemId)return End("museum_donation_not_verified");
        _results.Add(new{itemId=item.QualifiedItemId,x=(int)target.X,y=(int)target.Y,donatedItemId=donated,verified=true});
        Completed++;_index++;return "running";
    }
    private string PublicAction(AiInputSimulator input)
    {
        if(_action=="complete_public_project")return End("public_project_requires_project_specific_budget_adapter");
        var current=Game1.currentLocation.currentEvent;
        var eventId=Text("eventId");
        var eventLocation=Game1.locations.FirstOrDefault(location=>location.currentEvent?.id==eventId)?.NameOrUniqueName
            ??(current?.id==eventId?Game1.currentLocation.NameOrUniqueName:Map);
        if(current!=null&&current.id==eventId){eventLocation=Game1.currentLocation.NameOrUniqueName;_resolvedEntityMap=eventLocation;}
        if(current!=null&&current.id==Text("eventId")&&Text("activity")=="leave"&&Game1.activeClickableMenu is ConfirmationDialog confirmation){
            if(Read(confirmation,"onConfirm") is not ConfirmationDialog.behavior callback || callback != current.forceEndFestival)
                return End("unexpected_event_confirmation");
            _eventObservedId=current.id;_eventExitRequested=true;_wait=_clock.Now+8000;
            _ownedMenu=confirmation;var button=confirmation.okButton.bounds.Center;ClickMenu(confirmation,button.X,button.Y);return "running";
        }
        if(_eventExitRequested){
            if(current==null||current.id!=_eventObservedId){Completed=1;return Finish("event_exit_verified");}
            if(Game1.activeClickableMenu is DialogueBox exitDialogue)return Dialogue(exitDialogue);
            return _clock.Now<_wait?"running":End("event_exit_not_verified");
        }
        if(Game1.currentLocation.NameOrUniqueName!=eventLocation){var move=_nav.Move(eventLocation,new(3,8),input,_interact);return move is "blocked" or "unsupported"?End("public_action_route_unavailable:"+_nav.Reason):"running";}
        if(Game1.activeClickableMenu is DialogueBox dialogue){_eventDialogueObserved=true;return Dialogue(dialogue);}
        if(_action=="attend_event"&&Text("activity")=="minigame")return End("event_minigame_not_adapted:"+Text("eventId"));
        if(Text("activity")=="shop"){
            if(current!=null&&current.id!=Text("eventId"))return End("event_identity_mismatch");
            return EventShop(input,eventLocation);
        }
        if(Text("activity")=="dialogue"){
            if(!_eventDialogueObserved){
                if(current==null||current.id!=eventId)return End("event_dialogue_requires_active_matching_event");
                var actors=Game1.currentLocation.characters.OfType<NPC>()
                    .Where(npc=>Text("npcName")==""||npc.Name.Equals(Text("npcName"),StringComparison.OrdinalIgnoreCase)
                        ||npc.displayName.Equals(Text("npcName"),StringComparison.OrdinalIgnoreCase))
                    .OrderBy(npc=>new GridPoint(npc.TilePoint.X,npc.TilePoint.Y).Distance(Navigation.Current)).ToArray();
                if(actors.Length==0)return End(Text("npcName")==""?"event_dialogue_npc_not_observed":"event_dialogue_npc_not_found:"+Text("npcName"));
                var npc=actors[0];
                var point=new GridPoint(npc.TilePoint.X,npc.TilePoint.Y);var near=Near(eventLocation,point,input);
                if(near is "blocked" or "unsupported")return End("event_dialogue_npc_unreachable:"+_nav.Reason);
                if(near!="reached")return "running";
                Game1.player.CurrentToolIndex=EmptyHand();var handled=npc.checkAction(Game1.player,Game1.currentLocation);
                if(handled&&Game1.activeClickableMenu is DialogueBox){_eventDialogueObserved=true;_ownedMenu=Game1.activeClickableMenu;return "running";}
                return End(handled?"event_npc_interaction_opened_no_dialogue":"event_npc_interaction_rejected:"+npc.Name);
            }
            Completed=1;return Finish("event_dialogue_acknowledged");
        }
        if(current!=null){
            if(current.id!=Text("eventId"))return End("event_identity_mismatch");
            if(Text("activity")=="enter"){Completed=1;_results.Add(new{eventId=current.id,isFestival=current.isFestival});return Finish("event_entered_verified");}
            if(Text("activity")=="leave"){
                if(current.isFestival && !Game1.IsMultiplayer && Game1.activeClickableMenu==null){
                    if(!current.TryStartEndFestivalDialogue(Game1.player))return End("festival_exit_dialogue_unavailable");
                    _ownedMenu=Game1.activeClickableMenu;
                    return "running";
                }
                // Festival exits are not always Leave*/Exit* Building actions; many
                // are TouchAction warps on the Back layer or a differently named
                // Action. Search Action/CheckAction/TouchAction layers and match
                // leave/exit names case-insensitively so the exit is observed.
                Func<string,bool> isExit=a=>a.Contains("Leave",StringComparison.OrdinalIgnoreCase)||a.Contains("Exit",StringComparison.OrdinalIgnoreCase);
                var exit=FindAction(isExit)??FindCheckAction(isExit)??FindTouchAction(isExit);if(exit==null)return End("event_exit_not_observed");
                var near=Near(eventLocation,exit.Value,input);
                if(near is "blocked" or "unsupported")return End("event_exit_unreachable");
                if(near=="reached"){_eventObservedId=current.id;_eventExitRequested=true;_wait=_clock.Now+8000;_interact(exit.Value);}return "running";
            }
        }
        return End("requested_event_not_active");
    }
    private string EventShop(AiInputSimulator input,string eventLocation)
    {
        if(Num("count")<=0||Text("itemName")=="")return End("event_purchase_requires_name_and_positive_count");
        if(_eventTrade==null){
            var shopId=Game1.activeClickableMenu is ShopMenu menu?menu.ShopId:Text("shopId");
            if(shopId==""){
                if(_eventShopOpenedAt!=0)return _clock.Now-_eventShopOpenedAt<4000?"running":End("event_shop_interaction_did_not_open_menu");
                var tile=FindAction(action=>{
                    var parts=action.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length>=2&&parts[0]=="Shop";
                });
                if(tile==null)return End("event_shop_not_observed:requires_native_Shop_action_or_open_menu");
                var near=Near(eventLocation,tile.Value,input);
                if(near is "blocked" or "unsupported")return End("event_shop_unreachable");
                if(near=="reached"){_eventShopOpenedAt=_clock.Now;_interact(tile.Value);}
                return "running";
            }
            _eventTrade=new("purchase_items",System.Text.Json.JsonSerializer.Serialize(new{
                location=eventLocation,shopId,maxTotal=Num("maxCost"),items=new[]{new{itemName=Text("itemName"),count=Num("count"),maxUnitPrice=Num("maxCost")}}
            }),_interact){_festivalShop=true};
        }
        var status=_eventTrade.Tick(input);Completed=_eventTrade.Completed;
        Phase="event_shop:"+_eventTrade.Phase;
        return status=="completed"?Finish("event_purchase_verified"):status=="running"?status:End(_eventTrade.Reason);
    }
    private string Transit(AiInputSimulator input)
    {
        var method=Text("method");
        if(_travelRepairPrompt&&Game1.activeClickableMenu is not DialogueBox){
            var consumed=_travelBatteryBefore-Count("(O)787");_travelRepairPrompt=false;
            if(_travelRepairDeclined)return End("transport_repair_declined_battery_unchanged");
            if(consumed!=5)return End("transport_repair_battery_consumption_not_verified");
            _results.Add(new{repair="ticket_machine",itemId="(O)787",consumed,verified=true});
            _travelInteractionAt=0;_travelAttempts=0;_nav.Reset();return "running";
        }
        // Some game versions close the ticket-repair dialogue without leaving
        // a readable DialogueBox snapshot. The exact five-item inventory delta
        // is still authoritative evidence that repair was accepted; continue
        // the requested trip instead of misreporting that prompt as failure.
        if(method=="boat"&&_travelInteractionAt!=0&&Game1.activeClickableMenu is not DialogueBox
            &&_travelBatteryBefore-Count("(O)787")==5){
            _results.Add(new{repair="ticket_machine",itemId="(O)787",consumed=5,verified=true,evidence="native_inventory_delta"});
            _travelInteractionAt=0;_travelAttempts=0;_nav.Reset();return "running";
        }
        _travelMoneyBefore??=Game1.player.Money;
        var spent=_travelMoneyBefore.Value-Game1.player.Money;
        if(spent>Num("maxCost"))return End("travel_actual_cost_exceeded_contract");
        if(_travelItemId!=null&&!_travelItemVerified){
            if(Game1.currentLocation.NameOrUniqueName==_travelExpectedMap){
                input.Tool=false;_travelItemPressSent=false;
                var consumed=_travelItemBefore-Count(_travelItemId);
                if(method=="totem"&&consumed!=1||method=="return_scepter"&&consumed!=0)return End("travel_item_consumption_not_verified");
                _travelItemVerified=true;_results.Add(new{itemId=_travelItemId,consumed,arrived=Game1.currentLocation.NameOrUniqueName});
            }else{
                if(_travelItemPressSent){input.Tool=false;_travelItemPressSent=false;_travelItemPressReleased=true;return "running";}
                if(!_travelItemPressReleased){
                    var item=Item(_travelItemId);
                    if(item==null)return End("travel_item_missing_after_use_attempt");
                    Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(item);
                    input.Tool=true;_travelItemPressSent=true;return "running";
                }
                return _clock.Now-_travelInteractionAt<20000?"running":End("travel_item_destination_not_verified");
            }
        }
        if(Game1.currentLocation.NameOrUniqueName==Map||_travelItemVerified||_travelInteractionAt!=0&&Game1.currentLocation.NameOrUniqueName==_travelExpectedMap){
            var destination=_nav.Move(Map,new(Num("x"),Num("y")),input,_interact);
            if(destination=="reached"){Completed=1;_results.Add(new{method,destination=Map,x=Num("x"),y=Num("y"),actualCost=spent,verified=true});return Finish("travel_destination_observed");}
            return destination is "blocked" or "unsupported"?End("travel_destination_tile_unreachable"):"running";
        }
        if(method=="horse"){
            if(Game1.player.mount==null){var horse=Game1.currentLocation.characters.OfType<StardewValley.Characters.Horse>().FirstOrDefault();if(horse==null)return End("horse_not_observed");
                var near=Near(Game1.currentLocation.NameOrUniqueName,new(horse.TilePoint.X,horse.TilePoint.Y),input);if(near is "blocked" or "unsupported")return End("horse_unreachable");
                if(near=="reached")horse.checkAction(Game1.player,Game1.currentLocation);return "running";}
            return _nav.Move(Map,new(Num("x"),Num("y")),input,_interact) is "blocked" or "unsupported"?End("horse_route_unavailable"):"running";
        }
        if(method is "totem" or "return_scepter"){
            var item=Item(Text("itemId"));if(item==null||item.modData.ContainsKey("StardewCompanion/Protected"))return End("travel_item_missing_or_protected");
            _travelExpectedMap=method=="return_scepter"&&item is Wand?"Farm":method=="totem"?item.QualifiedItemId switch{
                "(O)688"=>"Farm","(O)689"=>"Mountain","(O)690"=>"Beach","(O)261"=>"Desert","(O)886"=>"IslandSouth",_=>null}:null;
            if(_travelExpectedMap==null)return End("travel_item_not_supported_for_method");
            if(Map!=_travelExpectedMap&&!(_travelExpectedMap=="Farm"&&Map==Utility.getHomeOfFarmer(Game1.player).NameOrUniqueName))return End("travel_item_destination_mismatch");
            _travelItemId=item.QualifiedItemId;_travelItemBefore=Count(_travelItemId);_travelInteractionAt=_clock.Now;
            if(method=="totem"){
                // Vanilla activates held objects from pressActionButton via
                // performUseAction, not the use-tool input used by the wand.
                // Use that same native activation and consumption sequence;
                // destination and exact item loss are verified on later ticks.
                Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(item);
                if(item is not StardewValley.Object totem||!totem.performUseAction(Game1.currentLocation))return End("travel_item_use_rejected_by_native_conditions");
                Game1.player.reduceActiveItemByOne();_travelItemPressSent=false;_travelItemPressReleased=true;
                _results.Add(new{itemId=_travelItemId,activation="native_performUseAction",destinationVerificationPending=true});
                return "running";
            }
            _travelItemPressReleased=false;Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(item);input.Tool=true;_travelItemPressSent=true;return "running";
        }
        if(Game1.activeClickableMenu is DialogueBox dialogue){
            if(_travelInteractionAt==0)return End("unexpected_transport_dialogue");
            if(_responses.Count==0&&dialogue.isQuestion){
                var prompt=GameEventTracker.LastDialogueCapture?.Text??"";
                var asksRepair=method=="boat"&&(prompt.Contains("battery",StringComparison.OrdinalIgnoreCase)||prompt.Contains("电池",StringComparison.Ordinal));
                if(asksRepair){
                    var yes=dialogue.responses.FirstOrDefault(r=>r.responseKey=="Yes");var no=dialogue.responses.FirstOrDefault(r=>r.responseKey=="No");
                    if(yes==null||no==null)return End("transport_repair_choices_unavailable");
                    _travelRepairPrompt=true;_travelBatteryBefore=Count("(O)787");
                    _travelRepairDeclined=_travelBatteryBefore<5;_responses.Enqueue(_travelRepairDeclined?"No":"Yes");
                }else if(method=="minecart"){
                    // The native minecart menu calls the Bus Stop choice "Bus",
                    // while our world map and travel API call that location "BusStop".
                    var responseKey=Map is "BusStop" or "Bus Stop"?"Bus":Map;
                    _responses.Enqueue(responseKey);
                }else _responses.Enqueue("Yes");
            }
            return Dialogue(dialogue);
        }
        if(_travelInteractionAt!=0){
            // BusStop.answerDialogue deducts the fare before its native departure animation finishes.
            // Treat the observed fare as accepted and wait for the real map transition.
            if(method=="bus"&&spent>=500&&Game1.currentLocation.NameOrUniqueName!=_travelExpectedMap){
                if(_clock.Now-_travelInteractionAt<20000)return "running";
                return End("bus_departure_not_observed_after_fare");
            }
            if(_clock.Now-_travelInteractionAt<4000)return "running";
            if(GameEventTracker.LastDialogueCapture is {} capture&&capture.Revision>_travelPromptRevision){_results.Add(new{nativePrompt=capture.Text});return End("transport_unavailable_native_prompt");}
            if(_travelAttempts>=2)return End("transport_interaction_failed");
            _travelInteractionAt=0;_nav.Reset();
        }
        if(method=="bus"&&Map is not ("Desert" or "SandyHouse" or "Club"))return End("bus_destination_not_supported_by_native_desert_route");
        if(method=="boat"&&!Map.StartsWith("Island",StringComparison.Ordinal)&&Map!="QiNutRoom")return End("boat_destination_not_supported_by_native_island_route");
        var source=method=="bus"?"BusStop":method=="boat"?"BoatTunnel":Game1.currentLocation.NameOrUniqueName;
        if(Game1.currentLocation.NameOrUniqueName!=source)return _nav.Move(source,new(9,9),input,_interact) is "blocked" or "unsupported"?End("transport_source_unreachable"):"running";
        var matches=method=="bus"?(Func<string,bool>)(a=>a.Contains("Bus",StringComparison.OrdinalIgnoreCase)||a.Contains("BusStop",StringComparison.OrdinalIgnoreCase))
            :method=="boat"?a=>a.Contains("Boat",StringComparison.OrdinalIgnoreCase)
            :a=>a.Contains("Minecart",StringComparison.OrdinalIgnoreCase);
        // Bus and minecart routes are native CheckAction entrances. Action
        // tiles near them often belong to signs and are not a usable ticket
        // interaction, so prefer the real entrance; retain the map Action
        // fallback only for the boat path where maps expose it directly.
        // BusStop.checkAction is keyed by native Buildings tile index (1057),
        // not a CheckAction map property. Minecart pads use native tile indexes too.
        var action=FindTransportEntrance(method,matches)??(method=="boat"?FindAction(matches):null);
        if(action==null){
            var unlocked=method switch{
                "bus"=>Game1.player.mailReceived.Contains("ccVault")||Game1.player.HasTownKey,
                "minecart"=>Game1.player.mailReceived.Contains("ccBoilerRoom"),
                _=>true};
            return End(unlocked?"transport_check_action_not_observed":"transport_locked:"+method);
        }
        var nearAction=Near(source,action.Value,input);if(nearAction is "blocked" or "unsupported")return End("transport_unreachable");
        if(nearAction=="reached"){
            if(method=="bus"&&(Num("maxCost")<500||Game1.player.Money<500)||method=="boat"&&(Num("maxCost")<1000||Game1.player.Money<1000))return End("transport_budget_exceeded");
            _travelExpectedMap=method=="bus"?"Desert":method=="boat"?"IslandSouth":Map;
            if(method=="boat")_travelBatteryBefore=Count("(O)787");
            _travelInteractionAt=_clock.Now;_travelAttempts++;_travelPromptRevision=GameEventTracker.DialogueRevision;
            _interact(action.Value);
        }return "running";
    }
}
