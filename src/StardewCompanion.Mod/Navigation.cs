using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewCompanion.Mod;

internal sealed record MapEdge(string From, string To, GridPoint Tile, bool Interact, GridPoint? ArrivalTile, int? OpenTime=null, int? CloseTime=null);

internal sealed class Navigation
{
    private List<GridPoint>? _path;
    private int _index;
    private string _map = "";
    private GridPoint _goal;
    private Vector2 _lastPosition;
    private long _lastMove;
    private int _replans;
    private int _entranceReplans;
    private long _transitionStarted;
    private string _observedMap = "";
    private string _previousTransitionFrom = "";
    private string _previousTransitionTo = "";
    private GridPoint? _arrivalClearance;
    private HashSet<GridPoint> _arrivalExitTiles=new();
    private int _mapBounces;
    private readonly HashSet<MapEdge> _deadEndEntrances = new();
    private readonly HashSet<MapEdge> _blockedEntrances = new();
    private MapEdge? _incomingEdge;
    private string _routeFrom="";
    private string _routeTo="";
    private IReadOnlyList<MapEdge>? _routeCache;
    private GridPoint? _routeApproachCache;
    private bool _routeCacheInitialized;
    private GridPoint? _breakingTarget;
    private GridPoint? _breakingApproach;
    private long _breakWaitUntil;
    private string? _breakingType;
    private string? _breakingItemId;
    private string? _breakingTool;
    private readonly List<object> _brokenObstacles=new();
    internal object[] BrokenObstacles=>_brokenObstacles.ToArray();
    internal string Reason { get; private set; } = "";
    internal bool AvoidMonsters { get; set; }
    internal static IEnumerable<MapEdge> Edges()
    {
        var home = Utility.getHomeOfFarmer(Game1.player);
        var locations = Game1.locations.Concat(Game1.locations.SelectMany(l => l.buildings.Select(b => b.GetIndoors())).Where(l => l != null)).Append(Game1.currentLocation).Distinct();
        foreach (var l in locations.Where(WorldView.Supported))
        {
            if(l is Farm farm && farm.GetMainFarmHouse()==null && ReferenceEquals(home,Game1.getLocationFromName("FarmHouse")))
            {var entry=farm.GetMainFarmHouseEntry();yield return new(l.NameOrUniqueName,home.NameOrUniqueName,new(entry.X,entry.Y-1),true,Arrival(home,l.NameOrUniqueName));}
            foreach (var w in l.warps)
            {
                if (w.npcOnly.Value) continue;
                var target = Game1.getLocationFromName(w.TargetName);
                if (target != null && WorldView.Supported(target)) yield return new(l.NameOrUniqueName, target.NameOrUniqueName, new(w.X, w.Y), false, new(w.TargetX,w.TargetY));
            }
            foreach (var building in l.buildings)
            {
                if(building.GetIndoors() is not {} indoors||!WorldView.Supported(indoors))continue;
                yield return new(l.NameOrUniqueName, indoors.NameOrUniqueName,
                    new(building.tileX.Value + building.humanDoor.X, building.tileY.Value + building.humanDoor.Y), true, Arrival(indoors,l.NameOrUniqueName));
            }
            foreach(var door in l.doors.Pairs){
                var target=Game1.getLocationFromName(door.Value);
                if(!WorldView.Supported(target))continue;
                int? open=null,close=null;
                var action=l.doesTileHaveProperty(door.Key.X,door.Key.Y,"Action","Buildings")?.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(action is {Length:>=6}&&action[0]=="LockedDoorWarp"){
                    if(int.TryParse(action[4],out var openTime))open=openTime;
                    if(int.TryParse(action[5],out var closeTime))close=closeTime;
                }
                yield return new(l.NameOrUniqueName,target!.NameOrUniqueName,new(door.Key.X,door.Key.Y),true,Arrival(target,l.NameOrUniqueName),open,close);
            }
        }
    }
    private static GridPoint? Arrival(GameLocation target,string from)
    {
        var warp=target.warps.FirstOrDefault(w=>!w.npcOnly.Value && w.TargetName==from);
        return warp==null?null:new GridPoint(warp.X,warp.Y);
    }
    internal static IReadOnlyList<MapEdge>? Route(string from, string to, HashSet<MapEdge>? excluded = null,HashSet<MapEdge>? blocked = null)
    {
        var currentMap=Game1.currentLocation?.NameOrUniqueName??"";var current=Current;
        var edges=Edges().Where(edge=>blocked?.Contains(edge)!=true&&
            excluded?.Any(previous => previous.From == edge.From && previous.To == edge.To && Nullable.Equals(previous.ArrivalTile, edge.ArrivalTile)) != true).ToList();
        while(true){
            var route=Pathfinding.Dijkstra(from,to,edges,e=>e.From,e=>e.To,
                e=>100+(e.From==currentMap?e.Tile.Distance(current):0));
            if(route==null||route.Count==0||from!=currentMap)return route;
            var first=route[0];var navigator=new Navigation();bool reachable;
            if(first.Interact)reachable=navigator.BestAdjacent(first.Tile)!=null;
            else if(Game1.currentLocation?.Map is {} currentMapData){
                var layer=currentMapData.Layers[0];
                var goal=new GridPoint(Math.Clamp(first.Tile.X,0,layer.LayerWidth-1),Math.Clamp(first.Tile.Y,0,layer.LayerHeight-1));
                reachable=navigator.PreviewPath(goal)!=null;
            }else reachable=false;
            if(reachable)return route;
            // Only the first edge must be reachable from the player's current
            // position. Exclude that single blocked edge and ask Dijkstra for
            // an alternative instead of running A* for every door on this map.
            edges.RemoveAll(edge=>edge.Equals(first));
        }
    }
    internal static GridPoint Current => new(Game1.player.TilePoint.X, Game1.player.TilePoint.Y);
    internal GridPoint SegmentGoal(string destination)
    {
        if(Game1.currentLocation.NameOrUniqueName==destination)return Current;
        var edge=Route(Game1.currentLocation.NameOrUniqueName,destination)?.FirstOrDefault()
            ??throw new InvalidOperationException("no_supported_route");
        return edge.Interact?BestAdjacent(edge.Tile)??throw new InvalidOperationException("exit_unreachable"):
            new GridPoint(Math.Clamp(edge.Tile.X,0,Game1.currentLocation.Map.Layers[0].LayerWidth-1),Math.Clamp(edge.Tile.Y,0,Game1.currentLocation.Map.Layers[0].LayerHeight-1));
    }
    internal IReadOnlyList<GridPoint>? PreviewPath(GridPoint goal,bool breakNaturalObstacles=false)
        =>Pathfinding.AStar(Current,goal,p=>Cost(p,breakNaturalObstacles));
    internal static bool Passable(GridPoint p)
    {
        var l = Game1.currentLocation;
        if (p.X < 0 || p.Y < 0 || p.X >= l.Map.Layers[0].LayerWidth || p.Y >= l.Map.Layers[0].LayerHeight) return false;
        // Query native collision at a centered farmer bounding box; never alter position or apply collision side effects.
        return !l.isCollidingPosition(new Rectangle(p.X * 64 + 8, p.Y * 64 + 16, 48, 32), Game1.viewport,
            true, 0, false, Game1.player, true, skipCollisionEffects: true);
    }
    internal void Reset() { _path = null; _map = ""; _replans = 0; _entranceReplans=0;_transitionStarted = 0; _observedMap="";_previousTransitionFrom="";_previousTransitionTo="";_arrivalClearance=null;_mapBounces=0;_deadEndEntrances.Clear();_blockedEntrances.Clear();_incomingEdge=null;_routeFrom="";_routeTo="";_routeCache=null;_routeApproachCache=null;_routeCacheInitialized=false;_breakingTarget=null;_breakingApproach=null;_breakWaitUntil=0;_breakingType=null;_breakingItemId=null;_breakingTool=null;Reason = ""; }
    internal void Suspend() { _lastMove = Environment.TickCount64; _lastPosition = Game1.player.Position;if(_transitionStarted!=0)_transitionStarted=_lastMove; }
    // Returns reached / moving / blocked / unsupported. Only emits normal held direction input.
    internal string Move(string location, GridPoint goal, AiInputSimulator input, Action<GridPoint> interact, bool breakNaturalObstacles=false)
    {
        var here = Game1.currentLocation;
        if (!WorldView.Supported(here)) { Reason = "unsupported_current_map"; return "unsupported"; }
        var currentMap=here.NameOrUniqueName;
        if(_observedMap=="")_observedMap=currentMap;
        else if(currentMap!=_observedMap){
            var from=_observedMap;
            var traversed = _routeCache?.FirstOrDefault(edge => edge.From == from && edge.To == currentMap);
            if(from==_previousTransitionTo&&currentMap==_previousTransitionFrom){
                // The farmer re-entered the warp it just came through (A->B->A),
                // usually because arrival clearance did not step it off the
                // incoming warp tile before re-navigating. Recover by clearing
                // route state and stepping away from that warp, but bound the
                // retries so a genuine oscillation still terminates.
                if (_incomingEdge is {} failedEntrance) _deadEndEntrances.Add(failedEntrance);
                _observedMap=currentMap;input.Clear();
                _routeCache=null;_routeApproachCache=null;_routeCacheInitialized=false;
                _transitionStarted=0;_path=null;_previousTransitionFrom="";_previousTransitionTo="";
                _arrivalClearance=ArrivalClearance(from);
                if(++_mapBounces>3){Reason=$"map_transition_bounced:{from}->{currentMap}";return "blocked";}
                return "moving";
            }
            // Do not reset _mapBounces here: an A<->B oscillation alternates a
            // bounce with a normal hop, so resetting on every normal transition
            // kept the counter pinned below the threshold and spun forever. It is
            // cleared on reaching the goal and in Reset() instead.
            _incomingEdge = traversed;
            _previousTransitionFrom=from;_previousTransitionTo=currentMap;_observedMap=currentMap;
            _routeCache=null;_routeApproachCache=null;_routeCacheInitialized=false;_transitionStarted=0;_path=null;
            if(currentMap!=location||Current.Distance(goal)>1)_arrivalClearance=ArrivalClearance(from);
        }
        if(_arrivalClearance is {} clearTile){
            var clearing=Local(clearTile,input,false);
            if(clearing is "blocked" or "unsupported"){Reason="arrival_clearance_unreachable:"+Reason;return clearing;}
            if(clearing!="reached")return clearing;
            _arrivalClearance=null;_path=null;input.Clear();return "moving";
        }
        if (here.NameOrUniqueName != location)
        {
            if(!_routeCacheInitialized||_routeFrom!=here.NameOrUniqueName||_routeTo!=location){
                if(_routeFrom!=here.NameOrUniqueName||_routeTo!=location)_entranceReplans=0;
                if(_routeTo!=location)_blockedEntrances.Clear();
                _routeFrom=here.NameOrUniqueName;_routeTo=location;
                var selected=Route(_routeFrom,_routeTo,_deadEndEntrances,_blockedEntrances);
                if(selected==null&&_deadEndEntrances.Count>0)
                    selected=Route(_routeFrom,_routeTo,blocked:_blockedEntrances);
                var first=selected?.FirstOrDefault();
                // A map can contain disconnected walkable regions (e.g. Backwoods).
                // If the only reachable route takes us straight back, avoid this
                // entrance when planning the next leg from the previous map.
                if (first != null && _incomingEdge is {} entrance && first.To == entrance.From)
                    _deadEndEntrances.Add(entrance);
                SetRoute(selected,here);
                _routeCacheInitialized=true;
            }
            var route = _routeCache;
            if (route == null || route.Count == 0) { Reason = "no_supported_route"; return "blocked"; }
            var edge = route[0];
            // A general go_to may target a shop map before it opens. Native
            // LockedDoorWarp actions otherwise display a blocking "It's
            // locked" dialogue on every interaction attempt. Stop before
            // approaching the door and return the native retry window instead.
            var targetLocation=Game1.getLocationFromName(edge.To);
            var townKeyBypass=Game1.player.HasTownKey&&targetLocation?.InValleyContext()==true;
            if(!townKeyBypass&&edge.OpenTime is {} openTime&&Game1.timeOfDay<openTime){
                Reason=$"destination_closed_until:{edge.To}:current={Game1.timeOfDay}:retryAt={openTime}";
                input.Clear();return "blocked";
            }
            if(!townKeyBypass&&edge.CloseTime is {} closeTime&&Game1.timeOfDay>=closeTime){
                Reason=$"destination_closed_for_today:{edge.To}:current={Game1.timeOfDay}:close={closeTime}";
                input.Clear();return "blocked";
            }
            var approach = _routeApproachCache;
            if (approach == null) { Reason = "exit_unreachable"; return RecoverEntrance(edge,here,input); }
            var state = Local(approach.Value,input,false,entrance:true);
            if(state=="blocked")return RecoverEntrance(edge,here,input);
            if (state != "reached") return state;
            if (_transitionStarted == 0) _transitionStarted = Environment.TickCount64;
            if (Environment.TickCount64 - _transitionStarted > 5000) { Reason = "exit_did_not_transition"; return RecoverEntrance(edge,here,input); }
            if (edge.Interact) interact(edge.Tile);
            else
            {
                // Warp touches a tile boundary. Keep walking toward its boundary while waiting for native loading.
                input.Direction = edge.Tile.X <= 0 ? "left" : edge.Tile.Y <= 0 ? "up"
                    : edge.Tile.X >= here.Map.Layers[0].LayerWidth - 1 ? "right" : "down";
            }
            return "moving";
        }
        _transitionStarted = 0;
        var reachedGoal = Local(goal, input, breakNaturalObstacles);
        if (reachedGoal == "reached") { _mapBounces = 0; _deadEndEntrances.Clear(); _blockedEntrances.Clear(); _incomingEdge = null; }
        return reachedGoal;
    }

