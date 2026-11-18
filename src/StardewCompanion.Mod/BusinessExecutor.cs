using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewCompanion.Protocol;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Objects;

namespace StardewCompanion.Mod;

internal sealed record WorkUnit(string Skill,object Arguments,bool Optional=false);
internal sealed partial class BusinessExecutor
{
    private readonly string _skill;
    private readonly JsonElement _args;
    private readonly ExecutionTargets.ResolutionFailure? _targetFailure;
    private readonly string? _constructionTargetFailure;
    private readonly Action<GridPoint> _interact;
    private readonly Farm? _buildingTargetFarm;
    private readonly StardewValley.Buildings.Building? _buildingTargetAtStart;
    private IEnumerator<WorkUnit>? _workflow;
    private WorkUnit? _unit;
    private RoutineExecutor? _child;
    private NativeWorkExecutor? _native;
    private string? _finishedStatus;
    private int _childCount;
    private readonly Dictionary<string,int> _initial=Inventory();
    internal readonly List<object> Stages=new();
    internal readonly List<object> Skipped=new();
    private int _completed;
    private int _planted;
    private int _delivered;
    private bool _countStorageDelivery;
    internal object Outcome=>new{requested=_skill=="organize_storage"?_args.GetProperty("rules").EnumerateArray().Sum(r=>r.GetProperty("count").GetInt32()):Num("plantCount",Num("width")*Num("height")),planted=_planted,delivered=_delivered,completedStageUnits=_completed};
    internal int Completed=>_skill is "cultivate_plot" or "harvest_and_replant"?_planted+(_finishedStatus==null&&_unit?.Skill=="plant_area"?(_child?.Completed??0):0):_skill=="organize_storage"?_delivered:_completed+(_finishedStatus==null?(_native?.Completed??_child?.Completed??0):0);
    private bool HasProgress=>Completed>0||_completed>0;
    internal object? ActiveStage=>_unit!=null&&_finishedStatus==null
        ?new{skill=_unit.Skill,status="running",completed=_native?.Completed??_child?.Completed??0,details=_native?.Details??_child?.BusinessDetails}:null;
    internal string Reason {get;private set;}="";
    internal string Phase {get;private set;}="prepare";
    internal Dictionary<string,int> CollectedItems {get;}=new();
    internal object[] AffectedTiles=>_tiles.ToArray();
    private readonly List<object> _tiles=new();
    private readonly List<object> _remainingDrops=new();
    internal object[] RemainingDrops()=>_remainingDrops.Concat(_finishedStatus==null?(_child?.RemainingDrops()??Array.Empty<object>()):Array.Empty<object>()).ToArray();
    internal BusinessExecutor(string skill,string args,Action<GridPoint> interact)
    {
        _skill=skill;_args=ExecutionTargets.NormalizeBusiness(skill,args,out _targetFailure);_interact=interact;
        if(skill is "order_building" or "modify_building")_buildingTargetFarm=Game1.getFarm();
        if(skill=="modify_building"&&_buildingTargetFarm!=null&&_args.TryGetProperty("buildingId",out var requestedBuilding)
            &&Guid.TryParse(requestedBuilding.GetString(),out var requestedBuildingId))
            _buildingTargetAtStart=_buildingTargetFarm.buildings.FirstOrDefault(b=>b.id.Value==requestedBuildingId);
        ExecutionTargets.ValidateBounds(_args,skill is "order_building" or "modify_building"?"Farm":null);
        if(skill=="modify_building"&&_args.TryGetProperty("operation",out var operation)
            &&operation.ValueKind==JsonValueKind.String&&operation.GetString() is "move" or "upgrade" or "demolish"){
            var requested=_args.TryGetProperty("buildingId",out var id)?id.GetString():null;
            _constructionTargetFailure=Guid.TryParse(requested,out var buildingId)
                &&_buildingTargetFarm!.buildings.Any(b=>b.id.Value==buildingId)?null:"building_id_not_found";
        }
    }
    private string Text(string k,string fallback="")=>_args.TryGetProperty(k,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetString()!:fallback;
    private int Num(string k,int fallback=0)=>_args.TryGetProperty(k,out var v)?v.GetInt32():fallback;
    private bool Flag(string k)=>_args.TryGetProperty(k,out var v)&&v.GetBoolean();
    private string Map=>Text("location",Game1.currentLocation.NameOrUniqueName);
    private Dictionary<string,object?> Region()=>new(){["location"]=Map,["x"]=Num("x"),["y"]=Num("y"),["width"]=Num("width"),["height"]=Num("height")};
    private IEnumerable<GridPoint> Cells()=>Enumerable.Range(Num("x"),Num("width")).SelectMany(x=>Enumerable.Range(Num("y"),Num("height")).Select(y=>new GridPoint(x,y)));
    internal static Dictionary<string,int> Inventory()=>Game1.player.Items.Where(i=>i!=null).GroupBy(i=>i.QualifiedItemId).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.Stack));
    internal static int Count(string id)=>Game1.player.Items.Where(i=>i?.QualifiedItemId==id).Sum(i=>i.Stack);
    internal bool HandlesStoryDialogue=>_native?.HandlesStoryDialogue==true||_child?.HandlesStoryDialogue==true;
    internal void SuspendForStoryPrompt(){_native?.SuspendForStoryPrompt();_child?.SuspendForStoryPrompt();}
    internal string Tick(AiInputSimulator input)
    {
        Capture();
        if(_targetFailure!=null){Reason=_targetFailure.Reason;Stages.Add(new{status="failed",reason=Reason,details=_targetFailure});return "failed";}
        if(_constructionTargetFailure!=null){Reason=_constructionTargetFailure;Stages.Add(new{skill=_skill,status="failed",reason=Reason,completed=0});return "failed";}
        _workflow??=Build().GetEnumerator();
        if(_unit==null){
            bool hasNext;
            try{hasNext=_workflow.MoveNext();}
            catch(Exception ex)when(ex is InvalidOperationException or JsonException or ArgumentException){
                Reason=ex.Message;var denied=Reason.StartsWith("chest_not_authorized:",StringComparison.Ordinal)||Reason.StartsWith("authorized_chest_ambiguous:",StringComparison.Ordinal)||Reason.StartsWith("protected_or_unlisted_item:",StringComparison.Ordinal);
                Stages.Add(new{skill=_skill,status=denied?"rejected":"failed",reason=Reason,completed=Completed});
                return HasProgress?"partial":denied?"rejected":"failed";
            }
            if(!hasNext){
                Reason=Skipped.Count>0?"business_incomplete_with_skips":"business_completed";
                return Skipped.Count==0?"completed":HasProgress?"partial":"failed";
            }
            _unit=_workflow.Current;_childCount=0;
            if(_unit.Skill.StartsWith("$"))_native=new(_unit.Skill[1..],JsonSerializer.Serialize(_unit.Arguments),_interact,_buildingTargetFarm,_buildingTargetAtStart);
            else _child=new(_unit.Skill,JsonSerializer.Serialize(_unit.Arguments),_interact);
        }
        var status=_finishedStatus??(_native?.Tick(input)??_child!.Tick(input));
        Phase=_unit.Skill+":"+(_native?.Phase??_child!.Phase);
        if(status=="running")return status;
        if(_finishedStatus==null){
        _childCount=_native?.Completed??_child!.Completed;
        var recordedReason=_native?.Reason??_child!.Reason;
        Stages.Add(new{skill=_unit.Skill,requestedTarget=_unit.Arguments,status,reason=recordedReason,completed=_childCount,details=_native?.Details??_child?.BusinessDetails});
        if(_child!=null){_tiles.AddRange(_child.AffectedTiles);_remainingDrops.AddRange(_child.RemainingDrops());}
        _completed+=_childCount;
        if(_unit.Skill=="plant_area")_planted+=_childCount;
        if(_countStorageDelivery){_delivered+=_childCount;_countStorageDelivery=false;}
        _finishedStatus=status;
        }
        var reason=_native?.Reason??_child!.Reason;
        var optional=_unit.Optional;
        if(_native?.Cleanup()==false||_child?.Cleanup()==false){Phase="native_menu_cleanup_pending";return "running";}
        _child=null;_native=null;var done=_unit;_unit=null;_finishedStatus=null;
        if(status!="completed"){
            if(optional){
                var alternativeSource=done.Skill=="$approach"&&(!_args.TryGetProperty("sourceChests",out var sources)||sources.GetArrayLength()==0);
                if(!alternativeSource)Skipped.Add(new{skill=done.Skill,reason});
                return "running";
            }
            Reason=reason;return HasProgress?"partial":"failed";
        }
        return "running";
    }
    internal void Capture(){foreach(var (id,count) in Inventory()){var gain=Math.Max(0,count-_initial.GetValueOrDefault(id));if(gain>CollectedItems.GetValueOrDefault(id))CollectedItems[id]=gain;}}
    internal void MarkDayCompleted(){
        if(_unit?.Skill=="sleep"&&_finishedStatus==null){
            Stages.Add(new{skill="sleep",status="completed",reason="new_day_observed",completed=1});
            _completed++;_finishedStatus="completed";
        }
    }
    internal bool Cleanup(){Capture();return (_child?.Cleanup()??true)&&(_native?.Cleanup()??true);}
    private WorkUnit Native(string action,object args,bool optional=false)=>new("$"+action,args,optional);
    private IEnumerable<WorkUnit> Build()
    {
        switch(_skill){
            case "cultivate_plot":case "harvest_and_replant":case "maintain_plot":
                foreach(var u in Cultivate())yield return u;break;
            case "gather_resource":
                foreach(var u in Gather())yield return u;break;
            case "travel":
                if(Text("method")=="walk")yield return new("go_to",new{location=Map,x=Num("x"),y=Num("y")});
                else yield return Native("travel",_args);break;
            case "forage_route":
                foreach(var stop in _args.GetProperty("stops").EnumerateArray()){
                    yield return new("forage_area",stop,true);
                    var destination=stop.GetProperty("location").GetString();
                    var arrived=Game1.currentLocation.NameOrUniqueName==destination;
                    Stages.Add(new{skill="forage_route_stop",requestedLocation=destination,actualLocation=Game1.currentLocation.NameOrUniqueName,arrived,forageCompleted=_childCount});
                    if(!arrived){
                        Stages.Add(new{skill="forage_extras",status="not_executed",reason="stop_location_not_reached",requestedLocation=destination,completed=0});
                        continue;
                    }
                    if(stop.GetProperty("shake").GetBoolean()||stop.GetProperty("digSpots").GetBoolean())yield return Native("forage_extras",stop,true);
                }break;
            case "craft_items":case "cook_recipes":case "craft_and_place":case "install_irrigation":
                foreach(var u in Produce())yield return u;break;
            case "plant_trees":
                foreach(var tile in _args.GetProperty("tiles").EnumerateArray())yield return Native("plant_tree",new{location=Map,itemId=Text("itemId"),x=tile.GetProperty("x").GetInt32(),y=tile.GetProperty("y").GetInt32()},true);
                break;
            case "collect_outputs":case "service_machines":
                foreach(var unit in Machines())yield return unit;
                break;
            case "organize_storage":case "prepare_loadout":
                foreach(var u in Storage())yield return u;break;
            case "recover":
                if(Text("method")=="sleep")yield return new("sleep",new{});
                else yield return Native("recover",_args);break;
            case "end_day":
                if(_args.GetProperty("storageRules").GetArrayLength()>0)yield return new("organize_storage",new{rules=_args.GetProperty("storageRules"),_bindings=_args.GetProperty("_bindings")});
                if(_args.GetProperty("shippingItems").GetArrayLength()>0)yield return new("sell_items",new{channel="shipping",location="Farm",shopId=(string?)null,items=_args.GetProperty("shippingItems")});
                if(Flag("sleep"))yield return new("sleep",new{});break;
            default:
                foreach(var u in Domain())yield return u;break;
        }
    }
    private IEnumerable<WorkUnit> Cultivate()
    {
        if(Game1.currentLocation.NameOrUniqueName!=Map)yield return new("go_to",new{location=Map,x=Num("x"),y=Num("y")});
        var region=Region();var partial=Flag("allowPartial");
        if(_skill=="harvest_and_replant"||Flag("harvest"))yield return new("harvest_area",region,true);
        if(Flag("removeDead"))yield return Native("remove_dead",region,true);
        var seed=Text("seedId",Text("replantSeedId"));var desired=Num("plantCount",Num("width")*Num("height"));var needed=0;
        if(seed!=""){
            var seedItem=ItemRegistry.Create(seed) as StardewValley.Object;
            if(seedItem==null||seedItem.Category!=StardewValley.Object.SeedsCategory||seedItem.IsWildTreeSapling()||seedItem.IsFruitTreeSapling())throw new InvalidOperationException("unsupported_crop_seed:"+seed);
            if(Game1.cropData.TryGetValue(ItemRegistry.GetDataOrErrorItem(seed).ItemId,out var cropData)&&!Game1.currentLocation.SeedsIgnoreSeasonsHere()&&!cropData.Seasons.Contains(Game1.GetSeasonForLocation(Game1.currentLocation)))throw new InvalidOperationException("seed_not_in_current_season:"+seed);
            var requestedSeed=ItemRegistry.GetDataOrErrorItem(seed).ItemId;
            var occupied=Cells().Count(p=>Game1.currentLocation.terrainFeatures.TryGetValue(new(p.X,p.Y),out var f)&&f is HoeDirt {crop:not null} soil&&!soil.crop.dead.Value&&soil.crop.netSeedIndex.Value==requestedSeed);
            needed=Math.Max(0,desired-occupied);
            if(Count(seed)<needed&&!partial){
                var available=Count(seed);
                Stages.Add(new{skill="plant_area",status="failed",reason="seed_missing",seedId=seed,requested=needed,available,remaining=needed-available,completed=0});
                if(Text("clearPolicy","none")!="none")Stages.Add(new{skill="clear_area",status="not_executed",reason="planting_precondition_failed",completed=0});
                Stages.Add(new{skill="till_area",status="not_executed",reason="planting_precondition_failed",completed=0});
                if(Text("fertilizerId")!="")Stages.Add(new{skill="fertilize",status="not_executed",reason="planting_precondition_failed",completed=0});
                if(Text("waterPolicy")=="water_now")Stages.Add(new{skill="water_area",status="not_executed",reason="planting_precondition_failed",completed=0});
                throw new InvalidOperationException($"seed_missing:required={needed};available={available}");
            }
        }
        var clear=Text("clearPolicy","none");
        if(clear!="none"){
            var a=Region();a["targetType"]=clear=="debris_and_wild_trees"?"all":"debris";a["removeStumps"]=true;
            yield return new("clear_area",a,partial);
        }
        if(seed!=""&&needed>0){
            var available=Count(seed);
            if(available<needed)Skipped.Add(new{skill="plant_area",reason="seed_item_missing",requested=needed,available,remaining=needed-available});
            if(available==0)yield break;
            yield return new("till_area",region,partial);
            if(Text("fertilizerId")!="")yield return Native("fertilize",new{region, itemId=Text("fertilizerId")},partial);
            var a=Region();a["seedId"]=seed;a["maxTargets"]=Math.Min(needed,Count(seed));yield return new("plant_area",a,partial);
            Stages.Add(new{skill="plant_summary",requested=needed,actual=_childCount,remaining=Math.Max(0,needed-_childCount),seedId=seed});
            if(_childCount<needed&&available>=needed)Skipped.Add(new{skill="plant_area",reason="plant_target_shortfall",requested=needed,actual=_childCount,remaining=needed-_childCount});
        }
        if(Text("waterPolicy")=="water_now")yield return new("water_area",region,partial);
        else Stages.Add(new{skill="water_area",status="skipped_by_policy",reason="skip_today"});
    }
    private IEnumerable<WorkUnit> Gather()
    {
        if(Game1.currentLocation.NameOrUniqueName!=Map)yield return new("go_to",new{location=Map,x=Num("x"),y=Num("y")});
        var item=Text("itemId");var before=Count(item);var sources=_args.GetProperty("sources").EnumerateArray().Select(s=>s.GetString()!).ToHashSet();
        if(sources.Contains("grass"))Skipped.Add(new{reason="grass_inventory_yield_not_guaranteed",itemId=item,recovery="Grass produces hay only under native silo/scythe rules; it is not a guaranteed inventory fibre source"});
        var attempted=new HashSet<GridPoint>();
        while(Count(item)-before<Num("count")){
            var l=Game1.currentLocation;
            var targets=Cells().Where(p=>!attempted.Contains(p)).Select(p=>{
                l.objects.TryGetValue(new(p.X,p.Y),out var obj);l.terrainFeatures.TryGetValue(new(p.X,p.Y),out var f);
                var type=obj?.Name=="Twig"&&sources.Contains("twigs")?"twig":obj?.Name=="Stone"&&sources.Contains("stones")?"stone":
                    obj?.Name=="Weeds"&&sources.Contains("weeds")?"weeds":f is Grass?null:
                    f is Tree tree&&!tree.tapped.Value&&tree.growthStage.Value>=5&&(tree.stump.Value?sources.Contains("small_stumps"):sources.Contains("wild_trees"))?(tree.stump.Value?"stump":"tree"):null;
                var yield=type=="tree"?12:1;
                return new{p,type,score=(p.Distance(Navigation.Current)+1)/(float)yield};
            }).Where(t=>t.type!=null&&(t.type is "twig" or "tree" or "stump"?item=="(O)388":t.type=="stone"?item=="(O)390":t.type=="weeds"&&item=="(O)771")).OrderBy(t=>t.score).ToArray();
            var target=targets.FirstOrDefault(t=>new Navigation().BestAdjacent(t.p)!=null);
            if(target==null)break;
            attempted.Add(target.p);
            yield return new("clear_area",new{location=Map,x=target.p.X,y=target.p.Y,width=1,height=1,targetType=target.type,
                removeStumps=Flag("removeStumps"),maxTargets=1},true);
        }
        var gained=Count(item)-before;
        if(gained<Num("count")){Skipped.Add(new{reason="resource_target_shortfall",itemId=item,requested=Num("count"),actual=gained});}
    }
    private IEnumerable<WorkUnit> Produce()
    {
        var recipeName=Text("recipe");var cooking=_skill=="cook_recipes";
        if(cooking&&Utility.getHomeOfFarmer(Game1.player).upgradeLevel<1)throw new InvalidOperationException("accessible_kitchen_required");
        if(_skill=="install_irrigation"&&(ItemRegistry.Create(Text("itemId")) is not StardewValley.Object requestedSprinkler||!requestedSprinkler.IsSprinkler()))throw new InvalidOperationException("irrigation_item_must_be_sprinkler");
        var craftCount=Num("count",_args.TryGetProperty("tiles",out var requestedTiles)?requestedTiles.GetArrayLength():_args.TryGetProperty("placements",out var requestedPlacements)?requestedPlacements.GetArrayLength():1);
        if(_skill is "craft_and_place" or "install_irrigation"){
            var destination=Game1.getLocationFromName(Map)??throw new InvalidOperationException("placement_location_unknown:"+Map);
            var tiles=_args.TryGetProperty("tiles",out var preflightTiles)?preflightTiles:_args.GetProperty("placements");
            var missing=0;
            var seen=new HashSet<Vector2>();
            foreach(var target in tiles.EnumerateArray()){
                var point=new Vector2(target.GetProperty("x").GetInt32(),target.GetProperty("y").GetInt32());
                if(!seen.Add(point))throw new InvalidOperationException($"duplicate_placement_target:{Map}:{point.X},{point.Y}");
                if(destination.objects.TryGetValue(point,out var existing)){
                    if(existing.QualifiedItemId!=Text("itemId"))throw new InvalidOperationException($"target_tile_occupied:{Map}:{point.X},{point.Y}");
                    if(target.TryGetProperty("attachmentId",out var attachment)&&attachment.ValueKind==JsonValueKind.String
                        &&existing.heldObject.Value is {} installed&&installed.QualifiedItemId!=attachment.GetString())
                        throw new InvalidOperationException($"sprinkler_attachment_already_occupied:{Map}:{point.X},{point.Y}");
                }else missing++;
            }
            craftCount=missing;
        }
        if(recipeName!=""&&craftCount>0){
            var known=cooking?Game1.player.cookingRecipes:Game1.player.craftingRecipes;
            if(!known.ContainsKey(recipeName))throw new InvalidOperationException("recipe_not_unlocked:"+recipeName);
            var recipe=new CraftingRecipe(recipeName,cooking);
            if(_skill is "craft_and_place" or "install_irrigation"&&recipe.GetItemData(useFirst:true).QualifiedItemId!=Text("itemId"))throw new InvalidOperationException("recipe_output_item_mismatch");
            if(_skill is "craft_and_place" or "install_irrigation")craftCount=(craftCount+recipe.numberProducedPerCraft-1)/recipe.numberProducedPerCraft;
            foreach(var unit in IngredientSources(recipe.recipeList,craftCount,cooking))yield return unit;
        }
        if(cooking)yield return Native("prepare_recipe",new{recipe=recipeName,count=craftCount,cooking,ingredientQuality=Num("ingredientQuality")});
        if(cooking){var home=Utility.getHomeOfFarmer(Game1.player);var entry=home.getEntryLocation();if(Game1.currentLocation!=home)yield return new("go_to",new{location=home.NameOrUniqueName,x=entry.X,y=entry.Y});}
        var sources=_args.TryGetProperty("sourceChests",out var chests)?chests:JsonSerializer.SerializeToElement(Array.Empty<string>());
        yield return Native("prepare_recipe",new{recipe=recipeName,count=craftCount,sourceChests=sources,cooking,ingredientQuality=Num("ingredientQuality")});
        if(_skill is "craft_items" or "cook_recipes")yield return Native("craft",new{recipe=recipeName,count=craftCount,cooking,ingredientQuality=Num("ingredientQuality")});
        else{
            var placements=_args.TryGetProperty("tiles",out var t)?t:_args.GetProperty("placements");
            if(_skill=="install_irrigation"){
                var destination=Game1.getLocationFromName(Map)??throw new InvalidOperationException("irrigation_location_unknown");
                var needed=new Dictionary<string,int>();
                foreach(var p in placements.EnumerateArray()){
                    destination.objects.TryGetValue(new(p.GetProperty("x").GetInt32(),p.GetProperty("y").GetInt32()),out var existing);
                    if(recipeName==""&&existing?.QualifiedItemId!=Text("itemId"))needed[Text("itemId")]=needed.GetValueOrDefault(Text("itemId"))+1;
                    if(p.TryGetProperty("attachmentId",out var attachment)&&attachment.ValueKind==JsonValueKind.String&&existing?.heldObject.Value?.QualifiedItemId!=attachment.GetString()){
                        var id=attachment.GetString()!;needed[id]=needed.GetValueOrDefault(id)+1;
                    }
                }
                foreach(var unit in IngredientSources(needed,1,false,exactItems:true))yield return unit;
                var shortages=needed.Where(p=>Count(p.Key)<p.Value).Select(p=>new{itemId=p.Key,required=p.Value,available=Count(p.Key)}).ToArray();
                if(shortages.Length>0){Stages.Add(new{skill="placement_items",status="failed",shortages});throw new InvalidOperationException("irrigation_items_missing_after_authorized_sources");}
            }
            if(recipeName!=""&&craftCount>0)yield return Native("craft",new{recipe=recipeName,count=craftCount,cooking=false,ingredientQuality=0});
            foreach(var tile in placements.EnumerateArray())yield return Native("place",new{location=Map,itemId=Text("itemId"),target=tile},true);
            if(_skill=="install_irrigation"&&Text("waterPolicy")=="water_now"){
                var location=Game1.currentLocation;
                var covered=placements.EnumerateArray().SelectMany(p=>location.objects.TryGetValue(new(p.GetProperty("x").GetInt32(),p.GetProperty("y").GetInt32()),out var sprinkler)?sprinkler.GetSprinklerTiles():new List<Vector2>()).Distinct().ToArray();
                foreach(var tile in covered)yield return new("water_area",new{location=Map,x=(int)tile.X,y=(int)tile.Y,width=1,height=1,autoRefill=true});
            }
        }
    }
    private IEnumerable<WorkUnit> IngredientSources(IReadOnlyDictionary<string,int> requirements,int crafts,bool cooking,bool exactItems=false)
    {
        bool Matches(Item? item,string ingredient)=>item!=null&&(exactItems?item.QualifiedItemId==ingredient:CraftingRecipe.ItemMatchesForCrafting(item,ingredient));
        var explicitSources=_args.TryGetProperty("sourceChests",out var sources)&&sources.GetArrayLength()>0;
        var bindings=_args.TryGetProperty("_bindings",out var authorized)?authorized.EnumerateArray().ToArray():Array.Empty<JsonElement>();
        var names=explicitSources?sources.EnumerateArray().Select(s=>s.GetString()!).ToArray():bindings
            .Where(b=>ExecutionTargets.AllowedLocation(b.GetProperty("Location").GetString()!))
            .OrderBy(b=>b.GetProperty("Location").GetString()==Game1.currentLocation.NameOrUniqueName?0:1)
            .ThenBy(b=>b.GetProperty("Name").GetString(),StringComparer.Ordinal).Select(b=>b.GetProperty("Name").GetString()!).ToArray();
        Stages.Add(new{skill="ingredient_sources",status="selected",automatic=!explicitSources,sources=names,meaning="authorized source candidates; actual transfers recorded separately"});
        foreach(var name in names){
            if(requirements.All(r=>Game1.player.Items.Where(i=>Matches(i,r.Key)&&(!cooking||i.Quality==Num("ingredientQuality"))).Sum(i=>i.Stack)>=r.Value*crafts))yield break;
            var binding=bindings.FirstOrDefault(b=>b.GetProperty("Name").GetString()==name);
            if(binding.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("chest_not_authorized:"+name);
            if(!ExecutionTargets.AllowedLocation(binding.GetProperty("Location").GetString()!))throw new InvalidOperationException("location_disabled:"+binding.GetProperty("Location").GetString());
            var allowed=binding.GetProperty("AllowedIds").EnumerateArray().Select(v=>v.GetString()).Except(binding.GetProperty("ProtectedIds").EnumerateArray().Select(v=>v.GetString())).ToHashSet();
            if(!explicitSources&&!allowed.Any(id=>id!=null&&requirements.Any(r=>Matches(ItemRegistry.Create(id),r.Key)
                &&Game1.player.Items.Where(i=>Matches(i,r.Key)&&(!cooking||i.Quality==Num("ingredientQuality"))).Sum(i=>i.Stack)<r.Value*crafts)))continue;
            var sourceMap=binding.GetProperty("Location").GetString()!;var tile=new GridPoint(binding.GetProperty("X").GetInt32(),binding.GetProperty("Y").GetInt32());
            if(!explicitSources&&Game1.currentLocation.NameOrUniqueName!=sourceMap&&Navigation.Route(Game1.currentLocation.NameOrUniqueName,sourceMap)==null){
                Stages.Add(new{skill="ingredient_source",status="skipped",source=name,reason="no_supported_route"});continue;
            }
            yield return Native("approach",new{location=sourceMap,x=tile.X,y=tile.Y},optional:!explicitSources);
            if(!explicitSources&&(Game1.currentLocation.NameOrUniqueName!=sourceMap||Navigation.Current.Distance(tile)>1))continue;
            if(!Game1.currentLocation.objects.TryGetValue(new(tile.X,tile.Y),out var obj)||obj is not Chest chest||!obj.modData.TryGetValue(WorldView.ChestKey,out var chestId)||chestId!=binding.GetProperty("Id").GetString()){
                if(explicitSources)throw new InvalidOperationException("chest_binding_invalid:"+name);
                Stages.Add(new{skill="ingredient_source",status="skipped",source=name,reason="chest_binding_invalid"});continue;
            }
            foreach(var ingredient in requirements){
                var needed=ingredient.Value*crafts-Game1.player.Items.Where(i=>Matches(i,ingredient.Key)&&(!cooking||i.Quality==Num("ingredientQuality"))).Sum(i=>i.Stack);
                foreach(var item in chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(i=>i!=null&&!i.modData.ContainsKey("StardewCompanion/Protected")&&allowed.Contains(i.QualifiedItemId)&&Matches(i,ingredient.Key)&&(!cooking||i.Quality==Num("ingredientQuality"))).ToArray()){
                    if(needed<=0)break;var amount=Math.Min(needed,item.Stack);
                    yield return Transfer("withdraw_items",new{chest=name,itemId=item.QualifiedItemId,count=amount,quality=item.Quality});needed-=amount;
                }
            }
        }
    }
    private IEnumerable<WorkUnit> Storage()
    {
        if(_skill=="prepare_loadout"){
            // Resolve every permission before the first transfer, including
            // destinations that would otherwise be checked after a withdrawal.
            foreach(var entry in _args.GetProperty("deposit").EnumerateArray())_ = Transfer("deposit_items",entry);
            foreach(var entry in _args.GetProperty("withdraw").EnumerateArray())_ = Transfer("withdraw_items",entry);
            foreach(var entry in _args.GetProperty("deposit").EnumerateArray())yield return Transfer("deposit_items",entry);
            foreach(var entry in _args.GetProperty("withdraw").EnumerateArray())yield return Transfer("withdraw_items",entry);
        }else{
        foreach(var rule in _args.GetProperty("rules").EnumerateArray()){
            var id=rule.GetProperty("itemId").GetString()!;
            foreach(var side in new[]{"source","destination"}){
                var reference=rule.GetProperty(side).GetString();
                if(reference!="inventory")_ = Transfer(side=="source"?"withdraw_items":"deposit_items",new{chest=reference,itemId=id,count=rule.GetProperty("count").GetInt32()});
            }
        }
        foreach(var rule in _args.GetProperty("rules").EnumerateArray()){
            var source=rule.GetProperty("source").GetString();var destination=rule.GetProperty("destination").GetString();
            if(source!="inventory")source=AuthorizedBinding(source).GetProperty("Name").GetString();
            if(destination!="inventory")destination=AuthorizedBinding(destination).GetProperty("Name").GetString();
            var id=rule.GetProperty("itemId").GetString()!;var count=rule.GetProperty("count").GetInt32();var quality=rule.GetProperty("quality").GetInt32();
            if(source==destination){Stages.Add(new{skill="storage_rule",status="already_satisfied",source,destination});continue;}
            var beforeTransfer=Game1.player.Items.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
            if(source!="inventory"){
                var binding=AuthorizedBinding(source);
                var map=Game1.getLocationFromName(binding.GetProperty("Location").GetString()!);
                var tile=new Vector2(binding.GetProperty("X").GetInt32(),binding.GetProperty("Y").GetInt32());
                if(map==null||!map.objects.TryGetValue(tile,out var obj)||obj is not Chest chest||!obj.modData.TryGetValue(WorldView.ChestKey,out var chestId)||chestId!=binding.GetProperty("Id").GetString())throw new InvalidOperationException("chest_binding_invalid:"+source);
                var stock=chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack);
                count=Math.Min(count,Math.Max(0,stock-rule.GetProperty("retain").GetInt32()));
                if(count>0){_countStorageDelivery=destination=="inventory";yield return Transfer("withdraw_items",new{chest=source,itemId=id,count,quality});}
                count=Math.Min(count,Math.Max(0,Game1.player.Items.Where(i=>i?.QualifiedItemId==id&&i.Quality==quality).Sum(i=>i.Stack)-beforeTransfer));
            }else count=Math.Min(count,Math.Max(0,beforeTransfer-rule.GetProperty("retain").GetInt32()));
            if(count<rule.GetProperty("count").GetInt32())Skipped.Add(new{reason="storage_source_shortfall_or_retain_limit",source,destination,itemId=id,requested=rule.GetProperty("count").GetInt32(),actual=count,remaining=rule.GetProperty("count").GetInt32()-count});
            if(destination!="inventory"&&count>0){
                _countStorageDelivery=true;yield return Transfer("deposit_items",new{chest=destination,itemId=id,count,quality});
            }
        }
        }
    }
}
