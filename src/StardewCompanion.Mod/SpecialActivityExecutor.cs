using System.Text.Json;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewCompanion.Fishing;
using StardewCompanion.Mod;
using StardewCompanion.Protocol;
using StardewMiningAgent.Combat;
using StardewMiningAgent.Models;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Tools;

namespace StardewCompanion.Mod;

/// <summary>Native game adapters for fishing, combat, and ordinary mine traversal.
/// Strategy inputs stay in the plan; high-frequency controls and verification stay local.</summary>
internal sealed class SpecialActivityExecutor
{
    private static SpecialActivityExecutor? _activeFishing;
    private static readonly FieldInfo? OldMouse = AccessTools.Field(typeof(Game1), "oldMouseState");
    private static readonly FieldInfo? OldKeyboard = AccessTools.Field(typeof(Game1), "oldKBState");
    private static readonly FishingPhysics NativeFishingPhysics = new();
    private static readonly PhasePlaneController NativeFishingController = new(NativeFishingPhysics, new TackleKit());
    private readonly string _skill;
    private readonly JsonElement _args;
    private readonly Action<GridPoint> _interact;
    private readonly ExecutionClock _clock=new();
    private readonly Navigation _nav = new();
    private readonly Dictionary<string, int> _inventoryBefore;
    private readonly Dictionary<string, int> _gains = new();
    private readonly List<object> _events = new();
    private string _phase = "starting";
    private string _reason = "";
    private long _phaseAt;
    private long _lastProgress;
    private int _casts;
    private int _verifiedCatchCasts;
    private int _receivedCatchCasts;
    private int _fishObservedCast;
    private bool _castInFlight;
    private long _castStartedAt;
    private FishingRod? _fishingRod;
    private bool _ownsFishingAction;
    private bool _fishingCleanupRequested;
    private readonly HashSet<int> _treasureSeenCasts=new();
    private readonly HashSet<int> _treasureCaughtCasts=new();
    private Dictionary<string,int>? _castInventoryBefore;
    private string? _pendingFishId;
    private int _pendingFishCount;
    private long _fishPickupDeadline;
    private int _attackAttempts;
    private int _startFloor = -1;
    private int _startTime;
    private int _startDay;
    private Monster? _target;
    private int _targetHp;
    private bool _attackSent;
    private GridPoint? _approach;
    private GridPoint? _approachEnemyTile;
    private readonly List<object> _attackPositionDiagnostics=new();
    private string? _map;
    private int _targetFloor;

    internal int Completed { get; private set; }
    internal string Reason => _reason;
    internal string Phase => _phase;
    internal object Details => new { stage = _phase, castsAttempted = _casts, catchesHooked = _verifiedCatchCasts, catchesVerified = _receivedCatchCasts,
        treasureOpportunitiesObserved=_treasureSeenCasts.Count,treasureMinigamesCaught=_treasureCaughtCasts.Count,
        killsVerified = _skill=="fight_enemies"?Completed:0,playerHealth=_skill=="fight_enemies"?Game1.player.health:(int?)null,
        playerLocation=Game1.currentLocation.NameOrUniqueName,gameTimeHHMM=Game1.timeOfDay,events = _events.ToArray(), actualGains = _gains };
    internal bool ProgressDue => _clock.Now - _lastProgress > 1000;
    internal void ProgressSent() => _lastProgress = _clock.Now;
    internal static CapabilityStatus[] Capabilities() => new[]
    {
        new CapabilityStatus{Name="fish_at",Group="resources_exploration",State="available_unverified",Implemented=true,Reason="requires_gameplay_validation"},
        new CapabilityStatus{Name="fight_enemies",Group="resources_exploration",State="available_unverified",Implemented=true,Reason="requires_gameplay_validation"},
        new CapabilityStatus{Name="mine_travel",Group="navigation_movement",State="disabled",Implemented=true,Reason="mines_disabled"}
    };