    private void SetRoute(IReadOnlyList<MapEdge>? route,GameLocation here)
    {
        _routeCache=route;
        var first=route?.FirstOrDefault();
        _routeApproachCache=first==null?null:first.Interact?BestAdjacent(first.Tile):new GridPoint(
            Math.Clamp(first.Tile.X,0,here.Map.Layers[0].LayerWidth-1),Math.Clamp(first.Tile.Y,0,here.Map.Layers[0].LayerHeight-1));
    }

    private string RecoverEntrance(MapEdge blocked,GameLocation here,AiInputSimulator input)
    {
        input.Clear();
        if(_entranceReplans>=3){
            if(Reason=="entrance_approach_stalled")Reason="blocked_after_3_replans";
            return "blocked";
        }
        _entranceReplans++;
        _blockedEntrances.Add(blocked);
        var alternative=Route(_routeFrom,_routeTo,_deadEndEntrances,_blockedEntrances);
        if(alternative!=null)SetRoute(alternative,here);
        else{
            _blockedEntrances.Remove(blocked);
            if(Reason!="entrance_approach_stalled")return "blocked";
        }
        _path=null;_lastMove=Environment.TickCount64;_lastPosition=Game1.player.Position;_transitionStarted=0;
        return "moving";
    }

    internal object FailureEvidence(string destination,GridPoint? target,string reason)
    {
        var here=Game1.currentLocation.NameOrUniqueName;
        var entrance=here==destination?null:_routeCache?.FirstOrDefault();
        var candidates=here==destination&&target is {} tile?ReachableNear(tile):Array.Empty<GridPoint>();
        var releaseCondition=reason.StartsWith("destination_closed_until:",StringComparison.Ordinal)?"wait_until_observed_opening_time":
            reason.StartsWith("destination_closed_for_today:",StringComparison.Ordinal)?"new_day_and_recheck_hours":
            candidates.Length>0?"choose_a_returned_reachable_tile":"recheck_current_map_and_route_before_retry";
        return new{reason,blockedMap=here,playerTile=Current,destination,target,entrance,
            entranceReplans=_entranceReplans,reachableTiles=candidates,releaseCondition};
    }

