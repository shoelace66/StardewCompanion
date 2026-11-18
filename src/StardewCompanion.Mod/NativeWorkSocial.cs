using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Quests;

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private bool _dailySourceObserved;
    private long _dailyPromptRevision;
    private int _dailyUnavailable;
    private Dictionary<(string ItemId,int Quality),int>? _mailInventoryBefore;
    private int _mailMoneyBefore;
    private bool _mailSnapshotTaken;
    private static Dictionary<(string ItemId,int Quality),int> MailInventory()=>Game1.player.Items.Where(i=>i!=null).GroupBy(i=>(i.QualifiedItemId,i.Quality)).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.Stack));
    private bool _specialOrderAcceptRequested;
    private long _specialOrderAcceptDeadline;
    private string? _visitedNpc;
    private int? _visitPointsBefore;
    private string Visit(AiInputSimulator input)
    {
        if(Game1.activeClickableMenu is DialogueBox dialogue)return Dialogue(dialogue);
        if(Game1.activeClickableMenu!=null)return End("unexpected_npc_interaction_menu");
        var found=EntityLocator.Npc(Text("npc"),out var lookupReason);
        if(found==null)return End(lookupReason!);
        if(!EntityLocator.CanEnter(found.Location))return End("npc_location_disabled:"+found.Location.NameOrUniqueName);
        var npc=found.Npc;_resolvedEntityMap=found.Location.NameOrUniqueName;
        if(_visitedNpc==null){_visitedNpc=npc.Name;_visitPointsBefore=Game1.player.friendshipData.TryGetValue(npc.Name,out var initial)?initial.Points:0;}
        var point=new GridPoint(npc.TilePoint.X,npc.TilePoint.Y);var near=Near(Map,point,input);
        if(near is "blocked" or "unsupported")return End("npc_unreachable");if(near!="reached")return "running";
        if(_index==0&&Flag("talk")){
            Game1.player.friendshipData.TryGetValue(npc.Name,out var friendship);
            var beforePoints=friendship?.Points;var talkedBefore=friendship?.TalkedToToday??false;
            Game1.player.CurrentToolIndex=EmptyHand();var handled=npc.checkAction(Game1.player,Game1.currentLocation);
            if(handled&&Game1.activeClickableMenu!=null)_ownedMenu=Game1.activeClickableMenu;
            Game1.player.friendshipData.TryGetValue(npc.Name,out friendship);
            var dialogueOpened=handled&&Game1.activeClickableMenu is DialogueBox;
            var verified=dialogueOpened||!talkedBefore&&friendship?.TalkedToToday==true;
            _results.Add(new{npc=npc.Name,name=npc.displayName,talkVerified=verified,dialogueOpened,friendshipBefore=beforePoints,friendshipAfter=friendship?.Points,talkedToToday=friendship?.TalkedToToday});
            if(!verified)return End("npc_talk_not_verified");
            _index=1;Completed++;return "running";
        }
        if(Text("giftId")!=""){
            if(_index<2){
                var gift=Item(Text("giftId"),Num("quality")) as StardewValley.Object;
                if(gift==null||gift.modData.ContainsKey("StardewCompanion/Protected"))return End("gift_missing_or_protected");
                Game1.player.friendshipData.TryGetValue(npc.Name,out var friendship);
                var giftsBefore=friendship?.GiftsToday??0;var pointsBefore=friendship?.Points;
                _before=Count(gift.QualifiedItemId);Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(gift);
                var received=npc.tryToReceiveActiveObject(Game1.player);
                Game1.player.friendshipData.TryGetValue(npc.Name,out friendship);
                var consumed=_before-Count(gift.QualifiedItemId);
                var verified=received&&consumed==1&&(friendship?.GiftsToday>giftsBefore||gift.QualifiedItemId=="(O)StardropTea"&&friendship?.Points!=pointsBefore);
                _results.Add(new{npc=npc.Name,itemId=gift.QualifiedItemId,consumed,giftVerified=verified,friendshipBefore=pointsBefore,friendshipAfter=friendship?.Points,giftsToday=friendship?.GiftsToday});
                if(!verified){
                    if(gift.QualifiedItemId!="(O)StardropTea"&&giftsBefore>=1)return End("gift_daily_limit_reached:retry_next_day");
                    if(gift.QualifiedItemId!="(O)StardropTea"&&friendship?.GiftsThisWeek>=2)return End("gift_weekly_limit_reached:recheck_next_week_or_native_exception");
                    return End(received?"gift_consumption_or_friendship_not_verified":"native_gift_limit_or_rejection");
                }
                _index=2;Completed++;return "running";
            }
        }
        Game1.player.friendshipData.TryGetValue(npc.Name,out var finalFriendship);
        _results.Add(new{npc=npc.Name,name=npc.displayName,friendshipBefore=_visitPointsBefore,friendshipAfter=finalFriendship?.Points,
            friendshipDelta=_visitPointsBefore.HasValue&&finalFriendship!=null?(int?)(finalFriendship.Points-_visitPointsBefore.Value):null,
            talkedToToday=finalFriendship?.TalkedToToday,giftsToday=finalFriendship?.GiftsToday});
        return Finish("requested_villager_interactions_completed");
    }
    private string Daily(AiInputSimulator input)
    {
        var sources=_args.GetProperty("sources").EnumerateArray().Select(s=>s.GetString()!).ToArray();
        if(_index>=sources.Length)return _dailyUnavailable>0?End("daily_sources_partly_unavailable"):Finish("accessible_daily_sources_read");
        var source=sources[_index];
        if(source=="quests"){
            _results.Add(new{source,quests=Game1.player.questLog.Select(q=>new{id=QuestIdentity.For(q),title=q.questTitle,completed=q.completed.Value}).ToArray(),
                specialOrders=Game1.player.team.specialOrders.Select(q=>new{questId=q.questKey.Value,title=q.GetName(),description=q.GetDescription()}).ToArray()});
            _index++;Completed++;return "running";
        }
        if(Game1.activeClickableMenu is DialogueBox dialogue){
            if(_wait!=0&&!dialogue.isQuestion)_dailySourceObserved=true;
            if(source=="recipes"&&dialogue.isQuestion&&!dialogue.responses.Any(r=>r.responseKey=="The")){
                if(dialogue.transitioning||dialogue.safetyTimer>0)return "running";
                if(dialogue.characterIndexInDialogue<dialogue.getCurrentString().Length-1){dialogue.receiveLeftClick(dialogue.x+64,dialogue.y+64);return "running";}
                var leave=Array.FindIndex(dialogue.responses,r=>r.responseKey=="(Leave)");
                if(leave<0)return End("recipe_channel_unavailable_today");
                dialogue.selectedResponse=leave;dialogue.receiveLeftClick(0,0);
                if(!dialogue.transitioning&&ReferenceEquals(Game1.activeClickableMenu,dialogue))return "running";
                _responses.Clear();_wait=0;_index++;_dailyUnavailable++;
                _results.Add(new{source,available=false,reason="recipe_channel_not_in_native_choices"});return "running";
            }
            return Dialogue(dialogue);
        }
        if(Game1.activeClickableMenu is LetterViewerMenu letter){
            if(source!="mail")return End("unexpected_daily_source_menu:LetterViewerMenu");
            _ownedMenu=letter;
            if(letter.readyToClose()){
                var pages=letter.mailMessage.ToArray();
                var attachments=letter.itemsToGrab.Where(c=>c.item!=null).GroupBy(c=>(c.item.QualifiedItemId,c.item.Quality)).Select(g=>new{itemId=g.Key.QualifiedItemId,quality=g.Key.Quality,count=g.Sum(c=>c.item.Stack)}).ToArray();
                var letterId=letter.mailTitle;var moneyIncluded=letter.moneyIncluded;
                letter.exitThisMenu();
                var inventory=MailInventory();
                var received=attachments.Select(a=>new{a.itemId,a.quality,expected=a.count,received=_mailInventoryBefore==null?(int?)null:Math.Max(0,inventory.GetValueOrDefault((a.itemId,a.quality))-_mailInventoryBefore.GetValueOrDefault((a.itemId,a.quality)))}).ToArray();
                var pending=Game1.activeClickableMenu is ItemGrabMenu;
                _results.Add(new{source,letterId,content=string.Join("\n",pages),pages,moneyIncluded,moneyDelta=_mailSnapshotTaken?(int?)(Game1.player.Money-_mailMoneyBefore):null,attachments=received,claimState=pending?"pending_native_inventory_claim":_mailSnapshotTaken?"native_close_effects_observed":"opened_before_task_effects_unknown"});
                _mailSnapshotTaken=false;_mailInventoryBefore=null;
                if(pending)return End("mail_attachment_claim_pending_inventory_space");
                _wait=0;_dailySourceObserved=false;_responses.Clear();Completed++;
                if(Game1.player.mailbox.Count==0)_index++;
            }
            return "running";
        }
        if(Game1.activeClickableMenu is Billboard board){
            if(source is not ("calendar" or "help_wanted"))return End("unexpected_daily_source_menu:Billboard");
            _ownedMenu=board;
            if(Read(board,"dailyQuestBoard") is not bool questBoard||questBoard!=(source=="help_wanted"))return End("daily_source_board_mismatch");
            if(source=="calendar")_results.Add(new{source,season=Game1.currentSeason,days=board.calendarDayData.OrderBy(d=>d.Key).Select(d=>new{dayOfSeason=d.Key,events=d.Value.HoverText}).ToArray(),meaning="native_visible_calendar; days_without_events_omitted"});
            else _results.Add(new{source,available=Game1.questOfTheDay!=null,quest=Game1.questOfTheDay is {} quest?new{questId=QuestIdentity.For(quest),title=quest.questTitle,description=quest.questDescription,accepted=quest.accepted.Value}:null});
            board.exitThisMenu();_wait=0;_dailySourceObserved=false;_index++;Completed++;return "running";
        }
        if(Game1.activeClickableMenu is SpecialOrdersBoard orders){
            _ownedMenu=orders;
            if(source!="special_orders")return End("unexpected_daily_source_menu:SpecialOrdersBoard");
            _results.Add(new{source,orders=new[]{orders.leftOrder,orders.rightOrder}.Where(q=>q!=null).Select(q=>new{questId=q.questKey.Value,title=q.GetName(),description=q.GetDescription()}).ToArray()});
            orders.exitThisMenu();_wait=0;_dailySourceObserved=false;_index++;Completed++;return "running";
        }
        if(Game1.activeClickableMenu!=null){_ownedMenu=Game1.activeClickableMenu;return End("daily_source_menu_not_adapted:"+Game1.activeClickableMenu.GetType().Name);}
        if(_wait!=0&&Game1.activeClickableMenu==null){
            if(GameEventTracker.LastDialogueCapture is {Question:false} capture&&capture.Revision>_dailyPromptRevision){
                _dailySourceObserved=true;_results.Add(new{source,content=capture.Text,evidence="script_captured_native_notification"});
            }
            if(!_dailySourceObserved)return _clock.Now-_wait<4000?"running":End("daily_source_interaction_not_verified:"+source);
            if(source=="special_orders"){
                _results.Add(new{source,available=false,reason="native_prompt_without_accessible_order_board"});_dailyUnavailable++;_index++;_wait=0;_dailySourceObserved=false;return "running";
            }
            _index++;Completed++;_wait=0;_dailySourceObserved=false;_responses.Clear();return "running";
        }
        if(source=="special_orders"&&!StardewValley.SpecialOrders.SpecialOrder.IsSpecialOrdersBoardUnlocked())
            return End("special_orders_board_locked:requires_days_played_58");
        var location=source=="mail"?"Farm":source is "calendar" or "help_wanted" or "special_orders"?"Town":Utility.getHomeOfFarmer(Game1.player).NameOrUniqueName;
        if(Game1.currentLocation.NameOrUniqueName!=location){var move=_nav.Move(location,new(3,8),input,_interact);return move is "blocked" or "unsupported"?End("daily_source_route_unavailable:"+source):"running";}
        GridPoint? tile;
        if(source is "weather" or "fortune" or "recipes"){
            var tv=Game1.currentLocation.furniture.OfType<TV>().FirstOrDefault();if(tv==null)return End("television_not_observed");
            tile=null;var distance=int.MaxValue;
            for(var x=(int)tv.TileLocation.X;x<(int)tv.TileLocation.X+tv.getTilesWide();x++)for(var y=(int)tv.TileLocation.Y;y<(int)tv.TileLocation.Y+tv.getTilesHigh();y++){
                var candidate=new GridPoint(x,y);if(_nav.BestAdjacent(candidate) is not {} approach)continue;
                var cost=approach.Distance(Navigation.Current);if(cost<distance){tile=candidate;distance=cost;}
            }
            if(_responses.Count==0)_responses.Enqueue(source=="weather"?"Weather":source=="fortune"?"Fortune":"The");
        }else if(source=="mail"){
            var mailbox=Game1.player.getMailboxPosition();tile=new(mailbox.X,mailbox.Y);
        }else tile=FindAction(a=>{
            var parts=a.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length==0)return false;
            return source=="special_orders"?parts[0]=="SpecialOrders":parts[0]=="Billboard"&&((parts.ElementAtOrDefault(1)=="3")== (source=="help_wanted"));
        })??(source=="special_orders"?FindCheckAction(a=>a.StartsWith("SpecialOrders",StringComparison.Ordinal)):null);
        if(tile==null)return End(source=="special_orders"?"special_orders_board_map_action_missing":"daily_source_not_observed:"+source);
        var near=Near(location,tile.Value,input);if(near is "blocked" or "unsupported")return End("daily_source_unreachable");
        if(near=="reached"){
            if(source=="mail"&&!_mailSnapshotTaken){_mailInventoryBefore=MailInventory();_mailMoneyBefore=Game1.player.Money;_mailSnapshotTaken=true;}
            _dailyPromptRevision=GameEventTracker.DialogueRevision;_interact(tile.Value);_wait=_clock.Now;
        }return "running";
    }
    private string Quests(AiInputSimulator input)
    {
        if(_specialOrderAcceptRequested){
            if(Game1.player.team.specialOrders.Any(q=>q.questKey.Value==Text("questId"))){Completed=1;return Finish("special_order_acceptance_verified");}
            return _clock.Now<_specialOrderAcceptDeadline?"running":End("special_order_acceptance_not_verified");
        }
        var quest=Game1.player.questLog.FirstOrDefault(q=>QuestIdentity.For(q)==Text("questId"));
        if(Text("operation")=="accept"){
            if(quest!=null||Game1.player.team.specialOrders.Any(q=>q.questKey.Value==Text("questId")))return Finish("quest_already_active_no_new_acceptance");
            _resolvedEntityMap="Town";
            if(Game1.currentLocation.NameOrUniqueName!=Map){var move=_nav.Move(Map,Navigation.Current,input,_interact);return move is "blocked" or "unsupported"?End("quest_board_location_unreachable"):"running";}
            var daily=QuestIdentity.For(Game1.questOfTheDay)==Text("questId");
            var tile=FindAction(a=>{
                var parts=a.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length==0)return false;
                return daily?parts[0]=="Billboard"&&parts.ElementAtOrDefault(1)=="3":parts[0]=="SpecialOrders";
            })??(!daily?FindCheckAction(a=>a.StartsWith("SpecialOrders",StringComparison.Ordinal)):null);
            if(Game1.activeClickableMenu is SpecialOrdersBoard orders){
                _ownedMenu=orders;
                var button=orders.leftOrder?.questKey.Value==Text("questId")?orders.acceptLeftQuestButton
                    :orders.rightOrder?.questKey.Value==Text("questId")?orders.acceptRightQuestButton:null;
                if(button==null||!button.visible)return End("requested_special_order_not_available");
                var point=button.bounds.Center;ClickMenu(orders,point.X,point.Y);_specialOrderAcceptRequested=true;_specialOrderAcceptDeadline=_clock.Now+5000;return "running";
            }
            if(Game1.activeClickableMenu is Billboard board){
                if(QuestIdentity.For(Game1.questOfTheDay)!=Text("questId"))return End("requested_quest_not_on_board");
                _ownedMenu=board;var accept=Read(board,"acceptQuestButton") as ClickableComponent;
                if(accept==null)return End("quest_accept_option_not_available");
                var p=accept.bounds.Center;board.receiveLeftClick(p.X,p.Y);
                if(Game1.player.questLog.Any(q=>QuestIdentity.For(q)==Text("questId"))){Completed=1;return Finish("quest_accepted");}
                return End("requested_quest_not_accepted");
            }
            if(tile==null)return End(!daily&&!StardewValley.SpecialOrders.SpecialOrder.IsSpecialOrdersBoardUnlocked()?"special_orders_board_locked:requires_days_played_58":!daily?"special_orders_board_map_action_missing":"quest_board_not_observed");
            var near=Near(Map,tile.Value,input);if(near is "blocked" or "unsupported")return End("quest_board_unreachable");if(near=="reached")_interact(tile.Value);return "running";
        }
        if(quest==null)return End("quest_not_active");
        if(Text("operation")=="deliver"){
            if(_index==0&&quest.completed.Value)return Finish("quest_already_complete_no_delivery");
            if(quest is not ItemDeliveryQuest delivery)return End("quest_delivery_type_requires_specific_adapter");
            if(Text("npc")!=""&&delivery.target.Value!=Text("npc"))return End("quest_delivery_npc_mismatch");
            var found=EntityLocator.Npc(delivery.target.Value,out var lookupReason);if(found==null)return End(lookupReason!);
            if(!EntityLocator.CanEnter(found.Location))return End("quest_npc_location_disabled:"+found.Location.NameOrUniqueName);
            var npc=found.Npc;_resolvedEntityMap=found.Location.NameOrUniqueName;
            var tile=new GridPoint(npc.TilePoint.X,npc.TilePoint.Y);var near=Near(Map,tile,input);if(near is "blocked" or "unsupported")return End("quest_npc_unreachable");if(near!="reached")return "running";
            if(Game1.activeClickableMenu is DialogueBox dialogue)return Dialogue(dialogue);
            var requests=_args.GetProperty("items").EnumerateArray().ToArray();
            if(_index<requests.Length){
                var r=requests[_index];var item=Item(r.GetProperty("itemId").GetString()!,r.GetProperty("quality").GetInt32());
                if(item==null)return End("quest_delivery_item_missing");
                if(item.QualifiedItemId!=delivery.ItemId.Value||r.GetProperty("count").GetInt32()<delivery.number.Value||item.modData.ContainsKey("StardewCompanion/Protected"))return End("quest_delivery_contract_mismatch");
                var before=Count(item.QualifiedItemId);Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(item);
                if(!npc.tryToReceiveActiveObject(Game1.player)||before-Count(item.QualifiedItemId)!=delivery.number.Value)return End("quest_delivery_consumption_not_verified");
                _results.Add(new{questId=QuestIdentity.For(quest),npc=npc.Name,itemId=item.QualifiedItemId,quality=item.Quality,delivered=before-Count(item.QualifiedItemId),questCompleted=quest.completed.Value});
                _index++;return "running";
            }
            if(quest.completed.Value){Completed=1;return Finish("quest_delivery_verified");}return End("quest_delivery_conditions_not_satisfied");
        }
        if(Text("operation")=="claim"&&!quest.completed.Value)return End("quest_not_complete");
        var page=new QuestLog();Game1.activeClickableMenu=page;_ownedMenu=page;
        return QuestMenuOperation(page,quest);
    }
    private string QuestMenuOperation(QuestLog menu,StardewValley.Quests.Quest quest)
    {
        // Use the journal's own selection and claim/cancel handlers rather than editing gold or quest state.
        var pages=Read(menu,"pages") as System.Collections.IList;
        if(pages==null)return End("native_quest_journal_layout_unavailable");
        for(var i=0;i<pages.Count;i++){
            if(pages[i] is not System.Collections.IEnumerable entries)continue;
            var index=0;foreach(var entry in entries){
                if(ReferenceEquals(entry,quest)){Set(menu,"currentPage",i);var row=menu.questLogButtons[index].bounds.Center;menu.receiveLeftClick(row.X,row.Y);
                    if(!ReferenceEquals(Read(menu,"_shownQuest"),quest))return End("quest_journal_selection_not_verified");
                    if(Text("operation")=="cancel"&&!quest.CanBeCancelled())return End("quest_cannot_be_cancelled");
                    if(Text("operation")=="claim"&&!quest.HasMoneyReward())return End("quest_has_no_money_reward");
                    var reward=quest.GetMoneyReward();var before=Game1.player.Money;
                    var button=Read(menu,Text("operation")=="claim"?"rewardBox":"cancelQuestButton") as ClickableComponent;
                    if(button==null)return End("quest_requested_action_not_available");
                    var point=button.bounds.Center;menu.receiveLeftClick(point.X,point.Y);
                    if(Text("operation")=="claim"&&quest.GetMoneyReward()>0)menu.receiveLeftClick(point.X,point.Y+48);
                    var verified=Text("operation")=="claim"?quest.GetMoneyReward()==0&&Game1.player.Money-before==reward:!Game1.player.questLog.Contains(quest);
                    _results.Add(new{operation=Text("operation"),questId=QuestIdentity.For(quest),expectedReward=Text("operation")=="claim"?reward:0,
                        moneyBefore=before,moneyAfter=Game1.player.Money,moneyDelta=Game1.player.Money-before,rewardRemaining=quest.GetMoneyReward(),questStillInLog=Game1.player.questLog.Contains(quest),verified});
                    if(!verified)return End("quest_journal_action_not_verified");Completed=1;return Finish("quest_journal_action_verified");}
                index++;
            }
        }
        return End("quest_journal_entry_not_found");
    }
}