    internal SpecialActivityExecutor(string skill, string arguments, Action<GridPoint> interact)
    {
        _skill = skill;
        _args = JsonDocument.Parse(arguments).RootElement.Clone();
        _interact = interact;
        _inventoryBefore = InventorySnapshot();
        if (skill == "fish_at")
        {
            _map = Text("location"); _targetFloor = Number("casts");
            if (_targetFloor is < 1 or > 20) throw new JsonException("casts_out_of_range");
            if (Text("direction") is not ("up" or "right" or "down" or "left")) throw new JsonException("invalid_cast_direction");
            if (Text("treasurePolicy") is not ("try" or "ignore")) throw new JsonException("invalid_treasure_policy");
        }
        if (skill == "fight_enemies")
        {
            if (Number("maxKills") is < 1 or > 30 || Number("maxGameMinutes") is < 1 or > 600) throw new JsonException("combat_limits_out_of_range");
            _startTime = Game1.timeOfDay;
            _startDay = Game1.Date.TotalDays;
        }
        if (skill == "mine_travel")
        {
            _targetFloor = Number("targetFloor");
            if (_targetFloor is < 0 or > 120) throw new JsonException("ordinary_mine_floor_out_of_range");
        }
    }

    internal void SuspendForStoryPrompt(){_clock.Pause();_nav.Suspend();}
    internal string Tick(AiInputSimulator input)
    {
        _clock.Resume();
        if (_skill == "fish_at") return Fish(input);
        if (_skill == "fight_enemies") return Fight(input);
        return MineTravel(input);
    }

