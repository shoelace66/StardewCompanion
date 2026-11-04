using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using StardewValley.Monsters;

namespace StardewCompanion.Mod;

internal sealed class RoutineExecutor
{
    internal static readonly string[] Skills = new[]{ "go_to", "follow_player", "return_home", "sleep", "respond_dialogue", "water_area", "harvest_area", "refill_water", "forage_area", "clear_area", "till_area", "plant_area", "deposit_items", "withdraw_items", "fish_at", "fight_enemies", "mine_travel" }.Concat(BusinessCapabilities.All.Where(c=>!c.Query).Select(c=>c.Name)).ToArray();
    private DialogueBox? _responseSource;
    private string _responseText="";
    private bool _responseWasQuestion;
    private bool _responseClicked;
    private long _responseDeadline;
    private readonly BusinessExecutor? _business;
    private readonly AreaActionExecutor? _areaAction;
    private readonly SpecialActivityExecutor? _activity;
    private SpecialActivityExecutor? _encounterActivity;
    private readonly List<object> _encounterResults=new();
    private readonly string _encounterPolicy;
    private readonly List<object> _storageTransfers=new();
    private readonly List<object> _routeDropResults=new();
    private readonly HashSet<Debris> _visitedRouteDrops=new();
    private readonly Dictionary<string,int> _routeItemGains=new();
    private Debris? _activeRouteDrop;
    private long _activeRouteDropWait;
    private long _activeRouteDropDeadline;
    private int _uncollectedRouteDrops;
    internal object? BusinessDetails=>_business==null&&_activity==null&&_encounterResults.Count==0&&_storageTransfers.Count==0&&_routeDropResults.Count==0?null:new{stages=_business?.Stages.AsEnumerable()??Array.Empty<object>(),activity=_activity?.Details,encounters=_encounterResults.ToArray(),skipped=_business?.Skipped.AsEnumerable()??Array.Empty<object>(),storageTransfers=_storageTransfers.ToArray(),routeDrops=_routeDropResults.ToArray()};
    private readonly string _skill;
    private readonly JsonElement _args;
    private readonly Action<GridPoint> _interact;
    private readonly Navigation _nav = new();
    private readonly HashSet<GridPoint> _done = new();
    private GridPoint? _target;
    private GridPoint? _approach;
    private long _waitUntil;
    private bool _acted;
    private int _before;
    private string? _rewardId;
    private int _day;
    private long _menuWait;
    private long _lastProgress;
    private long _nextTargetScan;
    private string _waterMap = "";
    private List<GridPoint> _waterTiles = new();
    private Chest? _ownedChest;
    private bool _disposed;
    private bool _lockFailed;
    private Dictionary<string,int> _inventoryBefore = new();
    private HashSet<Debris> _debrisBefore = new();
    private readonly Dictionary<string,int> _actionGains = new();
    private readonly Dictionary<string,int> _collectedItems=new();
    internal Dictionary<string,int> CollectedItems=>_business?.CollectedItems??_areaAction?.CollectedItems??_collectedItems;
    internal object[] AffectedTiles=>_business?.AffectedTiles??_areaAction?.AffectedTiles??_done.Select(p=>(object)new{x=p.X,y=p.Y}).ToArray();
    internal object[] RemainingDrops()=>!StardewModdingAPI.Context.IsWorldReady||!_acted||_skill is not ("harvest_area" or "forage_area")?Array.Empty<object>():PendingDrops().Select(d=>(object)new{
        id=d.item?.QualifiedItemId??d.itemId.Value,type=d.debrisType.Value.ToString(),
        chunks=d.Chunks.Select(c=>new{x=c.position.X,y=c.position.Y}).ToArray()}).ToArray();
    private readonly List<GridPoint> _actionTargets = new();
    private bool _actionVerified;
    private int _actionCounted;
    private long _pickupDeadline;
    internal int Completed { get; private set; }
    internal string Reason { get; private set; } = "";
    internal string Phase { get; private set; } = "starting";
    internal RoutineExecutor(string skill, string arguments, Action<GridPoint> interact, string encounterPolicy = "interrupt")
    {
        _skill = skill; using var json = JsonDocument.Parse(arguments); _args = json.RootElement.Clone();
        _encounterPolicy=encounterPolicy is "fight" or "avoid" or "interrupt"?encounterPolicy:"interrupt";
        _nav.AvoidMonsters=_encounterPolicy=="avoid";
        if(BusinessCapabilities.Find(skill) is {Query:false})_business=new(skill,arguments,interact);
        if(skill is "clear_area" or "till_area" or "plant_area")_areaAction=new(skill,arguments,interact);
        if(skill is "fish_at" or "fight_enemies" or "mine_travel")_activity=new(skill,arguments,interact);
        if (_args.ValueKind != JsonValueKind.Object) throw new JsonException("Arguments must be an object");
        _interact = interact; _day = Game1.Date.TotalDays;
        if (skill is "deposit_items" or "withdraw_items")
        { Text("chestId"); Text("itemId"); if (Number("count", 0) is < 1 or > 9999) throw new JsonException("Invalid item count"); }
        if (skill == "follow_player" && !long.TryParse(Text("playerId"), out _)) throw new JsonException("Invalid player identity");
        if (skill == "go_to") { Text("location"); Number("x"); Number("y"); }
        if(skill=="go_to"&&Flag("collect")&&(Number("pickupRadius",2) is <0 or >8))throw new JsonException("Invalid pickup radius");
        if(skill=="go_to"&&Flag("collect"))_inventoryBefore=InventorySnapshot();
        if (skill.EndsWith("_area"))
        { Text("location"); Number("x"); Number("y"); if (Number("width", 1) is < 1 or > 32 || Number("height", 1) is < 1 or > 32) throw new JsonException("Invalid area dimensions"); }
    }
    private string Text(string key) => _args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new JsonException("Missing " + key);
    private int Number(string key, int? fallback = null) => _args.TryGetProperty(key, out var v) ? v.GetInt32() : fallback ?? throw new JsonException("Missing " + key);
    private string Map => _args.TryGetProperty("location", out var l) ? l.GetString()! : Game1.currentLocation.NameOrUniqueName;
    private bool InArea(GridPoint p) => p.X >= Number("x") && p.Y >= Number("y") && p.X < Number("x") + Number("width", 1) && p.Y < Number("y") + Number("height", 1);
    private bool Flag(string key)=>_args.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
    private string End(string reason) { Reason = reason; return Completed > 0 ? "partial" : "failed"; }
    internal string Tick(AiInputSimulator input)
    {
        var now = Environment.TickCount64;
        if(_skill=="respond_dialogue")return RespondDialogue(now);
        var sleeping=_skill=="sleep"||_skill=="end_day"&&Flag("sleep")||_skill=="recover"&&Text("method")=="sleep";
        if(sleeping&&Game1.activeClickableMenu is ShippingMenu shipping){
            if(_menuWait==0)_menuWait=now;
            if(now-_menuWait>60000)return End("shipping_confirmation_timeout");
            shipping.receiveKeyPress(Keys.Escape);Phase="confirm_shipping_and_save";return "running";
        }
        if(sleeping&&Game1.activeClickableMenu is LevelUpMenu level){
            if(level.isProfessionChooser)return End("profession_choice_required");
            var button=level.okButton.bounds.Center;level.receiveLeftClick(button.X,button.Y);Phase="confirm_level_up";return "running";
        }
        if(sleeping&&Game1.activeClickableMenu is SaveGameMenu){Phase="waiting_for_native_save";return "running";}
        if(Game1.Date.TotalDays!=_day){
            if(sleeping){
                if(Game1.activeClickableMenu!=null||!Game1.player.CanMove||Game1.endOfNightMenus.Count>0){Phase="waiting_for_native_save";return "running";}
                Reason="new_day_observed";return "completed";
            }
            return End("native_day_changed_replan_required:原版已换日，请根据新状态决定后续工作");
        }
        if(_skill!="fight_enemies"&&Game1.currentLocation is MineShaft&&Game1.currentLocation.characters.OfType<Monster>().Any(m=>m.Health>0))
        {
            if(_encounterPolicy=="interrupt")return End("encounter_policy_interrupt:visible_monsters_observed");
            if(_encounterPolicy=="fight"&&_encounterActivity==null)
            {
                var weapon=Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w=>!w.isScythe());
                if(weapon==null)return End("encounter_fight_selected_but_no_melee_weapon");
                _encounterActivity=new SpecialActivityExecutor("fight_enemies",JsonSerializer.Serialize(new{maxKills=1,maxGameMinutes=60,weaponId=weapon.QualifiedItemId}),_interact);
            }
        }
        if(_encounterActivity!=null)
        {
            var encounterStatus=_encounterActivity.Tick(input);
            if(encounterStatus=="running"){Phase="encounter_"+_encounterActivity.Phase;return "running";}
            if(_encounterActivity.Completed==0&&encounterStatus=="failed")return End("encounter_fight_failed:"+_encounterActivity.Reason);
            _encounterResults.Add(new{status=encounterStatus,reason=_encounterActivity.Reason,details=_encounterActivity.Details});
            _encounterActivity=null;_nav.Reset();
        }
        if(_activity!=null){var status=_activity.Tick(input);Completed=_activity.Completed;Reason=_activity.Reason;Phase=_activity.Phase;return status;}
        if(_business!=null){var status=_business.Tick(input);Completed=_business.Completed;Reason=_business.Reason;Phase=_business.Phase;return status;}
        if(_areaAction!=null){var status=_areaAction.Tick(input);Completed=_areaAction.Completed;Reason=_areaAction.Reason;Phase=_areaAction.Phase;return status;}
        if (Game1.eventUp || !Game1.player.CanMove || Game1.player.UsingTool || Game1.activeClickableMenu != null)
        {
            _nav.Suspend();
            if (_skill == "sleep" && Game1.activeClickableMenu is DialogueBox box && Game1.currentLocation.lastQuestionKey == "Sleep" && Game1.player.isInBed.Value)
            {
                box.selectedResponse = Array.FindIndex(box.responses, r => r.responseKey == "Yes");
                if (box.selectedResponse >= 0) box.receiveLeftClick(0, 0);
                Phase = "waiting_for_next_day"; return "running";
            }
            if (_skill is "deposit_items" or "withdraw_items" && Game1.activeClickableMenu is ItemGrabMenu menu) return Transfer(menu);
            if (_skill == "sleep" && Game1.player.isInBed.Value) { Phase = "waiting_for_next_day"; return "running"; }
            if (_menuWait == 0) _menuWait = now;
            if (now - _menuWait > 30000) return End("menu_or_animation_timeout");
            if(Game1.activeClickableMenu!=null)return End("unexpected_menu:"+Game1.activeClickableMenu.GetType().Name);
            Phase="waiting_for_game";return "running";
        }
        _menuWait = 0;
        if (!WorldView.Supported(Game1.currentLocation)) return End("unsupported_map");
        if (_skill is "go_to" or "return_home" or "sleep" or "follow_player") return Travel(input);
        if (_skill is "deposit_items" or "withdraw_items") return Storage(input);
        if (_skill == "refill_water" || _refillTarget != null) return Refill(input, _skill == "water_area");
        if (Game1.currentLocation.NameOrUniqueName != Map)
        {
            Phase = "travel_to_area";
            var state = _nav.Move(Map, new(Number("x", 0), Number("y", 0)), input, _interact);
            return state is "blocked" or "unsupported" ? End(_nav.Reason) : "running";
        }
        if (_acted)
        {
            if (now < _waitUntil) return "running";
            if (Satisfied(_target!.Value))
            {
                CaptureCompleted();
                if(_skill is "harvest_area" or "forage_area")
                {
                    var drops=PendingDrops();
                    if(drops.Length>0)
                    {
                        if(now>_pickupDeadline)return End("harvest_pickup_timeout");
                        Phase="collect_harvest_drops";
                        var chunk=drops.SelectMany(d=>d.Chunks).OrderBy(c=>Vector2.DistanceSquared(c.position.Value,Game1.player.Position)).FirstOrDefault();
                        if(chunk!=null)
                        {
                            var dropTile=new GridPoint((int)(chunk.position.X+32)/64,(int)(chunk.position.Y+32)/64);
                            {
                                GridPoint? goal=Navigation.Passable(dropTile)?dropTile:_nav.BestAdjacent(dropTile);
                                if(goal==null)return End("harvest_drop_unreachable");
                                var pickup=_nav.Move(Map,goal.Value,input,_interact);
                                if(pickup is "blocked" or "unsupported")return End("harvest_drop_unreachable");
                            }
                        }
                        return "running";
                    }
                    if(_actionCounted==0)return End("harvest_output_not_collected");
                }
            }
            else return End("action_not_verified");
            _acted = false; _target = null; _approach = null; _nav.Reset();
        }
        if (_target == null)
        {
            if (now < _nextTargetScan) return "running";
            _nextTargetScan = now + 250;
            // Re-read actual world every batch. No model call at a 16-target boundary.
            var batch = Targets().Where(p => !_done.Contains(p)).OrderBy(p => p.Distance(Navigation.Current)).Take(16).ToArray();
            if (batch.Length == 0) { Reason = "observed_targets_finished"; return "completed"; }
            _target = batch[0]; _approach = _nav.BestAdjacent(_target.Value);
            if (_approach == null) return End("target_unreachable");
        }
        var can = Game1.player.Items.OfType<WateringCan>().FirstOrDefault();
        if (_skill == "water_area" && (can == null || can.WaterLeft == 0))
        {
            if(can==null)return End("tool_missing");
            if(!Flag("autoRefill"))return End("watering_can_empty:autoRefill=false");
            _target=null;_approach=null;_nextTargetScan=0;_refillTarget=null;_refillApproach=null;_refillWait=0;_nav.Reset();
            return Refill(input, true);
        }
        Phase = "approach_target";
        var move = _nav.Move(Map, _approach!.Value, input, _interact);
        if (move is "blocked" or "unsupported") return End(_nav.Reason);
        if (move != "reached") return "running";
        var tile = _target.Value;
        // Player may finish a target while AI is approaching. Don't repeat the action.
        if (Satisfied(tile)) { _done.Add(tile); _target = null; _approach = null; _nav.Reset(); return "running"; }
        if (_skill == "refill_water") return Refill(input, false);
        var isScythe = _skill == "harvest_area" && GetSoil(tile)?.crop?.GetHarvestMethod() == StardewValley.GameData.Crops.HarvestMethod.Scythe;
        Tool? tool = _skill == "water_area" ? can : isScythe ? Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w => w.isScythe()) : null;
        if ((_skill == "water_area" || isScythe) && tool == null) return End("tool_missing");
        Face(tile);
        if (_skill == "harvest_area")
            _rewardId=RewardId(GetSoil(tile)?.crop);
        else if (_skill == "forage_area" && Game1.currentLocation.objects.TryGetValue(new(tile.X,tile.Y),out var forage)) _rewardId = forage.QualifiedItemId;
        if(_skill is "harvest_area" or "forage_area")
        {
            if(_rewardId==null)return End("harvest_reward_unknown");
            var data=_skill=="harvest_area"?GetSoil(tile)?.crop?.GetData():null;
            var min=data?.HarvestMinQuality??0;var max=data?.HarvestMaxQuality??4;
            if(!new[]{0,1,2,4}.Where(q=>q>=min&&q<=max).Any(q=>Game1.player.couldInventoryAcceptThisItem(ItemRegistry.Create(_rewardId,1,q))))return End("inventory_full");
            _inventoryBefore=InventorySnapshot();_debrisBefore=Game1.currentLocation.debris.ToHashSet();
            _actionGains.Clear();_actionTargets.Clear();_actionTargets.Add(tile);
            if(isScythe)
                foreach(var p in Game1.currentLocation.terrainFeatures.Pairs)
                {
                    var nearby=new GridPoint((int)p.Key.X,(int)p.Key.Y);
                    if(nearby.Distance(tile)<=3&&p.Value is HoeDirt dirt&&WorldView.Ready(dirt.crop))
                        _actionTargets.Add(nearby);
                }
        }
        _actionVerified=false;_actionCounted=0;_pickupDeadline=now+12000;
        _before = InventoryCount(_rewardId);
        if (tool != null) { Game1.player.CurrentToolIndex = Game1.player.Items.IndexOf(tool); input.Tool = true; }
        else _interact(tile);
        _acted = true; _waitUntil = now + 900; Phase = "verify_action";
        return "running";
    }
    private string Travel(AiInputSimulator input)
    {
        string map; GridPoint goal;
        if (_skill is "return_home" or "sleep")
        {
            if (Utility.getHomeOfFarmer(Game1.player) is not FarmHouse home) return End("home_unknown");
            map = home.NameOrUniqueName;
            var bed = _skill == "sleep" ? home.GetPlayerBedSpot() : home.getEntryLocation(); goal = new(bed.X, bed.Y);
        }
        else if (_skill == "follow_player")
        {
            var player = Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID.ToString() == Text("playerId"));
            if (player?.currentLocation == null) return End("player_unavailable");
            if (!WorldView.Supported(player.currentLocation)) { Phase = "waiting_at_supported_boundary"; return "running"; }
            map = player.currentLocation.NameOrUniqueName; goal = new(player.TilePoint.X, player.TilePoint.Y);
            if (map == Game1.currentLocation.NameOrUniqueName && Navigation.Current.Distance(goal) <= 2) { Phase = "following_wait"; return "running"; }
            if (map == Game1.currentLocation.NameOrUniqueName) goal = _nav.BestAdjacent(goal) ?? goal;
        }
        else { map = Text("location"); goal = new(Number("x"), Number("y")); }
        if(_skill=="go_to"&&Flag("collect"))
        {
            var collection=CollectAlongRoute(map,input);
            if(collection!=null)return collection;
        }
        Phase = "walking";
        var state = _nav.Move(map, goal, input, _interact, _skill=="go_to"&&Flag("breakNaturalObstacles"));
        if (state is "blocked" or "unsupported") return End(_nav.Reason);
        if (state != "reached" || _skill == "follow_player") return "running";
        if (_skill == "sleep") { Phase = "enter_bed"; input.Direction = "right"; return "running"; }
        if(_skill=="go_to"&&Flag("collect")&&_uncollectedRouteDrops>0)
        {Reason="destination_reached_with_uncollected_route_drops";return "completed";}
        Reason = "destination_observed"; return "completed";
    }
    private string? CollectAlongRoute(string destination,AiInputSimulator input)
    {
        var location=Game1.currentLocation;CaptureRouteItems();GridPoint segmentGoal;
        try{segmentGoal=location.NameOrUniqueName==destination?new GridPoint(Number("x"),Number("y")):_nav.SegmentGoal(destination);}catch(InvalidOperationException){return null;}
        var breakObstacles=Flag("breakNaturalObstacles");var path=_nav.PreviewPath(segmentGoal,breakObstacles);
        if(path==null)return null;
        if(_activeRouteDrop!=null)
        {
            if(Environment.TickCount64>=_activeRouteDropDeadline){
                _visitedRouteDrops.Add(_activeRouteDrop);_routeDropResults.Add(new{state="skipped",reason="pickup_timeout_or_inventory_full",itemId=_activeRouteDrop.item?.QualifiedItemId??_activeRouteDrop.itemId.Value});
                _uncollectedRouteDrops++;_activeRouteDrop=null;_nav.Reset();return "running";
            }
            if(!location.debris.Contains(_activeRouteDrop)||_activeRouteDrop.Chunks.Count==0)
            {
                _visitedRouteDrops.Add(_activeRouteDrop);CaptureRouteItems();
                _routeDropResults.Add(new{state="picked_up",itemId=_activeRouteDrop.item?.QualifiedItemId??_activeRouteDrop.itemId.Value});
                _activeRouteDrop=null;_activeRouteDropWait=0;_nav.Reset();return "running";
            }
            var chunk=_activeRouteDrop.Chunks.OrderBy(c=>Vector2.DistanceSquared(c.position.Value,Game1.player.Position)).First();
            var tile=new GridPoint((int)((chunk.position.X+32)/64),(int)((chunk.position.Y+32)/64));
            var approach=Navigation.Passable(tile)?tile:_nav.BestAdjacent(tile);
            if(approach==null)
            {
                _visitedRouteDrops.Add(_activeRouteDrop);_routeDropResults.Add(new{state="skipped",reason="drop_tile_unreachable",itemId=_activeRouteDrop.item?.QualifiedItemId??_activeRouteDrop.itemId.Value});
                _uncollectedRouteDrops++;
                _activeRouteDrop=null;_nav.Reset();return "running";
            }
            var move=_nav.Move(location.NameOrUniqueName,approach.Value,input,_interact,breakObstacles);
            if(move is "blocked" or "unsupported")
            {
                _visitedRouteDrops.Add(_activeRouteDrop);_routeDropResults.Add(new{state="skipped",reason=_nav.Reason,itemId=_activeRouteDrop.item?.QualifiedItemId??_activeRouteDrop.itemId.Value});
                _uncollectedRouteDrops++;
                _activeRouteDrop=null;_nav.Reset();return "running";
            }
            if(move=="reached")
            {
                Phase="pickup_route_drop";
                if(_activeRouteDropWait==0)_activeRouteDropWait=Environment.TickCount64+1200;
                else if(Environment.TickCount64>=_activeRouteDropWait){CaptureRouteItems();_activeRouteDropWait=Environment.TickCount64+250;}
            }
            return "running";
        }
        var radius=Number("pickupRadius",2);
        var candidate=location.debris.Where(d=>!_visitedRouteDrops.Contains(d)&&d.Chunks.Count>0&&(d.item!=null||!string.IsNullOrEmpty(d.itemId.Value)))
            .Select(d=>new{drop=d,tiles=d.Chunks.Select(c=>new GridPoint((int)((c.position.X+32)/64),(int)((c.position.Y+32)/64))).ToArray()})
            .Where(d=>d.tiles.Any(tile=>path.Min(p=>DistanceSquared(p,tile))<=radius*radius))
            .OrderBy(d=>d.tiles.Min(tile=>Navigation.Current.Distance(tile))).FirstOrDefault();
        if(candidate==null)return null;
        _activeRouteDrop=candidate.drop;_activeRouteDropWait=0;_activeRouteDropDeadline=Environment.TickCount64+15000;_nav.Reset();
        return "running";
    }
    private static int DistanceSquared(GridPoint a,GridPoint b)
    {var x=a.X-b.X;var y=a.Y-b.Y;return x*x+y*y;}
    private void CaptureRouteItems()
    {
        foreach(var (id,count) in InventorySnapshot())
        {
            var gain=Math.Max(0,count-_inventoryBefore.GetValueOrDefault(id));var added=Math.Max(0,gain-_routeItemGains.GetValueOrDefault(id));
            if(added==0)continue;_routeItemGains[id]=gain;_collectedItems[id]=_collectedItems.GetValueOrDefault(id)+added;Completed+=added;
        }
    }
    private IEnumerable<GridPoint> Targets()
    {
        if (_skill == "refill_water")
        {
            if (_waterMap != Game1.currentLocation.NameOrUniqueName)
            {
                _waterMap = Game1.currentLocation.NameOrUniqueName; _waterTiles.Clear();
                for (var x = 0; x < Game1.currentLocation.Map.Layers[0].LayerWidth; x++)
                    for (var y = 0; y < Game1.currentLocation.Map.Layers[0].LayerHeight; y++)
                        if (Game1.currentLocation.CanRefillWateringCanOnTile(x, y)) _waterTiles.Add(new(x, y));
            }
            foreach (var tile in _waterTiles) yield return tile;
            yield break;
        }
        if (_skill == "forage_area")
        {
            foreach (var p in Game1.currentLocation.objects.Pairs)
            { var tile = new GridPoint((int)p.Key.X, (int)p.Key.Y); if (InArea(tile) && p.Value.IsSpawnedObject && p.Value.isForage()) yield return tile; }
            yield break;
        }
        foreach (var p in Game1.currentLocation.terrainFeatures.Pairs)
        {
            var tile = new GridPoint((int)p.Key.X, (int)p.Key.Y);
            if (!InArea(tile) || p.Value is not HoeDirt dirt) continue;
            if (_skill == "water_area" && WorldView.NeedsWater(dirt,Game1.currentLocation)) yield return tile;
            if (_skill == "harvest_area" && WorldView.Ready(dirt.crop)) yield return tile;
        }
    }
    private static HoeDirt? GetSoil(GridPoint t) => Game1.currentLocation.terrainFeatures.TryGetValue(new(t.X, t.Y), out var f) ? f as HoeDirt : null;
    private static int InventoryCount(string? id) => id == null ? 0 : Game1.player.Items.Where(i=>i?.QualifiedItemId==id).Sum(i=>i.Stack);
    private static string? RewardId(Crop? crop)
    { var id=crop?.indexOfHarvest.Value;return string.IsNullOrEmpty(id)?null:id.StartsWith("(")?id:"(O)"+id; }
    private static Dictionary<string,int> InventorySnapshot()=>Game1.player.Items.Where(i=>i!=null).GroupBy(i=>i.QualifiedItemId).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.Stack));
    private Debris[] PendingDrops()=>Game1.currentLocation.debris.Where(d=>!_debrisBefore.Contains(d)
        && (d.item!=null||!string.IsNullOrEmpty(d.itemId.Value))
        && d.Chunks.Any(c=>_actionTargets.Any(t=>Vector2.DistanceSquared(c.position.Value,new Vector2(t.X*64,t.Y*64))<=256*256))).ToArray();
    internal void CaptureCompleted()
    {
        if(_business!=null){_business.Capture();return;}
        if(_areaAction!=null){_areaAction.Capture();return;}
        if(!StardewModdingAPI.Context.IsWorldReady||!_acted||_target==null||Game1.currentLocation.NameOrUniqueName!=Map||!Satisfied(_target.Value))return;
        if(_skill=="water_area") { if(!_actionVerified&&GetSoil(_target.Value)?.state.Value==1){Completed++;_actionVerified=true;_done.Add(_target.Value);}return; }
        if(_skill is not ("harvest_area" or "forage_area"))return;
        _actionVerified=true;
        var inventory=InventorySnapshot();
        // Count all items actually collected during this verified action, including mixed crops and byproducts.
        // The per-item receipt makes the unit explicit and avoids timing-dependent omission of instant pickups.
        foreach(var (id,count) in inventory)
        {
            var gain=Math.Max(0,count-_inventoryBefore.GetValueOrDefault(id));
            var added=Math.Max(0,gain-_actionGains.GetValueOrDefault(id));
            if(added==0)continue;
            _actionGains[id]=gain;CollectedItems[id]=CollectedItems.GetValueOrDefault(id)+added;
            Completed+=added;_actionCounted+=added;
        }
        foreach(var tile in _actionTargets.Where(Satisfied))_done.Add(tile);
    }
    private bool Satisfied(GridPoint t) => _skill switch
    {
        "water_area" => GetSoil(t)?.state.Value == 1 || GetSoil(t)?.crop == null,
        "harvest_area" => !WorldView.Ready(GetSoil(t)?.crop),
        "forage_area" => !Game1.currentLocation.objects.ContainsKey(new(t.X, t.Y)),
        "refill_water" => Game1.player.Items.OfType<WateringCan>().FirstOrDefault()?.WaterLeft > _before,
        _ => false
    };
    private GridPoint? _refillTarget;
    private GridPoint? _refillApproach;
    private long _refillWait;
    private string Refill(AiInputSimulator input, bool continueWater)
    {
        var can = Game1.player.Items.OfType<WateringCan>().FirstOrDefault();
        if (can == null) return End("watering_can_missing");
        if (_refillWait != 0)
        {
            if (Environment.TickCount64 < _refillWait) return "running";
            if (can.WaterLeft <= _before) return End("refill_not_verified");
            if(continueWater&&can.WaterLeft<40){_refillWait=0;return "running";}
            _refillTarget = null; _refillWait = 0; _nav.Reset();
            if (continueWater) return "running";
            Completed++; Reason = "water_refilled"; return "completed";
        }
        if (_refillTarget == null)
        {
            var l = Game1.currentLocation;
            var candidates = new List<GridPoint>();
            for (var x = 0; x < l.Map.Layers[0].LayerWidth; x++) for (var y = 0; y < l.Map.Layers[0].LayerHeight; y++)
                if (l.CanRefillWateringCanOnTile(x, y)) candidates.Add(new(x, y));
            foreach (var p in candidates.OrderBy(p => p.Distance(Navigation.Current)))
            { var adjacent = _nav.BestAdjacent(p); if (adjacent != null) { _refillTarget = p; _refillApproach = adjacent; break; } }
            if (_refillTarget == null) return End("no_reachable_water");
        }
        Phase = "refill_water";
        var move = _nav.Move(Game1.currentLocation.NameOrUniqueName, _refillApproach!.Value, input, _interact);
        if (move is "blocked" or "unsupported") return End(_nav.Reason);
        if (move == "reached")
        {
            Face(_refillTarget.Value); Game1.player.CurrentToolIndex = Game1.player.Items.IndexOf(can);
            _before = can.WaterLeft; input.Tool = true; _refillWait = Environment.TickCount64 + 1000;
        }
        return "running";
    }
    private static void Face(GridPoint tile)
    { var p = Navigation.Current; Game1.player.faceDirection(tile.X > p.X ? 1 : tile.X < p.X ? 3 : tile.Y > p.Y ? 2 : 0); }
    private string Storage(AiInputSimulator input)
    {
        if (Game1.currentLocation.NameOrUniqueName != Map)
        { var m = _nav.Move(Map, new(Number("x"), Number("y")), input, _interact); return m is "blocked" or "unsupported" ? End(_nav.Reason) : "running"; }
        var tile = new GridPoint(Number("x"), Number("y"));
        if (!Game1.currentLocation.objects.TryGetValue(new(tile.X, tile.Y), out var obj) || obj is not Chest chest || !WorldView.WorkChest(chest) || !chest.modData.TryGetValue(WorldView.ChestKey, out var id) || id != Text("chestId")) return End("chest_binding_invalid");
        if (_approach == null) _approach = _nav.BestAdjacent(tile);
        if (_approach == null) return End("chest_unreachable");
        var move = _nav.Move(Map, _approach.Value, input, _interact);
        if (move is "blocked" or "unsupported") return End(_nav.Reason);
        if (move == "reached")
        {
            if (_waitUntil == 0)
            {
                _waitUntil=Environment.TickCount64+3000;_ownedChest=chest;
                // Guard a delayed native mutex callback so cancellation cannot open a new menu later.
                chest.GetMutex().RequestLock(()=>{if(_disposed)chest.GetMutex().ReleaseLock();else _interact(tile);},()=>_lockFailed=true);
            }
            else if(_lockFailed)return End("chest_busy_or_open_failed");
            else if (Environment.TickCount64 > _waitUntil) return End("chest_busy_or_open_failed");
        }
        Phase = "open_authorized_chest"; return "running";
    }
    private string Transfer(ItemGrabMenu menu)
    {
        if (menu.sourceItem is not Chest chest || !ReferenceEquals(chest,_ownedChest) || !WorldView.WorkChest(chest) || !chest.GetMutex().IsLockHeld() || !chest.modData.TryGetValue(WorldView.ChestKey, out var id) || id != Text("chestId")) return End("unexpected_container");
        var itemId = Text("itemId");
        if (!_args.TryGetProperty("allowedIds", out var allowed) || !allowed.EnumerateArray().Any(v => v.GetString() == itemId)) return End("item_not_authorized");
        var source = _skill == "deposit_items" ? menu.inventory.actualInventory : menu.ItemsToGrabMenu.actualInventory;
        var destination = _skill == "deposit_items" ? menu.ItemsToGrabMenu.actualInventory : Game1.player.Items;
        if (Completed >= Number("count")) { menu.receiveKeyPress(Keys.Escape); Reason = "transfer_verified"; return "completed"; }
        var quality = _args.TryGetProperty("quality", out var q) ? q.GetInt32() : -1;
        var transferred = 0;
        var eligibleSourceFound = false;
        var remaining = Number("count") - Completed;
        foreach (var pair in source.Select((item, index) => (item, index)).ToArray())
        {
            var item = pair.item;
            if (item == null || item.QualifiedItemId != itemId || item is Tool || item.modData.ContainsKey("StardewCompanion/Protected") || (quality >= 0 && item.Quality != quality)) continue;
            if (item.IsRecipe || item.QualifiedItemId is "(O)434" or "(O)326") { menu.receiveKeyPress(Keys.Escape); return End("item_has_consumption_side_effect"); }
            eligibleSourceFound = true;
            var amount = Math.Min(remaining, item.Stack);
            var moved = MoveStack(item, amount, destination, _skill == "deposit_items" ? chest.GetActualCapacity() : Game1.player.MaxItems);
            if (moved > 0)
            {
                item.Stack -= moved;
                if (item.Stack <= 0) source[pair.index] = null;
                transferred += moved; remaining -= moved;
            }
            if (remaining == 0) break;
        }
        if (transferred == 0)
        {
            menu.receiveKeyPress(Keys.Escape);
            return End(!eligibleSourceFound?"source_empty_or_protected_or_quality_mismatch":_skill=="withdraw_items"?"inventory_full":"chest_full");
        }
        _storageTransfers.Add(new{direction=_skill=="deposit_items"?"to_chest":"from_chest",itemId,quality=quality<0?(int?)null:quality,count=transferred,chestId=id});
        Completed += transferred;
        Phase = "transfer_items";
        if(Completed>=Number("count")){Reason="transfer_verified";return "completed";}return "running";
    }
    private string RespondDialogue(long now)
    {
        var popup=Game1.activeClickableMenu as DialogueBox;
        if(_responseClicked&&(!ReferenceEquals(popup,_responseSource)||popup!.getCurrentString()!=_responseText||popup.isQuestion!=_responseWasQuestion)){
            Reason=_responseWasQuestion?"dialogue_response_selected":"dialogue_acknowledged";return "completed";
        }
        if(popup==null)return End("dialogue_not_open");
        if(_responseDeadline==0){_responseDeadline=now+10000;_responseSource=popup;_responseText=popup.getCurrentString();_responseWasQuestion=popup.isQuestion;}
        if(now>=_responseDeadline)return End("dialogue_response_not_verified");
        Phase="waiting_for_dialogue_response";
        if(popup.transitioning||popup.safetyTimer>0)return "running";
        if(popup.characterIndexInDialogue<popup.getCurrentString().Length-1){popup.receiveLeftClick(popup.x+64,popup.y+64);return "running";}
        var key=_args.GetProperty("responseKey").GetString()??"";
        if(popup.isQuestion){
            var index=Array.FindIndex(popup.responses,r=>r.responseKey==key);
            if(index<0)return End("dialogue_response_unavailable:"+string.Join(",",popup.responses.Select(r=>r.responseKey)));
            popup.selectedResponse=index;
        }else if(key!="")return End("acknowledgement_requires_empty_response_key");
        popup.receiveLeftClick(popup.x+64,popup.y+64);
        _responseClicked=popup.transitioning||!ReferenceEquals(Game1.activeClickableMenu,popup)||popup.getCurrentString()!=_responseText||popup.isQuestion&&popup.selectedResponse==-1;
        return "running";
    }
    private static int MoveStack(Item source, int amount, IList<Item> destination, int capacity)
    {
        var copy = source.getOne(); copy.Stack = amount;
        var remainder = Utility.addItemToThisInventoryList(copy, destination, capacity);
        return amount - (remainder?.Stack ?? 0);
    }
    internal bool Cleanup()
    {
        _disposed=true;
        SpecialActivityExecutor.ClearActiveFishing();
        if(!StardewModdingAPI.Context.IsWorldReady)return true;
        if(_business?.Cleanup()==false)return false;
        if(_areaAction?.Cleanup()==false)return false;
        if(_ownedChest==null)return true;
        if(Game1.activeClickableMenu is ItemGrabMenu menu && ReferenceEquals(menu.sourceItem,_ownedChest))
        {
            if(menu.heldItem is Item held)
            {
                if(Game1.player.addItemToInventoryBool(held))menu.heldItem=null;
                else {Game1.createItemDebris(held,Game1.player.Position,Game1.player.FacingDirection,Game1.currentLocation);menu.heldItem=null;}
            }
            menu.exitThisMenu();
        }
        if(_ownedChest.GetMutex().IsLockHeld())_ownedChest.GetMutex().ReleaseLock();
        return true;
    }
    internal bool ProgressDue => Environment.TickCount64 - _lastProgress > 1000;
    internal void ProgressSent() => _lastProgress = Environment.TickCount64;
}
