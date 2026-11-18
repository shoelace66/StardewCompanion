using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.Tools;
using StardewValley.Internal;
using Microsoft.Xna.Framework.Input;

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private string? _collectedUpgradeId;
    private HashSet<long>? _livestockBefore;
    private bool _livestockSubmitted;
    private string? _livestockExpectedType;
    private string? _livestockExpectedHome;
    private int _livestockExpectedPrice;
    private int _livestockMoneyBefore;
    private long _livestockChoiceAt;
    private int? _animalSaleMoneyBefore;
    private long _animalSaleDeadline;
    private bool _animalRehomeRequested;
    private string? _animalRehomeTarget;
    private long _animalRehomeDeadline;
    private Point? _animalRehomeClick;
    private long _animalRehomeClickAt;
    private int _livestockChoiceRetries;
    private HashSet<Guid>? _constructionBefore;
    private bool _buildingUpgradeRequested;
    private bool _buildingMoveRequested;
    private int _buildingQuotedCost;
    private int _buildingMoneyBefore;
    private bool _buildingRecoveredFromMenuTransition;
    private FarmAnimal? _managedAnimal;
    private StardewValley.Object? _incubationTarget;
    private string? _incubationEggId;
    private int _incubationEggBefore;
    private long _incubationDeadline;
    private static string AnimalOfferName(ClickableTextureComponent button)
        =>Game1.farmAnimalData.TryGetValue(button.hoverText,out var data)
            ?StardewValley.TokenizableStrings.TokenParser.ParseText(data.ShopDisplayName??data.DisplayName)??button.hoverText
            :button.item?.DisplayName??button.hoverText;
    private IEnumerable<FarmAnimal> OwnedLivestock()=>Game1.getFarm().animals.Values.Concat(Game1.getFarm().buildings.SelectMany(b=>b.GetIndoors()?.animals.Values.AsEnumerable()??Enumerable.Empty<FarmAnimal>())).GroupBy(a=>a.myID.Value).Select(g=>g.First());
    private string ToolService(AiInputSimulator input)
    {
        if(_action=="collect_upgraded_tool"){
            if(Game1.player.toolBeingUpgraded.Value==null){
                if(_collectedUpgradeId!=null&&Game1.player.Items.OfType<Tool>().Any(t=>t.QualifiedItemId==_collectedUpgradeId)){Completed=1;return Finish("upgraded_tool_received");}
                return End("no_tool_upgrade_pending");
            }
            var pending=Game1.player.toolBeingUpgraded.Value;var requested=ItemRegistry.Create(Text("toolId")) as Tool;
            if(requested==null||pending.GetType()!=requested.GetType())return End("requested_tool_not_being_upgraded");
            if(Game1.player.daysLeftForToolUpgrade.Value>0){_results.Add(new{state="upgrading",daysRemaining=Game1.player.daysLeftForToolUpgrade.Value});return End("tool_upgrade_pending");}
            if(Game1.player.freeSpotsInInventory()==0)return End("upgrade_collection_inventory_full");
            _collectedUpgradeId=pending.QualifiedItemId;
        }else if(Game1.player.toolBeingUpgraded.Value!=null)return End("another_tool_upgrade_pending");
        var upgradeLevel=Num("upgradeLevel");
        var materialId=upgradeLevel switch{1=>"(O)334",2=>"(O)335",3=>"(O)336",4=>"(O)337",_=>""};
        if(_action=="upgrade_tool"&&materialId!=""&&Count(materialId)<5){_results.Add(new{requiredMaterial=materialId,requiredCount=5,availableCount=Count(materialId)});return End("tool_upgrade_materials_missing");}
        if(Game1.activeClickableMenu is ShopMenu shop){
            _ownedMenu=shop;
            if(_action!="upgrade_tool"||shop.ShopId!="ClintUpgrade")return End("unexpected_upgrade_shop");
            if(shop.safetyTimer>0)return "running";
            var target=shop.forSale.OfType<Tool>().FirstOrDefault(t=>t.UpgradeLevel==upgradeLevel&&(t.QualifiedItemId==Text("toolId")||ShopBuilder.GetToolUpgradeData(t.GetToolData(),Game1.player)?.RequireToolId==Text("toolId")));
            if(target==null)return End("requested_tool_upgrade_not_offered");
            var info=shop.itemPriceAndStock[target];
            if(info.Price>Num("maxCost")||info.Price>Game1.player.Money)return End("upgrade_budget_exceeded");
            var moneyBefore=Game1.player.Money;var materialBefore=materialId==""?0:Count(materialId);
            var n=shop.forSale.IndexOf(target);shop.currentItemIndex=Math.Min(n,Math.Max(0,shop.forSale.Count-4));
            var click=shop.forSaleButtons[n-shop.currentItemIndex].bounds.Center;shop.receiveLeftClick(click.X,click.Y);
            if(Game1.player.toolBeingUpgraded.Value is not {} ordered||ordered.UpgradeLevel!=upgradeLevel||ordered.GetType()!=target.GetType())return End("tool_upgrade_order_not_verified");
            var materialConsumed=materialId==""?0:materialBefore-Count(materialId);var moneyConsumed=moneyBefore-Game1.player.Money;
            if(materialId!=""&&materialConsumed!=5)return End("tool_upgrade_material_consumption_not_verified");
            if(moneyConsumed!=info.Price)return End("tool_upgrade_cost_not_verified");
            _results.Add(new{state="upgrading",tool=Text("toolId"),upgradeLevel,moneyConsumed,materialId,materialConsumed,daysRemaining=Game1.player.daysLeftForToolUpgrade.Value});Completed=1;return Finish("tool_upgrade_order_placed");
        }
        if(Game1.activeClickableMenu is DialogueBox dialogue){
            if(_action=="upgrade_tool"&&dialogue.isQuestion&&_responses.Count==0&&dialogue.responses.Any(r=>r.responseKey=="Upgrade"))_responses.Enqueue("Upgrade");return Dialogue(dialogue);
        }
        if(Game1.activeClickableMenu!=null)return End("unexpected_tool_service_menu");
        return NavigateToShopCounter("ClintUpgrade",input);
    }
    private static Point ScreenTile(int x,int y)
    {
        var vector=Utility.ModifyCoordinatesForUIScale(new Vector2(x*64+32-Game1.viewport.X,y*64+32-Game1.viewport.Y));
        return new((int)vector.X,(int)vector.Y);
    }
    private static void ClickMenu(IClickableMenu menu,int x,int y,bool right=false)
    {
        var keyboard=Game1.oldKBState;Game1.oldKBState=new KeyboardState();
        var prevPos=Game1.getMousePositionRaw();var prevMouse=Game1.oldMouseState;
        // Some native menus read the live mouse position instead of the
        // receiveLeftClick x/y arguments. Park the cursor on the target first so
        // those handlers process the intended component; restore it afterwards.
        Game1.setMousePositionRaw(x,y);
        Game1.oldMouseState=new MouseState(x,y,0,right?ButtonState.Released:ButtonState.Pressed,ButtonState.Released,ButtonState.Released,ButtonState.Released,ButtonState.Released);
        try{if(right)menu.receiveRightClick(x,y);else menu.receiveLeftClick(x,y);}
        finally{Game1.oldKBState=keyboard;Game1.setMousePositionRaw(prevPos.X,prevPos.Y);Game1.oldMouseState=prevMouse;}
    }
    private void ClickWorldTile(IClickableMenu menu,Point point)
    {
        void DispatchClick(){
            var previous=Game1.oldMouseState;
            var previousPosition=Game1.getMousePositionRaw();
            var mouseX=(int)(point.X*Game1.options.uiScale);
            var mouseY=(int)(point.Y*Game1.options.uiScale);
            // CarpenterMenu's Move action ignores the receiveLeftClick x/y
            // parameters and reads Game1.getMouseX()/getMouseY() instead.
            // Update the live input position as well as oldMouseState so the
            // native handler processes the exact simulated tile.
            Game1.setMousePositionRaw(mouseX,mouseY);
            Game1.oldMouseState=new MouseState(mouseX,mouseY,0,ButtonState.Pressed,ButtonState.Released,ButtonState.Released,ButtonState.Released,ButtonState.Released);
            try{menu.receiveLeftClick(point.X,point.Y);}
            finally{Game1.setMousePositionRaw(previousPosition.X,previousPosition.Y);Game1.oldMouseState=previous;}
        }
        if(menu is CarpenterMenu carpenter){
            // Building placement defers its world click through NetMutex and
            // reads oldMouseState in that callback. Demolition resolves its
            // target before requesting the witnessed lock, so dispatch it now.
            if(carpenter.Action==CarpenterMenu.CarpentryAction.Demolish){
                DispatchClick();
            }else{
                var mutex=Game1.player.team.buildLock;
                mutex.RequestLock(()=>{try{DispatchClick();}finally{mutex.ReleaseLock();}});
            }
            return;
        }
        DispatchClick();
    }
    // Keep the Farm instance captured when the business task was created. The
    // carpenter transition can replace TargetLocation with a transient Farm
    // view whose building collection is incomplete, even though the save and
    // the original Farm instance still contain the requested building.
    private Farm BuildingFarm()
    {
        var captured=_buildingTargetLocation as Farm;
        if(captured!=null)return captured;
        return Game1.activeClickableMenu is CarpenterMenu carpenter&&Read(carpenter,"TargetLocation") is Farm targetFarm
            ?targetFarm:Game1.getFarm();
    }
    private object? BuildingContext()
    {
        if(_action is not ("order_building" or "modify_building"))return null;
        var id=Text("buildingId");
        object? FarmInfo(Farm? farm)=>farm==null?null:new{
            reference=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(farm),
            hasRequested=farm.buildings.Any(b=>b.id.Value.ToString()==id),
            buildings=farm.buildings.Select(b=>new{id=b.id.Value.ToString(),type=b.buildingType.Value,x=b.tileX.Value,y=b.tileY.Value}).ToArray()};
        var menuFarm=Game1.activeClickableMenu is CarpenterMenu menu?menu.TargetLocation as Farm:null;
        return new{
            requestedBuildingId=id,
            targetWasPresentAtBusinessStart=_buildingAtTaskStart!=null,
            targetAtBusinessStart=_buildingAtTaskStart==null?null:new{id=_buildingAtTaskStart.id.Value.ToString(),type=_buildingAtTaskStart.buildingType.Value,x=_buildingAtTaskStart.tileX.Value,y=_buildingAtTaskStart.tileY.Value},
            recoveredFromCarpenterTransition=_buildingRecoveredFromMenuTransition,
            targetFarmAtStart=FarmInfo(_buildingTargetLocation as Farm),
            menuTargetFarm=FarmInfo(menuFarm),
            currentFarm=FarmInfo(Game1.currentLocation as Farm),
            gameFarm=FarmInfo(Game1.getFarm()),
            demolishLockLocked=Game1.player.team.demolishLock.IsLocked(),
            currentLocation=Game1.currentLocation.NameOrUniqueName};
    }
    private Building? SelectedBuilding()
    {
        var id=Text("buildingId");var farm=BuildingFarm();
        var building=farm.buildings.FirstOrDefault(b=>b.id.Value.ToString()==id);
        if(building!=null)return building;
        // Carpenter's map transition can momentarily detach the farm building
        // list. Prefer the same identity from another loaded Farm instance if
        // one exists; never substitute a building by type or position.
        var loaded=Game1.locations.OfType<Farm>()
            .Select(location=>(farm:location,building:location.buildings.FirstOrDefault(b=>b.id.Value.ToString()==id)))
            .FirstOrDefault(pair=>pair.building!=null);
        if(loaded.building!=null){if(Game1.activeClickableMenu is CarpenterMenu menu)menu.TargetLocation=loaded.farm;return loaded.building;}
        // If the exact object was present when the user's request began and
        // the transition detached it from every loaded Farm, restore that same
        // object to its original Farm so the native CarpenterMenu can operate
        // on it. This is not a guessed replacement and cannot affect other IDs.
        var demolitionWasSubmitted=_action=="modify_building"&&Text("operation")=="demolish"&&_index>=2;
        if(!demolitionWasSubmitted&&_buildingAtTaskStart is {} original&&original.id.Value.ToString()==id
            &&_buildingTargetLocation is Farm originalFarm&&ReferenceEquals(farm,originalFarm)){
            originalFarm.buildings.Add(original);_buildingRecoveredFromMenuTransition=true;return original;
        }
        return null;
    }
    private string Buildings(AiInputSimulator input)
    {
        // Carpenter dialogue/menu temporarily switches the game's current location to Farm.
        // Resolve and retain the counter map before that transition, or later ticks may
        // mistake the farm-view context for the carpenter's actual service location.
        if(_resolvedShopMap==null)_=ShopMap("Carpenter");
        if(_action=="modify_building"&&(Text("operation") is "move" or "upgrade" or "demolish")
            &&SelectedBuilding()==null){
            if(Text("operation")=="demolish"&&_index>=2){Completed=1;return Finish("building_demolition_verified");}
            return End("building_id_not_found:"+Text("buildingId"));
        }
        if(_constructionBefore==null&&(_action is "order_building" or "modify_building")&&BuildingFarm().buildings.Any(b=>b.daysOfConstructionLeft.Value>0)){
            var constructions=BuildingFarm().buildings.Where(b=>b.daysOfConstructionLeft.Value>0)
                .Select(b=>new{buildingId=b.id.Value.ToString(),buildingType=b.buildingType.Value,x=b.tileX.Value,y=b.tileY.Value,daysRemaining=b.daysOfConstructionLeft.Value}).ToArray();
            _results.Add(new{failure="carpenter_construction_in_progress",phase="check_native_construction_availability",constructions,
                recoveryCondition="wait_for_all_native_building_construction_to_finish_then_retry"});
            return End("carpenter_construction_in_progress");
        }
        if(_buildingUpgradeRequested){
            var upgradedBuilding=SelectedBuilding();
            if(upgradedBuilding!=null&&(upgradedBuilding.daysUntilUpgrade.Value>0||upgradedBuilding.buildingType.Value==Text("upgradeType"))){
                var cost=_buildingMoneyBefore-Game1.player.Money;Completed=1;
                if(cost!=_buildingQuotedCost&&_clock.Now-_wait<=5000)return "running";
                _results.Add(new{buildingId=upgradedBuilding.id.Value.ToString(),state=upgradedBuilding.daysUntilUpgrade.Value>0?"upgrading":"completed",daysRemaining=upgradedBuilding.daysUntilUpgrade.Value,actualCost=cost});
                return cost==_buildingQuotedCost?Finish("building_upgrade_order_verified"):End("building_upgrade_cost_not_verified");
            }
            return _clock.Now-_wait>5000?End("building_upgrade_not_verified"):"running";
        }
        if(_buildingMoveRequested){
            var moved=SelectedBuilding();
            if(moved!=null&&moved.tileX.Value==Num("x")&&moved.tileY.Value==Num("y")){Completed=1;return Finish("building_moved");}
            return _clock.Now-_wait>5000?End("building_move_not_verified"):"running";
        }
        if(_constructionBefore!=null){
            var added=BuildingFarm().buildings.FirstOrDefault(b=>!_constructionBefore.Contains(b.id.Value)&&b.tileX.Value==Num("x")&&b.tileY.Value==Num("y")&&b.buildingType.Value==Text("buildingType"));
            if(added!=null){
                // The native carpenter menu creates the building before its
                // delayed return warp and Robin's construction dialogue finish.
                // Keep this task active through that native story flow so the
                // next task doesn't inherit a transient shop/menu state.
                if(Game1.IsFading()||Game1.activeClickableMenu is CarpenterMenu)return "running";
                if(Game1.activeClickableMenu is DialogueBox constructionDialogue)return Dialogue(constructionDialogue);
                if(Game1.activeClickableMenu is ShopMenu constructionStore&&constructionStore.ShopId=="Carpenter"){
                    if(constructionStore.readyToClose())constructionStore.exitThisMenu();
                    return "running";
                }
                var cost=_buildingMoneyBefore-Game1.player.Money;Completed=1;
                if(cost!=_buildingQuotedCost&&_clock.Now-_wait<=5000)return "running";
                _results.Add(new{buildingId=added.id.Value.ToString(),state=added.daysOfConstructionLeft.Value>0?"constructing":"completed",daysRemaining=added.daysOfConstructionLeft.Value,actualCost=cost});
                return cost==_buildingQuotedCost?Finish("building_order_verified"):End("building_order_cost_not_verified");
            }
            return _clock.Now-_wait>5000?End("native_building_placement_not_verified"):"running";
        }
        if(_action=="modify_building"&&Text("operation")=="demolish"&&_index>=2){
            // NetWitnessedLock normally advances from FarmerTeam.Update. The
            // game can defer that team tick while CarpenterMenu has switched
            // the active map; advance the native lock while this task is
            // already waiting for its demolition result. Its witness checks
            // still gate execution for every online farmer.
            var demolishLock=Game1.player.team.demolishLock;
            if(demolishLock.IsLocked())demolishLock.Update();
            if(SelectedBuilding()==null){Completed=1;return Finish("building_demolition_verified");}
            if(Game1.activeClickableMenu is DialogueBox confirm){if(_responses.Count==0)_responses.Enqueue("Yes");return Dialogue(confirm);}
            return _clock.Now-_wait>5000?End("building_demolition_not_verified"):"running";
        }
        if(Game1.activeClickableMenu is DialogueBox dialogue){if(dialogue.isQuestion&&_responses.Count==0&&dialogue.responses.Any(r=>r.responseKey=="Construct"))_responses.Enqueue("Construct");return Dialogue(dialogue);}
        if(_action=="modify_building"&&Game1.activeClickableMenu is ShopMenu carpenterStore&&carpenterStore.ShopId=="Carpenter"){
            // A completed construction returns to Robin's furniture shop. Close
            // that unrelated catalog before routing back to the carpentry menu.
            carpenterStore.exitThisMenu();return "running";
        }
        if(Game1.activeClickableMenu is not CarpenterMenu menu){
            if(Game1.activeClickableMenu!=null)return End("unexpected_building_service_menu");
            return NavigateToShopCounter("Carpenter",input);
        }
        _ownedMenu=menu;
        if(_action=="modify_building"&&_buildingTargetLocation is Farm capturedFarm
            &&capturedFarm.buildings.Any(b=>b.id.Value.ToString()==Text("buildingId"))
            &&!ReferenceEquals(menu.TargetLocation,capturedFarm))
            menu.TargetLocation=capturedFarm;
        if(Read(menu,"TargetLocation") is GameLocation targetLocation&&!ReferenceEquals(targetLocation,BuildingFarm()))return End("building_target_location_mismatch");
        if(_index==0){
            var op=_action=="order_building"?"build":Text("operation");
            if(op=="build"||op=="upgrade"){
                var id=_action=="order_building"?Text("buildingType"):Text("upgradeType");
                var blueprint=menu.Blueprints.FirstOrDefault(b=>b.Id==id);if(blueprint==null)return End("building_not_unlocked:"+id);
                if(blueprint.BuildCost>Num("maxCost")||blueprint.BuildCost>Game1.player.Money)return End("building_budget_exceeded");
                _buildingQuotedCost=blueprint.BuildCost;
                if(op=="build")menu.TargetViewportCenterOnTile=new Vector2(Num("x")+3,Num("y")+1);
                menu.SetNewActiveBlueprint(blueprint);
                if(!menu.DoesFarmerHaveEnoughResourcesToBuild())return End("building_materials_missing");
                var click=menu.okButton.bounds.Center;menu.receiveLeftClick(click.X,click.Y);
            }else{
                if((op is "move" or "demolish")&&SelectedBuilding() is {} selected){
                    var centerX=selected.tileX.Value+Math.Max(0,selected.tilesWide.Value/2);
                    var centerY=selected.tileY.Value+Math.Max(0,selected.tilesHigh.Value/2);
                    menu.TargetViewportCenterOnTile=new Vector2(centerX,centerY);
                }
                var button=op=="move"?menu.moveButton:menu.demolishButton;var click=button.bounds.Center;menu.receiveLeftClick(click.X,click.Y);
            }
            _index=1;return "running";
        }
        // CarpenterMenu switches from the blueprint list to the farm view via
        // a fade. It reports onFarm as soon as the transition starts, but its
        // receiveLeftClick handler ignores input until the fade has finished.
        if(!menu.onFarm||menu.freeze||Game1.IsFading())return "running";
        var operation=_action=="order_building"?"build":Text("operation");
        var building=operation=="build"?null:SelectedBuilding();
        if(operation!="build"&&building==null)return End("building_identity_missing");
        if(_index==1&&operation!="build"){
            _buildingMoneyBefore=Game1.player.Money;
            var tileX=building!.tileX.Value+Math.Max(0,building.tilesWide.Value/2);
            var tileY=building.tileY.Value+Math.Max(0,building.tilesHigh.Value/2);
            var p=ScreenTile(tileX,tileY);var viewport=Game1.viewport;var zoom=Game1.options.zoomLevel;var uiScale=Game1.options.uiScale;
            var clickedTile=new{x=(int)(viewport.X+p.X*uiScale/zoom)/64,y=(int)(viewport.Y+p.Y*uiScale/zoom)/64};
            var nativeHit=menu.TargetLocation.getBuildingAt(new Vector2(clickedTile.x,clickedTile.y));
            _results.Add(new{operation="building_target_click",buildingId=building.id.Value.ToString(),menuAction=menu.Action.ToString(),menuOnFarm=menu.onFarm,
                buildingTile=new{x=building.tileX.Value,y=building.tileY.Value},clickTile=new{x=tileX,y=tileY},screenPoint=new{x=p.X,y=p.Y},viewport=new{x=viewport.X,y=viewport.Y},zoom,uiScale,
                calculatedWorldTile=clickedTile,nativeHit=nativeHit==null?null:new{id=nativeHit.id.Value.ToString(),type=nativeHit.buildingType.Value},
                nativeHitMatchesRequested=nativeHit!=null&&nativeHit.id.Value==building.id.Value,
                targetInMenuFarm=menu.TargetLocation.buildings.Contains(building),canDemolish=menu.CanDemolishThis(building),hasDemolishPermission=menu.hasPermissionsToDemolish(building),
                menuFreeze=menu.freeze,fading=Game1.IsFading(),currentLocation=Game1.currentLocation.NameOrUniqueName,
                demolishLockLockedBefore=Game1.player.team.demolishLock.IsLocked()});
            ClickWorldTile(menu,p);_index=2;_wait=_clock.Now;
            if(operation=="move"){
                var moving=Read(menu,"buildingToMove") as Building;
                _results.Add(new{operation="building_move_source_selection",selectedBuildingId=moving?.id.Value.ToString(),
                    selectedBuildingTile=moving==null?null:new{x=moving.tileX.Value,y=moving.tileY.Value},requestedBuildingId=building.id.Value.ToString(),
                    activeAction=menu.Action.ToString(),menuOnFarm=menu.onFarm,menuFreeze=menu.freeze,fading=Game1.IsFading()});
            }
            _results.Add(new{operation="building_target_click_dispatched",menuAction=menu.Action.ToString(),menuOnFarm=menu.onFarm,menuFreeze=menu.freeze,
                activeMenu=Game1.activeClickableMenu?.GetType().Name,currentLocation=Game1.currentLocation.NameOrUniqueName,
                demolishLockLockedImmediatelyAfter=Game1.player.team.demolishLock.IsLocked()});
            if(operation=="demolish")return "running";
            if(operation=="upgrade"){
                _buildingUpgradeRequested=true;return "running";
            }return "running";
        }
        var target=ScreenTile(Num("x"),Num("y"));if(operation=="build"){_constructionBefore=BuildingFarm().buildings.Select(b=>b.id.Value).ToHashSet();_buildingMoneyBefore=Game1.player.Money;}
        if(operation=="move"){
            var moving=Read(menu,"buildingToMove") as Building;
            var worldTarget=new Vector2(Num("x"),Num("y"));
            var occupied=menu.TargetLocation?.getBuildingAt(worldTarget);
            _results.Add(new{operation="building_move_target_preflight",selectedBuildingId=moving?.id.Value.ToString(),
                selectedBuildingTile=moving==null?null:new{x=moving.tileX.Value,y=moving.tileY.Value},target=new{x=Num("x"),y=Num("y")},
                occupiedBuildingId=occupied?.id.Value.ToString(),confirmable=moving!=null&&menu.ConfirmBuildingAccessibility(worldTarget),
                menuAction=menu.Action.ToString(),menuOnFarm=menu.onFarm,menuFreeze=menu.freeze,fading=Game1.IsFading(),currentLocation=Game1.currentLocation.NameOrUniqueName});
        }
        ClickWorldTile(menu,target);_wait=_clock.Now;
        if(operation=="move"){
            var moving=Read(menu,"buildingToMove") as Building;
            _results.Add(new{operation="building_move_target_click_dispatched",buildingId=building?.id.Value.ToString(),
                movingBuildingId=moving?.id.Value.ToString(),movingBuildingTile=moving==null?null:new{x=moving.tileX.Value,y=moving.tileY.Value},
                menuAction=menu.Action.ToString(),menuOnFarm=menu.onFarm,menuFreeze=menu.freeze,fading=Game1.IsFading(),currentLocation=Game1.currentLocation.NameOrUniqueName});
            _buildingMoveRequested=true;return "running";
        }
        return "running";
    }
    private string Livestock(AiInputSimulator input)
    {
        var operation=Text("operation");
        if(_incubationTarget!=null){
            var consumed=_incubationEggBefore-Count(_incubationEggId!);
            var sameTarget=Game1.currentLocation.NameOrUniqueName==Map&&Game1.currentLocation.objects.Values.Any(obj=>ReferenceEquals(obj,_incubationTarget));
            if(sameTarget&&_incubationTarget.heldObject.Value?.QualifiedItemId==_incubationEggId&&consumed==1){
                _results.Add(new{state="incubating",eggId=_incubationEggId,consumed,minutesRemaining=_incubationTarget.MinutesUntilReady,animalHatched=false,verified=true});
                Completed=1;return Finish("incubation_started");
            }
            if(_clock.Now<_incubationDeadline)return "running";
            _results.Add(new{eggId=_incubationEggId,consumed,sameTarget,heldObjectId=_incubationTarget.heldObject.Value?.QualifiedItemId,verified=false});
            var failed=End("native_incubation_not_verified");return consumed>0?"partial":failed;
        }
        if(_animalSaleMoneyBefore!=null){
            if(!OwnedLivestock().Any(a=>a.myID.Value.ToString()==Text("animalId"))){
                if(Game1.player.Money<_animalSaleMoneyBefore.Value)return End("animal_sale_income_not_verified");
                _results.Add(new{animalId=Text("animalId"),income=Game1.player.Money-_animalSaleMoneyBefore.Value});Completed=1;return Finish("animal_sale_verified");
            }
            return _clock.Now<_animalSaleDeadline?"running":End("animal_sale_not_verified");
        }
        if(_animalRehomeRequested){
            if(_managedAnimal?.home?.id.Value.ToString()==_animalRehomeTarget){Completed=1;_results.Add(new{animalId=Text("animalId"),homeId=_animalRehomeTarget});return Finish("animal_rehomed");}
            if(_clock.Now>=_animalRehomeDeadline)return End("animal_rehome_not_verified");
            // A single cross-map MoveHome click can miss while the native view is
            // still settling. Re-dispatch the target click on a bounded cadence so
            // the actual animal.homeId can still verify before the deadline.
            if(_animalRehomeClick is {} rehomeClick&&_ownedMenu is IClickableMenu rehomeMenu&&_clock.Now-_animalRehomeClickAt>1200){
                ClickWorldTile(rehomeMenu,rehomeClick);_animalRehomeClickAt=_clock.Now;
            }
            return "running";
        }
        if(_livestockSubmitted){
            var added=OwnedLivestock().FirstOrDefault(a=>!_livestockBefore!.Contains(a.myID.Value)&&a.Name==Text("name")&&a.type.Value==_livestockExpectedType&&a.home?.id.Value.ToString()==_livestockExpectedHome);
            if(added==null)return _clock.Now<_wait?"running":End("animal_purchase_not_verified");
            var spent=_livestockMoneyBefore-Game1.player.Money;
            _results.Add(new{animalId=added.myID.Value.ToString(),name=added.Name,type=added.type.Value,homeId=added.home?.id.Value.ToString(),spent});
            Completed=1;
            if(spent!=_livestockExpectedPrice)return End("animal_purchase_cost_not_verified");
            Completed=1;return Finish("animal_purchase_verified");
        }
        if(operation is "rehome" or "sell"){
            var found=EntityLocator.Animals().SingleOrDefault(a=>a.Animal.myID.Value.ToString()==Text("animalId"));
            if(found==null)return End("animal_id_not_present_in_world");
            if(!EntityLocator.CanEnter(found.Location))return End("livestock_location_disabled");
            _managedAnimal=found.Animal;_resolvedEntityMap=found.Location.NameOrUniqueName;
            if(operation=="rehome"){
                var targetHome=SelectedBuilding();
                if(targetHome==null)return End("animal_home_not_found:"+Text("buildingId"));
                if(targetHome.GetIndoors() is not AnimalHouse)return End("animal_home_is_not_animal_house");
                if(_managedAnimal.home?.id.Value==targetHome.id.Value)return Finish("animal_already_in_requested_home");
            }
        }
        if(operation=="incubate"){
            var home=Game1.getFarm().buildings.SingleOrDefault(b=>b.id.Value.ToString()==Text("buildingId"))?.GetIndoors();
            if(home==null)return End("incubation_building_not_found");
            if(home is not AnimalHouse)return End("incubation_building_not_animal_house");
            if(!EntityLocator.CanEnter(home))return End("livestock_location_disabled");
            _resolvedEntityMap=home.NameOrUniqueName;
            if(Game1.activeClickableMenu!=null)return End("incubation_blocked_by_menu:"+Game1.activeClickableMenu.GetType().Name);
        }
        if(operation is "incubate" or "rehome" or "sell"&&Game1.currentLocation.NameOrUniqueName!=Map&&Game1.activeClickableMenu==null){
            var destination=(operation is "rehome" or "sell")&&_managedAnimal!=null
                ?new GridPoint(_managedAnimal.TilePoint.X,_managedAnimal.TilePoint.Y):Navigation.Current;
            var move=_nav.Move(Map,destination,input,_interact);return move is "blocked" or "unsupported"?End("livestock_location_unreachable:"+_nav.Reason):"running";
        }
        if(operation=="incubate"){
            var incubator=Game1.currentLocation.objects.Pairs.FirstOrDefault(p=>p.Value.Name.Contains("Incubator")&&p.Value.heldObject.Value==null);
            if(incubator.Value==null)return End("empty_incubator_not_observed");
            var point=new GridPoint((int)incubator.Key.X,(int)incubator.Key.Y);var near=Near(Map,point,input);if(near is "blocked" or "unsupported")return End("incubator_unreachable");if(near!="reached")return "running";
            var egg=Item(Text("itemId"));if(egg==null)return End("incubation_item_missing");
            if(egg.modData.ContainsKey("StardewCompanion/Protected"))return End("incubation_item_protected");
            _incubationTarget=incubator.Value;_incubationEggId=egg.QualifiedItemId;_incubationEggBefore=Count(egg.QualifiedItemId);_incubationDeadline=_clock.Now+4000;
            Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(egg);_interact(point);
            return "running";
        }
        if(operation=="door")return Animals(input);
        if(operation is "rehome" or "sell"){
            var animal=_managedAnimal??=Game1.currentLocation.animals.Values.FirstOrDefault(a=>a.myID.Value.ToString()==Text("animalId"));if(animal==null)return End("animal_not_observed");
            if(Game1.activeClickableMenu!=null&&Game1.activeClickableMenu is not AnimalQueryMenu)return End("unexpected_livestock_menu:"+Game1.activeClickableMenu.GetType().Name);
            if(Game1.activeClickableMenu is not AnimalQueryMenu query){
                var near=Near(Map,new(animal.TilePoint.X,animal.TilePoint.Y),input);if(near is "blocked" or "unsupported")return End("animal_unreachable");if(near!="reached")return "running";
                // Open the native AnimalQueryMenu directly. Do NOT call pet(): it
                // raises friendship as a side effect and does not open the menu, so a
                // rehome would pet every attempt and never reach the move-home UI.
                Game1.activeClickableMenu=new AnimalQueryMenu(animal);_ownedMenu=Game1.activeClickableMenu;return "running";
            }
            _ownedMenu=query;
            if(Read(query,"animal") is not FarmAnimal queried||queried.myID.Value!=animal.myID.Value)return End("animal_query_identity_mismatch");
            if(operation=="sell"){
                if(query.confirmingSell){_animalSaleMoneyBefore??=Game1.player.Money;_animalSaleDeadline=_clock.Now+5000;}
                var button=query.confirmingSell?query.yesButton:query.sellButton;var click=button.bounds.Center;query.receiveLeftClick(click.X,click.Y);
                if(!OwnedLivestock().Any(a=>a.myID.Value==animal.myID.Value)){
                    if(_animalSaleMoneyBefore==null||Game1.player.Money<_animalSaleMoneyBefore.Value)return End("animal_sale_income_not_verified");
                    _results.Add(new{animalId=animal.myID.Value.ToString(),name=animal.Name,income=Game1.player.Money-_animalSaleMoneyBefore.Value});Completed=1;return Finish("animal_sale_verified");
                }return "running";
            }
            var home=SelectedBuilding();if(home==null)return End("animal_home_not_found:"+Text("buildingId"));
            // CanLiveIn can return false on a valid occupied/upgradeable coop
            // depending on the source AnimalHouse state. The native move-home
            // UI remains the authority; verify the actual home id after clicking
            // the selected building and report a refusal if the game declines.
            if(!query.movingAnimal){var click=query.moveHomeButton.bounds.Center;query.receiveLeftClick(click.X,click.Y);return "running";}
            _animalRehomeTarget=home.id.Value.ToString();_animalRehomeRequested=true;_animalRehomeDeadline=_clock.Now+5000;
            var point=ScreenTile(home.tileX.Value+Math.Max(0,home.tilesWide.Value/2),home.tileY.Value+Math.Max(0,home.tilesHigh.Value/2));
            _animalRehomeClick=point;_animalRehomeClickAt=_clock.Now;ClickWorldTile(query,point);return "running";
        }
        if(Game1.activeClickableMenu is PurchaseAnimalsMenu menu){
            _ownedMenu=menu;
            if(menu.priceOfAnimal>Num("maxCost")||menu.priceOfAnimal>Game1.player.Money)return End("animal_purchase_budget_exceeded");
            if(!menu.onFarm){
                if(_livestockChoiceAt!=0){
                    if(_clock.Now-_livestockChoiceAt<4000)return "running";
                    // The native selection can drop a click while the menu settles.
                    // Re-dispatch the offer click once before declaring it unverified.
                    if(_livestockChoiceRetries++>=1)return End("animal_menu_selection_not_verified");
                    _livestockChoiceAt=0;
                }
                var choices=menu.animalsToPurchase.Select(b=>new ExecutionTargets.Choice(b.hoverText,AnimalOfferName(b),b.item?.Name)).ToArray();
                var selected=ExecutionTargets.Resolve(Text("animalType"),choices,out var offered);
                if(selected==null){_results.Add(new{requestedAnimalType=Text("animalType"),offered});return End("animal_type_unknown_or_ambiguous");}
                // Native component.name is the PRICE; hoverText/item.Name carries animal identity.
                var buttons=menu.animalsToPurchase.Where(b=>b.hoverText==selected).ToArray();
                if(buttons.Length!=1)return End("animal_type_offer_ambiguous");
                var button=buttons[0];
                if(button.item is not StardewValley.Object {Type:not null})return End("animal_type_not_unlocked");
                if(!int.TryParse(button.name,out var price))return End("animal_offer_price_unavailable");
                if(price>Num("maxCost")||price>Game1.player.Money)return End("animal_purchase_budget_exceeded");
                var click=button.bounds.Center;ClickMenu(menu,click.X,click.Y);_livestockChoiceAt=_clock.Now;return "running";
            }
            if(menu.namingAnimal){
                var actualHome=Read(menu,"newAnimalHome") as Building;
                if(actualHome==null||actualHome.id.Value.ToString()!=Text("buildingId"))return End("animal_home_selection_mismatch");
                _livestockBefore=OwnedLivestock().Select(a=>a.myID.Value).ToHashSet();
                _livestockExpectedType=menu.animalBeingPurchased.type.Value;_livestockExpectedHome=actualHome.id.Value.ToString();
                _livestockExpectedPrice=menu.priceOfAnimal;_livestockMoneyBefore=Game1.player.Money;
                menu.textBox.Text=Text("name");menu.textBoxEnter(menu.textBox);_livestockSubmitted=true;_wait=_clock.Now+5000;return "running";
            }
            var building=SelectedBuilding();if(building==null)return End("animal_home_not_found");
            var point=ScreenTile(building.tileX.Value,building.tileY.Value);ClickWorldTile(menu,point);return "running";
        }
        if(Game1.activeClickableMenu is DialogueBox dialogue){if(dialogue.isQuestion&&_responses.Count==0&&dialogue.responses.Any(r=>r.responseKey=="Purchase"))_responses.Enqueue("Purchase");return Dialogue(dialogue);}
        if(Game1.activeClickableMenu!=null)return End("unexpected_animal_service_menu");
        return NavigateToShopCounter("AnimalShop",input);
    }
}