    private string Fish(AiInputSimulator input)
    {
        _activeFishing = this;
        var map = Text("location");
        var stand = new GridPoint(Number("x"), Number("y"));
        if (Game1.currentLocation.NameOrUniqueName != map)
        {
            _phase = "travel_to_shore";
            var travel = _nav.Move(map, stand, input, _interact, false);
            return travel is "blocked" or "unsupported" ? End(_nav.Reason) : "running";
        }
        if (Navigation.Current.Distance(stand) > 0)
        {
            _phase = "position_at_shore";
            var travel = _nav.Move(map, stand, input, _interact, false);
            return travel is "blocked" or "unsupported" ? End(_nav.Reason) : "running";
        }
        if (!Game1.currentLocation.IsOutdoors) return End("fishing_requires_observed_outdoor_shore");
        var rod = Game1.player.Items.OfType<FishingRod>().FirstOrDefault();
        if (rod == null) return End("fishing_rod_missing");
        _fishingRod=rod;
        Game1.player.faceDirection(Text("direction") switch { "up" => 0, "right" => 1, "down" => 2, _ => 3 });
        if (!ReferenceEquals(Game1.player.CurrentTool, rod)) Game1.player.CurrentToolIndex = Game1.player.Items.IndexOf(rod);
        CaptureFishGains();
        if (rod.fishCaught && _casts>0 && _fishObservedCast!=_casts)
        {
            _fishObservedCast=_casts;_verifiedCatchCasts++;
            _pendingFishId=rod.whichFish?.QualifiedItemId;_pendingFishCount=Math.Max(1,rod.numberOfFishCaught);_fishPickupDeadline=_clock.Now+15000;
            if(_pendingFishId==null)return End("native_caught_fish_identity_unknown");
            _events.Add(new { state = "native_fish_hooked_pending_inventory", cast = _verifiedCatchCasts, itemId = _pendingFishId, count = _pendingFishCount, junk = rod.lastCatchWasJunk, treasure = rod.treasureCaught });
        }
        if(_pendingFishId!=null){
            var received=InventorySnapshot().GetValueOrDefault(_pendingFishId)-(_castInventoryBefore?.GetValueOrDefault(_pendingFishId)??0);
            if(received>=_pendingFishCount){
                Completed+=_pendingFishCount;_receivedCatchCasts++;
                _events.Add(new{state="native_fish_inventory_verified",itemId=_pendingFishId,count=_pendingFishCount});_pendingFishId=null;
            }else{
                if(_clock.Now>_fishPickupDeadline)return End("native_caught_fish_not_received");
                if(Game1.activeClickableMenu==null&&rod.fishCaught){_phase="collect_native_caught_fish";rod.doneHoldingFish(Game1.player);return "running";}
                if(Game1.activeClickableMenu==null){_phase="waiting_for_fish_inventory_receipt";return "running";}
            }
        }

        if (Game1.activeClickableMenu is BobberBar)
        {
            _phase = "native_fishing_minigame";
            return "running";
        }
        if (Game1.activeClickableMenu is DialogueBox)
        {
            _phase = "native_result_popup_left_for_model";
            return End("native_fishing_dialogue_observed", Completed > 0);
        }
        if (Game1.activeClickableMenu is ItemGrabMenu loot)
        {
            if(!_castInFlight||!ReferenceEquals(loot.context,rod)&&!ReferenceEquals(loot.sourceItem,rod))return End("unexpected_fishing_loot_container");
            return CollectFishingLoot(loot);
        }
        if (Game1.activeClickableMenu != null) return End("unexpected_fishing_menu:" + Game1.activeClickableMenu.GetType().Name);

        // isCasting becomes true while the button is being held. Complete the
        // charge/release state before interpreting native rod flags, otherwise
        // that flag can bypass the release edge and stall the cast forever.
        if(_phase=="cast_line")
        {
            var charging = rod.isTimingCast && _clock.Now - _phaseAt < 3000;
            if(!charging){
                _casts++;_castInFlight=true;_castStartedAt=_clock.Now;_phaseAt=_clock.Now;_phase="cast_release";
                _events.Add(new{state="native_cast_started",cast=_casts,direction=Text("direction")});
            }
            return "running";
        }
        if(rod.fishCaught)
        {
            if(_clock.Now>_fishPickupDeadline)return End("native_catch_animation_not_finished");
            input.Tool=(_clock.Now/150)%2==0;return "running";
        }
        if (_castInFlight && (rod.hit || rod.pullingOutOfWater || rod.showingTreasure))
            return _clock.Now - _castStartedAt > 90000 ? End("cast_timeout_no_native_catch") : "running";
        if (rod.isNibbling)
        {
            _phase = "hook_native_bite";
            _events.Add(new { state = "native_bite_observed", cast = _casts });
            rod.DoFunction(Game1.currentLocation, (int)rod.bobber.X, (int)rod.bobber.Y, 1, Game1.player);
            return "running";
        }
        if (rod.isFishing || rod.isReeling || rod.castedButBobberStillInAir || (rod.isCasting && _phase != "cast_release"))
        {
            _phase = rod.isReeling ? "native_reeling" : "waiting_for_bite";
            if(!_castInFlight)return End("existing_fishing_action_not_owned_by_task");
            if (_clock.Now - _castStartedAt > 90000) return End("cast_timeout_no_native_catch");
            return "running";
        }
        if (_phase == "cast_release")
        {
            // The native fishing action is invoked on the release edge. Give the
            // rod time to take the cast and enter its wait-for-bite cycle (the
            // native isFishing/isReeling/isCasting/bobber flags are observed
            // above). A short fixed window used to abandon a still-starting cast
            // and skip to the next one before native ever registered it.
            if (_clock.Now - _phaseAt < 5000) return "running";
            _events.Add(new { state = "native_cast_not_registered", cast = _casts, direction = Text("direction") });
        }
        _castInFlight=false;
        if (_casts >= Number("casts"))
        {
            return Completed > 0 ? End("requested_casts_completed", true) : End("no_native_catch_confirmed");
        }
        _phase="cast_line";_phaseAt=_clock.Now;_castInventoryBefore=InventorySnapshot();_ownsFishingAction=true;
        rod.beginUsing(Game1.currentLocation, Game1.player.StandingPixel.X, Game1.player.StandingPixel.Y, Game1.player);
        return "running";
    }

