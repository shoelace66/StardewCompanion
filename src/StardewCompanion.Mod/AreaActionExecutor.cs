using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewCompanion.Mod;

// Native bounded area actions shared by ordinary gameplay routines.
internal sealed class AreaActionExecutor
{

    private readonly string _skill;
    private readonly JsonElement _args;
    private readonly Action<GridPoint> _interact;
    private readonly Navigation _nav=new();
    private readonly HashSet<GridPoint> _done=new();
    private readonly Dictionary<string,int> _initial=Inventory();
    private GridPoint? _target;
    private long _wait;
    private long _pickupDeadline;
    private int _hits;
    private int _seedBefore;
    private float _beforeHealth;
    private bool _disposed;
    private readonly HashSet<Debris> _oldDebris=Game1.currentLocation.debris.ToHashSet();
    internal int Completed {get;private set;}
    internal string Phase {get;private set;}="starting";
    internal string Reason {get;private set;}="";
    internal Dictionary<string,int> CollectedItems {get;}=new();
    internal object[] AffectedTiles=>_done.Select(p=>(object)new{x=p.X,y=p.Y}).ToArray();
    internal AreaActionExecutor(string skill,string arguments,Action<GridPoint> interact)
    {
        _skill=skill;using var doc=JsonDocument.Parse(arguments);_args=doc.RootElement.Clone();_interact=interact;
        if(!WorldView.Supported(Game1.getLocationFromName(Map))||Num("x")<0||Num("y")<0||Num("width") is <1 or >32||Num("height") is <1 or >32)throw new JsonException("Invalid area bounds");
        if(skill=="plant_area"&&string.IsNullOrWhiteSpace(SeedId))throw new JsonException("Missing seedId");
    }
    private int Num(string key,int fallback=0)=>_args.TryGetProperty(key,out var value)?value.GetInt32():fallback;
    private string Text(string key)=>_args.GetProperty(key).GetString()!;
    private string SeedId=>_args.TryGetProperty("seedId",out var id)?id.GetString()!:"(O)472";
    private string Map=>_args.TryGetProperty("location",out var location)?location.GetString()!:"Farm";
    private bool Trees=>_args.TryGetProperty("includeTrees",out var v)&&v.GetBoolean();
    private bool RemoveStumps=>!_args.TryGetProperty("removeStumps",out var v)||v.GetBoolean();
    private string End(string reason){Reason=reason;Capture();return Completed>0?"partial":"failed";}
    private static Dictionary<string,int> Inventory()=>Game1.player.Items.Where(i=>i!=null).GroupBy(i=>i.QualifiedItemId).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.Stack));
    private static int Count(string id)=>Game1.player.Items.Where(i=>i?.QualifiedItemId==id).Sum(i=>i.Stack);
    internal void Capture()
    {
        if(!StardewModdingAPI.Context.IsWorldReady)return;
        foreach(var (id,count) in Inventory())
        {var gain=Math.Max(0,count-_initial.GetValueOrDefault(id));if(gain>CollectedItems.GetValueOrDefault(id))CollectedItems[id]=gain;}
    }
    internal static bool Natural(StardewValley.Object obj)=>obj is not Chest && obj.Name is "Stone" or "Twig" or "Weeds";
    private static HoeDirt? Soil(GridPoint p)=>Game1.currentLocation.terrainFeatures.TryGetValue(new(p.X,p.Y),out var f)?f as HoeDirt:null;
    private bool Obstacle(GridPoint p)
    {
        var l=Game1.currentLocation;var tile=new Vector2(p.X,p.Y);
        var type=_args.TryGetProperty("targetType",out var filter)?filter.GetString():"all";
        if(type is not ("all" or "debris" or "twig" or "stone" or "weeds" or "grass" or "tree" or "stump"))throw new JsonException("Unknown clear targetType");
        if(type is "tree" or "stump")return l.terrainFeatures.TryGetValue(tile,out var tf)&&tf is Tree t&&!t.tapped.Value&&(type=="stump"?t.stump.Value:type=="tree"&&t.growthStage.Value>=5&&(!t.stump.Value||RemoveStumps));
        if(type is not ("all" or "debris")){
            if(type=="grass")return l.terrainFeatures.TryGetValue(tile,out var terrain)&&terrain is Grass;
            return l.objects.TryGetValue(tile,out var resource)&&resource.Name==(type=="twig"?"Twig":type=="stone"?"Stone":"Weeds");
        }
        return (l.objects.TryGetValue(tile,out var obj)&&Natural(obj)) ||
            (l.terrainFeatures.TryGetValue(tile,out var f)&&(f is Grass||(Trees||type=="all")&&type!="debris"&&f is Tree tree&&!tree.tapped.Value&&(!tree.stump.Value||RemoveStumps)));
    }
    private static bool Tilled(GridPoint p)=>Soil(p)!=null;
    private static bool CanTill(GridPoint p)
    {
        var l=Game1.currentLocation;var tile=new Vector2(p.X,p.Y);
        return l.doesTileHaveProperty(p.X,p.Y,"Diggable","Back")!=null&&!l.isWaterTile(p.X,p.Y)
            &&!l.objects.ContainsKey(tile)&&!l.terrainFeatures.ContainsKey(tile)&&Navigation.Passable(p);
    }
    private IEnumerable<GridPoint> Targets()
    {
        for(var x=Num("x");x<Num("x")+Num("width");x++)for(var y=Num("y");y<Num("y")+Num("height");y++)
        {
            var p=new GridPoint(x,y);if(_done.Contains(p))continue;
            if(_skill=="clear_area"&&Obstacle(p))yield return p;
            if(_skill=="till_area"&&CanTill(p))yield return p;
            if(_skill=="plant_area"&&Soil(p) is {crop:null} soil&&soil.canPlantThisSeedHere(ItemRegistry.GetDataOrErrorItem(SeedId).ItemId))yield return p;
        }
    }
    internal string Tick(AiInputSimulator input)
    {
        if(_disposed)return End("area_action_cancelled");
        Capture();
        if(Game1.activeClickableMenu!=null)return End("unexpected_menu:"+Game1.activeClickableMenu.GetType().Name);
        if(Game1.eventUp||!Game1.player.CanMove||Game1.player.UsingTool){_nav.Suspend();Phase="waiting_for_native_game";return "running";}
        if(Game1.currentLocation.NameOrUniqueName!=Map){var travel=_nav.Move(Map,new GridPoint(Num("x"),Num("y")),input,_interact);Phase="travel_to_area";return travel is "blocked" or "unsupported"?End(_nav.Reason):"running";}
        var now=Environment.TickCount64;
        if(_target is {} target&&_wait!=0)
        {
            if(now<_wait)return "running";
            if(_skill=="clear_area"&&Game1.currentLocation.terrainFeatures.TryGetValue(new(target.X,target.Y),out var fallingFeature)&&fallingFeature is Tree fallingTree&&fallingTree.falling.Value)
            {Phase="waiting_for_native_tree_fall";return "running";}
            if(_skill=="clear_area"&&Obstacle(target))
            {
                if(++_hits>40)return End("target_did_not_clear");
                var health=TreeHealth(target);
                if(_hits>3&&health==_beforeHealth&&health>0)return End("tool_not_effective");
                _beforeHealth=health;_wait=0;
            }
            else
            {
                var verified=_skill=="clear_area"?!Obstacle(target):_skill=="till_area"?Tilled(target):Soil(target)?.crop!=null&&Count(SeedId)==_seedBefore-1;
                if(!verified)return End("action_not_verified");
                if(_skill=="clear_area")
                {
                    var pickup=CollectDrops(input,target);if(pickup!="done")return pickup;
                }
                Completed++;_done.Add(target);_target=null;_wait=0;_hits=0;_nav.Reset();
            }
        }
        if(Completed>=Num("maxTargets",1024)){Reason="target_limit_reached";return "completed";}
        if(_target==null)
        {
            var candidates=Targets().OrderBy(p=>p.Distance(Navigation.Current)).ToArray();
            if(candidates.Length==0){
                var cells=Enumerable.Range(Num("x"),Num("width")).SelectMany(x=>Enumerable.Range(Num("y"),Num("height")).Select(y=>new GridPoint(x,y))).ToArray();
                if(_skill=="till_area"&&cells.Any(p=>Soil(p)==null)){
                    Reason="tilling_precondition_blocked:区域包含障碍或不可耕地，请先清障或缩小范围";return Completed>0?"partial":"failed";
                }
                if(_skill=="plant_area"){
                    if(cells.Any(p=>Soil(p)==null)){Reason="requires_tilled_empty_soil:先clear_area清障，再till_area锄地；plant_area只播种";return Completed>0?"partial":"failed";}
                    if(cells.Any(p=>Soil(p) is {crop:null}))return End("seed_or_season_not_valid_for_remaining_soil");
                }
                Reason=Completed==0?"already_satisfied_no_action":"observed_targets_finished";return "completed";
            }
            _target=candidates.FirstOrDefault(p=>_nav.BestAdjacent(p)!=null);
            if(!_target.HasValue||!candidates.Contains(_target.Value)||_nav.BestAdjacent(_target.Value)==null)return End("target_unreachable");
            _beforeHealth=TreeHealth(_target.Value);_pickupDeadline=0;
        }
        var tile=_target.Value;var adjacent=_nav.BestAdjacent(tile);if(adjacent==null)return End("target_unreachable");
        var move=_nav.Move(Map,adjacent.Value,input,_interact);Phase="approach_target";
        if(move is "blocked" or "unsupported")return End(_nav.Reason);
        if(move!="reached")return "running";
        Face(tile);
        if(_skill=="plant_area")
        {
            var seeds=Game1.player.Items.FirstOrDefault(i=>i?.QualifiedItemId==SeedId) as StardewValley.Object;
            if(seeds==null)return End("seed_item_missing");
            Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(seeds);_seedBefore=Count(SeedId);
            if(!Utility.tryToPlaceItem(Game1.currentLocation,seeds,tile.X*64,tile.Y*64))return End("native_seed_placement_failed");
        }
        else
        {
            Tool? tool=_skill=="till_area"?Game1.player.Items.OfType<Hoe>().FirstOrDefault():ClearTool(tile);
            if(tool==null)return End("required_tool_missing");
            if(tool is MeleeWeapon&&HasScytheHarvestRisk(tile,tool))return End("harvestable_crop_protected_from_scythe_area");
            Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(tool);input.Tool=true;
        }
        _wait=now+700;Phase="verify_action";return "running";
    }
    internal static bool HasScytheHarvestRisk(GridPoint tile,Tool tool)=>Game1.currentLocation.terrainFeatures.Pairs.Any(p=>
        Math.Abs(p.Key.X-tile.X)<=3&&Math.Abs(p.Key.Y-tile.Y)<=3&&p.Value is HoeDirt {crop:not null} soil&&WorldView.Ready(soil.crop)
        &&(soil.crop.GetHarvestMethod()==StardewValley.GameData.Crops.HarvestMethod.Scythe||tool.ItemId=="66"));
    private static float TreeHealth(GridPoint p)=>Game1.currentLocation.terrainFeatures.TryGetValue(new(p.X,p.Y),out var f)&&f is Tree t?t.health.Value:0;
    private Tool? ClearTool(GridPoint p)
    {
        if(Game1.currentLocation.objects.TryGetValue(new(p.X,p.Y),out var obj)&&obj.Name=="Stone")return Game1.player.Items.OfType<Pickaxe>().FirstOrDefault();
        if(Game1.currentLocation.terrainFeatures.TryGetValue(new(p.X,p.Y),out var f)&&f is Grass)return Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(t=>t.isScythe());
        return Game1.player.Items.OfType<Axe>().FirstOrDefault();
    }
    private string CollectDrops(AiInputSimulator input,GridPoint target)
    {
        var drops=Game1.currentLocation.debris.Where(d=>!_oldDebris.Contains(d)&&(d.item!=null||!string.IsNullOrEmpty(d.itemId.Value))
            &&d.Chunks.Any(c=>Vector2.DistanceSquared(c.position.Value,new Vector2(target.X*64,target.Y*64))<320*320)).ToArray();
        if(drops.Length==0)return "done";
        if(_pickupDeadline==0){_pickupDeadline=Environment.TickCount64+20000;_nav.Reset();}
        if(Environment.TickCount64>_pickupDeadline)return End("drop_pickup_timeout");
        var chunk=drops.SelectMany(d=>d.Chunks).OrderBy(c=>Vector2.DistanceSquared(c.position.Value,Game1.player.Position)).FirstOrDefault();
        if(chunk==null)return "done";
        var drop=new GridPoint((int)(chunk.position.X+32)/64,(int)(chunk.position.Y+32)/64);
        // Standing on a neighboring tile can still be outside the native magnetic radius.
        // Prefer centering on the actual item tile, falling back only for blocked tiles.
        GridPoint? goal=Navigation.Passable(drop)?drop:_nav.BestAdjacent(drop);
        if(goal==null)return End("drop_unreachable");
        var state=_nav.Move(Map,goal.Value,input,_interact);if(state is "blocked" or "unsupported")return End(_nav.Reason);
        Phase="collect_native_drops";return "running";
    }
    private static void Face(GridPoint target)
    {var p=Navigation.Current;Game1.player.faceDirection(target.X>p.X?1:target.X<p.X?3:target.Y>p.Y?2:0);}
    internal bool Cleanup(){_disposed=true;return true;}
}
