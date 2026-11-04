using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewCompanion.Mod;

internal sealed record MapEdge(string From, string To, GridPoint Tile, bool Interact, GridPoint? ArrivalTile);

internal sealed class Navigation
{
    private List<GridPoint>? _path;
    private int _index;
    private string _map = "";
    private GridPoint _goal;
    private Vector2 _lastPosition;
    private long _lastMove;
    private int _replans;
    private long _transitionStarted;
    private string _observedMap = "";
    private string _previousTransitionFrom = "";
    private string _previousTransitionTo = "";
    private GridPoint? _breakingTarget;
    private GridPoint? _breakingApproach;
    private long _breakWaitUntil;
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
                if(WorldView.Supported(target))yield return new(l.NameOrUniqueName,target.NameOrUniqueName,new(door.Key.X,door.Key.Y),true,Arrival(target!,l.NameOrUniqueName));
            }
        }
    }
    private static GridPoint? Arrival(GameLocation target,string from)
    {
        var warp=target.warps.FirstOrDefault(w=>w.TargetName==from);
        return warp==null?null:new GridPoint(warp.X,warp.Y);
    }
    internal static IReadOnlyList<MapEdge>? Route(string from, string to)
    {
        var currentMap=Game1.currentLocation?.NameOrUniqueName??"";var current=Current;
        return Pathfinding.Dijkstra(from,to,Edges().ToArray(),e=>e.From,e=>e.To,
            e=>100+(e.From==currentMap?e.Tile.Distance(current):0));
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
    internal void Reset() { _path = null; _map = ""; _replans = 0; _transitionStarted = 0; _observedMap="";_previousTransitionFrom="";_previousTransitionTo="";_breakingTarget=null;_breakingApproach=null;_breakWaitUntil=0;Reason = ""; }
    internal void Suspend() { _lastMove = Environment.TickCount64; _lastPosition = Game1.player.Position; }
    // Returns reached / moving / blocked / unsupported. Only emits normal held direction input.
    internal string Move(string location, GridPoint goal, AiInputSimulator input, Action<GridPoint> interact, bool breakNaturalObstacles=false)
    {
        var here = Game1.currentLocation;
        if (!WorldView.Supported(here)) { Reason = "unsupported_current_map"; return "unsupported"; }
        var currentMap=here.NameOrUniqueName;
        if(_observedMap=="")_observedMap=currentMap;
        else if(currentMap!=_observedMap){
            var from=_observedMap;
            if(from==_previousTransitionTo&&currentMap==_previousTransitionFrom){
                _observedMap=currentMap;input.Clear();Reason=$"map_transition_bounced:{from}->{currentMap}";return "blocked";
            }
            _previousTransitionFrom=from;_previousTransitionTo=currentMap;_observedMap=currentMap;
        }
        if (here.NameOrUniqueName != location)
        {
            var route = Route(here.NameOrUniqueName, location);
            if (route == null || route.Count == 0) { Reason = "no_supported_route"; return "blocked"; }
            var edge = route[0];
            var approach = edge.Interact ? BestAdjacent(edge.Tile) : new GridPoint(
                Math.Clamp(edge.Tile.X, 0, here.Map.Layers[0].LayerWidth - 1),
                Math.Clamp(edge.Tile.Y, 0, here.Map.Layers[0].LayerHeight - 1));
            if (approach == null) { Reason = "exit_unreachable"; return "blocked"; }
            var state = Local(approach.Value, input, false);
            if (state != "reached") return state;
            if (_transitionStarted == 0) _transitionStarted = Environment.TickCount64;
            if (Environment.TickCount64 - _transitionStarted > 5000) { Reason = "exit_did_not_transition"; return "blocked"; }
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
        return Local(goal, input, breakNaturalObstacles);
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
    private string Local(GridPoint goal, AiInputSimulator input,bool breakNaturalObstacles)
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
            if(Passable(blocked)){_breakingTarget=null;_breakingApproach=null;_breakWaitUntil=0;_path=null;_index=0;return "moving";}
            if(!breakNaturalObstacles||BreakTool(blocked) is not {} tool){Reason="obstacle_no_longer_breakable";return "blocked";}
            if(_breakingApproach is not {} approach){Reason="breakable_obstacle_no_approach";return "blocked";}
            if(_path==null||!_path.LastOrDefault().Equals(approach)){
                _path=Pathfinding.AStar(Current,approach,Passable)?.ToList();_index=0;
                if(_path==null){Reason="breakable_obstacle_approach_unreachable";return "blocked";}
            }
            if(FollowPath(_path,input)!="reached")return "moving";
            if(now<_breakWaitUntil)return "moving";
            Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(tool);
            Game1.player.faceDirection(blocked.X>approach.X?1:blocked.X<approach.X?3:blocked.Y>approach.Y?2:0);
            input.Tool=true;_breakWaitUntil=now+850;_lastMove=now;
            return "moving";
        }
        if(breakNaturalObstacles&&_index<_path.Count&&!Passable(_path[_index])&&BreakTool(_path[_index])!=null)
        {
            _breakingTarget=_path[_index];_breakingApproach=BreakApproach(_breakingTarget.Value);
            if(_breakingApproach==null){Reason="breakable_obstacle_no_approach";return "blocked";}
            _path=null;_index=0;return "moving";
        }
        return FollowPath(_path,input);
    }
}