    private string CollectFishingLoot(ItemGrabMenu menu)
    {
        _phase="collect_native_fishing_loot";
        var index=menu.ItemsToGrabMenu.actualInventory.ToList().FindIndex(item=>item!=null);
        if(index<0){menu.exitThisMenu();return "running";}
        if(index>=menu.ItemsToGrabMenu.inventory.Count)return End("fishing_loot_slot_not_visible");
        var item=menu.ItemsToGrabMenu.actualInventory[index];
        if(!Game1.player.couldInventoryAcceptThisItem(item))return End("native_fish_loot_menu_requires_inventory_space");
        var id=item.QualifiedItemId;var before=InventorySnapshot().GetValueOrDefault(id);
        var point=menu.ItemsToGrabMenu.inventory[index].bounds.Center;
        var previous=Game1.oldKBState;Game1.oldKBState=new KeyboardState();
        try{menu.receiveLeftClick(point.X,point.Y);}finally{Game1.oldKBState=previous;}
        if(menu.heldItem is {} held){
            if(!Game1.player.addItemToInventoryBool(held))return End("fishing_loot_not_received");
            menu.heldItem=null;
        }
        var received=InventorySnapshot().GetValueOrDefault(id)-before;
        if(received<=0)return End("fishing_loot_not_received");
        _events.Add(new{state="native_fishing_loot_received",itemId=id,count=received});CaptureFishGains();return "running";
    }