    private GridPoint[] ReachableNear(GridPoint goal)
    {
        var here=Game1.currentLocation;
        if(!WorldView.Supported(here))return Array.Empty<GridPoint>();
        var exits=Edges().Where(edge=>edge.From==here.NameOrUniqueName).Select(edge=>edge.Tile).ToHashSet();
        return (from x in Enumerable.Range(goal.X-4,9)
                from y in Enumerable.Range(goal.Y-4,9)
                let tile=new GridPoint(x,y)
                let distance=tile.Distance(goal)
                where distance is >0 and <=4&&!exits.Contains(tile)&&Passable(tile)
                orderby distance,tile.Distance(Current),x,y
                select tile).Where(tile=>Pathfinding.AStar(Current,tile,p=>exits.Contains(p)?null:Cost(p,false))!=null)
            .Take(3).ToArray();
    }
    private GridPoint? ArrivalClearance(string previousMap)
    {
        var location=Game1.currentLocation;var current=Current;var layer=location.Map.Layers[0];
        var exits=location.warps.Where(w=>!w.npcOnly.Value && w.TargetName==previousMap).Select(w=>new GridPoint(w.X,w.Y)).Where(p=>p.Distance(current)<=3).ToArray();
        if(exits.Length==0)return null;
        _arrivalExitTiles=exits.ToHashSet();
        var candidates=Enumerable.Range(Math.Max(1,current.X-4),Math.Max(0,Math.Min(layer.LayerWidth-2,current.X+4)-Math.Max(1,current.X-4)+1))
            .SelectMany(x=>Enumerable.Range(Math.Max(1,current.Y-4),Math.Max(0,Math.Min(layer.LayerHeight-2,current.Y+4)-Math.Max(1,current.Y-4)+1)).Select(y=>new GridPoint(x,y)))
            .Where(p=>exits.All(exit=>p.Distance(exit)>=2)&&Passable(p)).OrderBy(p=>p.Distance(current));
        foreach(var candidate in candidates){
            var path=Pathfinding.AStar(current,candidate,p=>Passable(p)&&(p.Equals(current)||exits.All(exit=>!exit.Equals(p))));
            if(path!=null)return candidate;
        }
        // Tight corridors may keep every tile within two of the incoming warp.
        // Accept a tile that is merely off the warp so the farmer still leaves
        // the exit instead of re-triggering it.
        foreach(var candidate in candidates.Where(p=>exits.All(exit=>!exit.Equals(p)))){
            var path=Pathfinding.AStar(current,candidate,p=>Passable(p)&&(p.Equals(current)||exits.All(exit=>!exit.Equals(p))));
            if(path!=null)return candidate;
        }
        return null;
    }
    internal GridPoint? BestAdjacent(GridPoint target)
    {
        // Interaction reach is cardinal; pick the closest actually reachable neighbor, not only Manhattan distance.
        IReadOnlyList<GridPoint>? best = null;
        foreach (var p in target.Neighbors())
        {
            if (!Passable(p) && !p.Equals(Current)) continue;
            var path = Pathfinding.AStar(Current, p, Cost);
            if (path != null && (best == null || path.Count < best.Count)) best = path;
        }
        return best == null ? null : best[best.Count - 1];
    }
    private static Tool? BreakTool(GridPoint p)
    {
        var location=Game1.currentLocation;var tile=new Vector2(p.X,p.Y);
        if(location.objects.TryGetValue(tile,out var obj))
        {
            if(obj.Name=="Stone")return Game1.player.Items.OfType<Pickaxe>().FirstOrDefault();
            if(obj.Name is "Twig" or "Weeds")return Game1.player.Items.OfType<Axe>().FirstOrDefault();
        }
        if(location.terrainFeatures.TryGetValue(tile,out var feature))
        {
            if(feature is Tree tree&&!tree.tapped.Value)return Game1.player.Items.OfType<Axe>().FirstOrDefault();
            if(feature is Grass)return Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(t=>t.isScythe());
        }
        return null;
    }
    private static int? EntryCost(GridPoint p,bool breakNaturalObstacles)
        =>Passable(p)?1:breakNaturalObstacles&&BreakTool(p)!=null?12:null;
    private int? Cost(GridPoint p)=>Cost(p,false);
    private int? Cost(GridPoint p,bool breakNaturalObstacles)
    {
        if(_arrivalClearance!=null&&!p.Equals(Current)&&_arrivalExitTiles.Contains(p))return null;
        var cost=EntryCost(p,breakNaturalObstacles);
        if(cost==null||!AvoidMonsters)return cost;
        var danger=Game1.currentLocation.characters.OfType<StardewValley.Monsters.Monster>()
            .Select(m=>new GridPoint(m.TilePoint.X,m.TilePoint.Y)).Where(t=>t.Distance(p)<=3).Sum(t=>Math.Max(0,4-t.Distance(p)));
        return cost.Value+danger*8;
    }
    private GridPoint? BreakApproach(GridPoint target)
    {
        IReadOnlyList<GridPoint>? best=null;
        foreach(var p in target.Neighbors().Where(Passable))
        {
            var path=Pathfinding.AStar(Current,p,Cost);
            if(path!=null&&(best==null||path.Count<best.Count))best=path;
        }
        return best==null?null:best[^1];
    }
    private string FollowPath(IReadOnlyList<GridPoint> path,AiInputSimulator input)
    {
        while(_index<path.Count)
        {
            var target=path[_index];var pixel=Game1.player.StandingPixel;
            var dx=target.X*64+32-pixel.X;var dy=target.Y*64+32-pixel.Y;
            // A wider dead band avoids alternating directions when the farmer
            // crosses the exact tile center between update ticks.
            if(Math.Abs(dx)<=16&&Math.Abs(dy)<=16){_index++;continue;}
            input.Direction=Math.Abs(dx)>16?(dx>0?"right":"left"):(dy>0?"down":"up");
            return "moving";
        }
        return "reached";
    }
    private string Local(GridPoint goal, AiInputSimulator input,bool breakNaturalObstacles,bool entrance=false)
    {
        var now = Environment.TickCount64; var current = Game1.player.Position;
        if (_map != Game1.currentLocation.NameOrUniqueName || !_goal.Equals(goal))
        { _path = null; _map = Game1.currentLocation.NameOrUniqueName; _goal = goal; _replans = 0; _lastMove = now; _lastPosition = current; _transitionStarted = 0; }
        var standing=Game1.player.StandingPixel;
        if(_breakingTarget==null&&Math.Abs(goal.X*64+32-standing.X)<=16&&Math.Abs(goal.Y*64+32-standing.Y)<=16)
        {_lastMove=now;_lastPosition=current;_replans=0;return "reached";}
        if (Vector2.DistanceSquared(current, _lastPosition) > 64) { _lastMove = now; _lastPosition = current; }
        if (now - _lastMove > 2000)
        {
            if(entrance){Reason="entrance_approach_stalled";return "blocked";}
            if (++_replans > 3) { Reason = "blocked_after_3_replans"; return "blocked"; }
            _path = null; _lastMove = now;
        }
        if (_path == null)
        {
            _path = Pathfinding.AStar(Current, goal, p=>Cost(p,breakNaturalObstacles))?.ToList(); _index = 0;
            if (_path == null) { Reason = "path_unreachable"; return "blocked"; }
        }
        if(_breakingTarget is {} blocked)
        {
            if(Passable(blocked)){
                _brokenObstacles.Add(new{location=Game1.currentLocation.NameOrUniqueName,x=blocked.X,y=blocked.Y,type=_breakingType,itemId=_breakingItemId,tool=_breakingTool,verifiedRemoved=true});
                _breakingTarget=null;_breakingApproach=null;_breakWaitUntil=0;_breakingType=null;_breakingItemId=null;_breakingTool=null;_path=null;_index=0;return "moving";
            }
            if(!breakNaturalObstacles||BreakTool(blocked) is not {} tool){Reason="obstacle_no_longer_breakable";return "blocked";}
            if(_breakingApproach is not {} approach){Reason="breakable_obstacle_no_approach";return "blocked";}
            if(_path==null||!_path.LastOrDefault().Equals(approach)){
                _path=Pathfinding.AStar(Current,approach,Passable)?.ToList();_index=0;
                if(_path==null){Reason="breakable_obstacle_approach_unreachable";return "blocked";}
            }
            if(FollowPath(_path,input)!="reached")return "moving";
            if(now<_breakWaitUntil)return "moving";
            if(tool is not MeleeWeapon&&Game1.player.Stamina<2){input.Clear();Reason="insufficient_stamina:required_minimum=2";return "blocked";}
            Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(tool);
            Game1.player.faceDirection(blocked.X>approach.X?1:blocked.X<approach.X?3:blocked.Y>approach.Y?2:0);
            input.Tool=true;_breakWaitUntil=now+850;_lastMove=now;
            return "moving";
        }
        if(breakNaturalObstacles&&_index<_path.Count&&!Passable(_path[_index])&&BreakTool(_path[_index])!=null)
        {
            _breakingTarget=_path[_index];_breakingApproach=BreakApproach(_breakingTarget.Value);
            var point=new Vector2(_breakingTarget.Value.X,_breakingTarget.Value.Y);
            if(Game1.currentLocation.objects.TryGetValue(point,out var obj)){_breakingType=obj.Name;_breakingItemId=obj.QualifiedItemId;}
            else if(Game1.currentLocation.terrainFeatures.TryGetValue(point,out var feature)){_breakingType=feature.GetType().Name;_breakingItemId=null;}
            _breakingTool=BreakTool(_breakingTarget.Value)?.GetType().Name;
            if(_breakingApproach==null){Reason="breakable_obstacle_no_approach";return "blocked";}
            _path=null;_index=0;return "moving";
        }
        return FollowPath(_path,input);
    }
}