    private string Fight(AiInputSimulator input)
    {
        if (Game1.currentLocation is not MineShaft mine || mine.mineLevel is < 1 or > 120) return End("fight_requires_ordinary_mine");
        _phase = "engage_monster";
        if(Game1.player.health<=35)return End("combat_health_critical_replan");
        var elapsedGameMinutes=(Game1.Date.TotalDays-_startDay)*1440+(Game1.timeOfDay/100*60+Game1.timeOfDay%100)-(_startTime/100*60+_startTime%100);
        if (elapsedGameMinutes >= Number("maxGameMinutes"))
            return End("model_selected_fight_time_limit_reached");
        if (Completed >= Number("maxKills")) return End("model_selected_kill_target_reached", true);
        var weapon = Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w => !w.isScythe() && w.QualifiedItemId == Text("weaponId"));
        if (weapon == null) return End("specified_melee_weapon_missing");
        if (!ReferenceEquals(Game1.player.CurrentTool, weapon)) Game1.player.CurrentToolIndex = Game1.player.Items.IndexOf(weapon);
        if (_target == null || !Game1.currentLocation.characters.Contains(_target))
        {
            if (_target != null)
            {
                if (_target.Health <= 0) { Completed++; _events.Add(new { state = "kill_verified", monster = _target.Name, floor = mine.mineLevel }); }
                else _events.Add(new { state = "target_disappeared_unverified", monster = _target.Name, hp = _target.Health });
                _target = null; _nav.Reset(); _approach = null; _approachEnemyTile=null;
                if (Completed >= Number("maxKills")) return End("model_selected_kill_target_reached", true);
            }
            _target = Game1.currentLocation.characters.OfType<Monster>().Where(m => m.Health > 0).OrderBy(m => Vector2.DistanceSquared(m.Position, Game1.player.Position)).FirstOrDefault();
            if (_target == null) return Completed > 0 ? End("no_more_visible_monsters") : End("no_visible_monsters");
            _targetHp = _target.Health; _attackAttempts = 0;
            _events.Add(new { state = "target_observed", monster = _target.Name, hp = _targetHp, floor = mine.mineLevel });
        }
        if(_target.Health<_targetHp){_targetHp=_target.Health;_events.Add(new{state="native_hp_delta_verified",target=_target.Name,hp=_targetHp});}
        var playerTile = Navigation.Current;
        var enemyTile = new GridPoint(_target.TilePoint.X, _target.TilePoint.Y);
        if(!_approachEnemyTile.HasValue||!_approachEnemyTile.Value.Equals(enemyTile)){_approach=null;_nav.Reset();_approachEnemyTile=enemyTile;}
        var delta = new StardewMiningAgent.Models.Vec2(enemyTile.X - playerTile.X, enemyTile.Y - playerTile.Y);
        var direction = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? delta.X >= 0 ? Direction.Right : Direction.Left : delta.Y >= 0 ? Direction.Down : Direction.Up;
        var facing=direction switch { Direction.Up=>0,Direction.Right=>1,Direction.Down=>2,_=>3 };
        Game1.player.faceDirection(facing);
        var attackFrame=NativeAttackFrame(weapon,Game1.player.GetBoundingBox(),Game1.player.GetToolLocation(ignoreClick:true),facing,_target.GetBoundingBox());
        var attackArea=attackFrame<0?Rectangle.Empty:NativeAttackArea(weapon,Game1.player.GetBoundingBox(),Game1.player.GetToolLocation(ignoreClick:true),facing,attackFrame);
        if (attackFrame>=0)
        {
            _phase = "native_weapon_attack";
            if (!_attackSent && !Game1.player.UsingTool)
            {
                // Use the native weapon swing and damage methods. A sword's
                // frame-zero hitbox can miss an adjacent enemy while its later
                // swing frame overlaps it, so evaluate and apply that exact
                // native hitbox without charging a second swing.
                var hpBefore=_target.Health;
                if(attackFrame==0||weapon.type.Value==1)weapon.setFarmerAnimating(Game1.player);
                else{
                    var swipeSpeed=(400-weapon.speed.Value*40)-Game1.player.addedSpeed*40f;
                    swipeSpeed*=1f-Game1.player.buffs.WeaponSpeedMultiplier;
                    weapon.doSwipe(weapon.type.Value,Game1.player.Position,Game1.player.FacingDirection,swipeSpeed/(weapon.type.Value==2?5:8),Game1.player);
                    var originalFrame=Game1.player.FarmerSprite.currentAnimationIndex;
                    Game1.player.FarmerSprite.currentAnimationIndex=attackFrame;
                    var hitLocation=Game1.player.GetToolLocation(ignoreClick:true);
                    weapon.DoDamage(Game1.currentLocation,(int)hitLocation.X,(int)hitLocation.Y,Game1.player.FacingDirection,1,Game1.player);
                    Game1.player.FarmerSprite.currentAnimationIndex=originalFrame;
                }
                _attackSent = true; _phaseAt = _clock.Now; _attackAttempts++;
                _events.Add(new { state = "attack_sent", weapon = weapon.QualifiedItemId, target = _target.Name, hpBefore, hpAfter=_target.Health, attempt = _attackAttempts,
                    playerTile,enemyTile,selectedHitFrame=attackFrame,attackArea=Box(attackArea),targetBox=Box(_target.GetBoundingBox()),
                    facing=Game1.player.FacingDirection,usingTool=Game1.player.UsingTool,animationIndex=Game1.player.FarmerSprite.currentAnimationIndex });
                if(_target.Health<hpBefore){_targetHp=_target.Health;_events.Add(new{state="native_hp_delta_verified",target=_target.Name,hp=_targetHp});}
            }
            else if (_clock.Now - _phaseAt > 900 && !Game1.player.UsingTool)
            {
                var damaged=_target.Health<_targetHp;
                if (damaged) { _targetHp = _target.Health; _events.Add(new { state = "native_hp_delta_verified", target = _target.Name, hp = _targetHp }); }
                else _events.Add(new{state="attack_window_finished",target=_target.Name,hp=_target.Health,playerTile,
                    facing=Game1.player.FacingDirection,usingTool=Game1.player.UsingTool,animationIndex=Game1.player.FarmerSprite.currentAnimationIndex});
                if(!damaged&&_attackAttempts>=4)return End("native_attack_no_verified_hp_change");
                _attackSent = false;
            }
            return "running";
        }
        _attackSent = false;
        _approach ??= FindReachableAttackPosition(enemyTile,weapon,_target);
        if (_approach == null){_events.Add(new{state="attack_position_search_failed",candidates=_attackPositionDiagnostics.ToArray()});return End("monster_has_no_reachable_attack_position");}
        _phase = "approach_monster";
        var moved = _nav.Move(Game1.currentLocation.NameOrUniqueName, _approach.Value, input, _interact);
        return moved is "blocked" or "unsupported" ? End(_nav.Reason) : "running";
    }

    private GridPoint? FindReachableAttackPosition(GridPoint enemy,MeleeWeapon weapon,Monster target)
    {
        IReadOnlyList<GridPoint>? bestPath=null;
        GridPoint? best=null;
        _attackPositionDiagnostics.Clear();
        var playerTile=Navigation.Current;var playerBox=Game1.player.GetBoundingBox();var targetBox=target.GetBoundingBox();var originalFacing=Game1.player.FacingDirection;
        for(var dx=-2;dx<=2;dx++)for(var dy=-2;dy<=2;dy++){
            var distance=Math.Abs(dx)+Math.Abs(dy);if(distance is <1 or >3)continue;
            var p=new GridPoint(enemy.X+dx,enemy.Y+dy);
            if(!Navigation.Passable(p)){_attackPositionDiagnostics.Add(new{tile=p,reason="not_passable"});continue;}
            var delta=new StardewMiningAgent.Models.Vec2(enemy.X-p.X,enemy.Y-p.Y);
            var direction=Math.Abs(delta.X)>=Math.Abs(delta.Y)?delta.X>=0?Direction.Right:Direction.Left:delta.Y>=0?Direction.Down:Direction.Up;
            var facing=direction switch{Direction.Up=>0,Direction.Right=>1,Direction.Down=>2,_=>3};
            var shift=new Vector2((p.X-playerTile.X)*64,(p.Y-playerTile.Y)*64);var box=playerBox;box.Offset((int)shift.X,(int)shift.Y);
            Game1.player.faceDirection(facing);
            var tool=Game1.player.GetToolLocation(ignoreClick:true)+shift;
            var frame=NativeAttackFrame(weapon,box,tool,facing,targetBox);
            var area=frame<0?Rectangle.Empty:NativeAttackArea(weapon,box,tool,facing,frame);
            if(frame<0){_attackPositionDiagnostics.Add(new{tile=p,reason="native_attack_box_miss",facing,tool=new{x=tool.X,y=tool.Y},target=Box(targetBox)});continue;}
            var path=_nav.PreviewPath(p);
            _attackPositionDiagnostics.Add(new{tile=p,reason=path==null?"path_unreachable":"attack_position_reachable",facing,selectedHitFrame=frame,tool=new{x=tool.X,y=tool.Y},area=Box(area),target=Box(targetBox),pathLength=path?.Count});
            if(path!=null&&(bestPath==null||path.Count<bestPath.Count)){bestPath=path;best=p;}
        }
        Game1.player.faceDirection(originalFacing);
        return best;
    }

    private static Rectangle NativeAttackArea(MeleeWeapon weapon,Rectangle playerBox,Vector2 toolLocation,int facing,int animationIndex)
    {
        var tile1=Vector2.Zero;var tile2=Vector2.Zero;
        return weapon.getAreaOfEffect((int)toolLocation.X,(int)toolLocation.Y,facing,ref tile1,ref tile2,playerBox,animationIndex);
    }
    private static int NativeAttackFrame(MeleeWeapon weapon,Rectangle playerBox,Vector2 toolLocation,int facing,Rectangle targetBox)
    {
        for(var frame=0;frame<=5;frame++)if(NativeAttackArea(weapon,playerBox,toolLocation,facing,frame).Intersects(targetBox))return frame;
        return -1;
    }
    private static object Box(Rectangle box)=>new{x=box.X,y=box.Y,width=box.Width,height=box.Height};

    private string MineTravel(AiInputSimulator input)
    {
        if (_targetFloor > 0 && Game1.currentLocation is not MineShaft)
        {
            _phase = "travel_to_mine_entrance";
            var enter = _nav.Move(MineShaft.GetLevelName(1), new(0, 0), input, _interact);
            return enter is "blocked" or "unsupported" ? End(_nav.Reason) : "running";
        }
        if (Game1.currentLocation is not MineShaft mine)
        {
            if (_targetFloor == 0) return End("already_outside_mine");
            return End("mine_entrance_did_not_enter_ordinary_mine");
        }
        if (mine.mineLevel > 120) return End("unsupported_mine_type");
        _startFloor = mine.mineLevel;
        if (mine.mineLevel == _targetFloor) return End("target_floor_observed");
        if (_targetFloor == 0 || _targetFloor < mine.mineLevel || (_targetFloor % 5 == 0 && mine.tileBeneathElevator != Vector2.Zero))
        {
            if (mine.tileBeneathElevator == Vector2.Zero) return End("no_observed_elevator_on_this_floor");
            var elevator = Tile(mine.tileBeneathElevator);
            var adjacent = _nav.BestAdjacent(elevator);
            if (adjacent == null) return End("elevator_unreachable");
            _phase = "walk_to_unlocked_elevator";
            var step = _nav.Move(mine.NameOrUniqueName, adjacent.Value, input, _interact);
            if (step is "blocked" or "unsupported") return End(_nav.Reason);
            if (step == "reached")
            {
                _interact(elevator);
                if (Game1.activeClickableMenu is MineElevatorMenu menu)
                {
                    var choice = menu.elevators.FirstOrDefault(e => int.TryParse(e.name, out var floor) && floor == _targetFloor);
                    if (choice == null) return End("target_floor_not_unlocked_in_native_elevator");
                    menu.receiveLeftClick(choice.bounds.Center.X, choice.bounds.Center.Y);
                    _phase = "native_elevator_transition";
                }
            }
            return "running";
        }
        if (!mine.ladderHasSpawned) return End("no_observed_ladder_model_must_choose_whether_to_fight_or_replan");
        var ladder = Tile(mine.tileBeneathLadder);
        var approachLadder = _nav.BestAdjacent(ladder);
        if (approachLadder == null) return End("observed_ladder_unreachable");
        _phase = "walk_to_observed_ladder";
        var result = _nav.Move(mine.NameOrUniqueName, approachLadder.Value, input, _interact);
        if (result is "blocked" or "unsupported") return End(_nav.Reason);
        if (result == "reached") { _interact(ladder); _phase = "native_ladder_transition"; }
        return "running";
    }

    // FishingRod reads the physical button inside tickUpdate, independently of IInputSimulator.
    // Let its own charge clock and launch animation run, releasing at full range.
    internal static bool ReleaseNativeCast(FishingRod __instance)
    {
        var task = _activeFishing;
        return task?._phase != "cast_line" || !ReferenceEquals(task._fishingRod, __instance)
            || __instance.castingPower >= 0.98f;
    }

    internal static void RecordLanding(FishingRod __instance, Farmer who)
    {
        var task = _activeFishing;
        if (task == null || !ReferenceEquals(task._fishingRod, __instance) || who != Game1.player) return;
        var x = (int)(__instance.bobber.X / 64);
        var y = (int)(__instance.bobber.Y / 64);
        task._events.Add(new { state = "native_bobber_landed", x, y, fishable = who.currentLocation.isTileFishable(x, y) });
    }
    internal static bool BobberBarInput(BobberBar bar)
    {
        var active = _activeFishing;
        if (active == null || active._skill != "fish_at") return false;
        if(bar.treasure&&active._treasureSeenCasts.Add(active._casts))active._events.Add(new{state="native_treasure_opportunity_observed",cast=active._casts});
        if(bar.treasureCaught&&active._treasureCaughtCasts.Add(active._casts))active._events.Add(new{state="native_treasure_minigame_caught",cast=active._casts,lootRequiresInventoryVerification=true});
        NativeFishingPhysics.SetNativeBarHeight(bar.bobberBarHeight);
        var state = new FishingState(
            BarPosition:(bar.bobberBarPos+bar.bobberBarHeight/2f)/568f,
            BarVelocity:bar.bobberBarSpeed/568f,
            FishPosition:Math.Clamp((bar.bobberPosition-2f)/568f,0f,1f),
            CatchProgress:bar.distanceFromCatching,
            TreasurePosition:bar.treasure?(float?)Math.Clamp((bar.treasurePosition-2f)/568f,0f,1f):null,
            TreasureProgress:bar.treasureCatchLevel,
            TreasureActive:bar.treasure&&!bar.treasureCaught);
        active._phase = "native_fishing_minigame";
        return NativeFishingController.Decide(state,active.Text("treasurePolicy")=="try"&&state.TreasureAvailable)==ControllerOutput.Press;
    }

    internal static bool OverrideNativeInput(out MouseState previousMouse, out KeyboardState previousKeyboard)
    {
        previousMouse = default; previousKeyboard=default;
        if (Game1.activeClickableMenu is not BobberBar bar || _activeFishing == null || OldMouse == null || OldKeyboard == null) return false;
        previousMouse = (MouseState)OldMouse.GetValue(null)!;
        previousKeyboard=(KeyboardState)OldKeyboard.GetValue(null)!;
        var pressed = BobberBarInput(bar);
        OldMouse.SetValue(null, new MouseState(0, 0, 0, pressed ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
        OldKeyboard.SetValue(null,default(KeyboardState));
        return true;
    }

    internal static void RestoreNativeInput(MouseState previousMouse,KeyboardState previousKeyboard)
    {OldMouse?.SetValue(null,previousMouse);OldKeyboard?.SetValue(null,previousKeyboard);}
    internal static void ClearActiveFishing() => _activeFishing = null;

    internal void Cleanup()
    {
        if(_skill!="fish_at"||!_ownsFishingAction||_fishingRod==null||_fishingCleanupRequested)return;
        _fishingCleanupRequested=true;_activeFishing=null;
        // A native loot container is the only remaining copy of unreceived
        // catches. Preserve it on failure rather than discarding the output.
        if(Game1.activeClickableMenu is ItemGrabMenu or DialogueBox)return;
        if(Game1.activeClickableMenu is BobberBar bar)bar.emergencyShutDown();
        _fishingRod.doneFishing(Game1.player,false);
    }

    private int CaptureFishGains()
    {
        foreach (var (id, count) in InventorySnapshot())
        {
            var gain = Math.Max(0, count - _inventoryBefore.GetValueOrDefault(id));
            var added = Math.Max(0, gain - _gains.GetValueOrDefault(id));
            if (added == 0) continue;
            _gains[id] = gain; _events.Add(new { state = "inventory_gain_verified", itemId = id, count = added });
        }
        return _gains.Sum(p => p.Value);
    }

    private string End(string reason, bool success = false) { _reason = reason; _activeFishing = null; return success ? "completed" : Completed > 0 ? "partial" : "failed"; }
    private string Text(string key) => _args.GetProperty(key).GetString() ?? "";
    private int Number(string key) => _args.GetProperty(key).GetInt32();
    private static GridPoint Tile(Vector2 p) => new((int)p.X, (int)p.Y);
    private static Dictionary<string, int> InventorySnapshot() => Game1.player.Items.Where(i => i != null).GroupBy(i => i!.QualifiedItemId).ToDictionary(g => g.Key, g => g.Sum(i => i!.Stack));
}
