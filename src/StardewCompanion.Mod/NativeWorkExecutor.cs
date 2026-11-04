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

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private readonly string _action;
    private readonly JsonElement _args;
    private readonly Action<GridPoint> _interact;
    private readonly Navigation _nav=new();
    private readonly HashSet<GridPoint> _done=new();
    private readonly List<object> _results=new();
    private long _wait;
    private long _started=Environment.TickCount64;
    private bool _shopInteractionAttempted;
    private int _before;
    private GridPoint? _target;
    private CraftingPage? _crafting;
    private int _crafts;
    private bool _disposed;
    internal int Completed {get;private set;}
    internal string Reason {get;private set;}="";
    internal string Phase {get;private set;}="prepare_native";
    internal object Details=>new{action=_action,results=_results};
    internal NativeWorkExecutor(string action,string args,Action<GridPoint> interact)
    {_action=action;using var json=JsonDocument.Parse(args);_args=json.RootElement.Clone();_interact=interact;}
    private string Text(string key,string fallback="")=>_args.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetString()!:fallback;
    private int Num(string key,int fallback=0)=>_args.TryGetProperty(key,out var v)?v.GetInt32():fallback;
    private bool Flag(string key)=>_args.TryGetProperty(key,out var v)&&v.GetBoolean();
    private string Map=>Text("location",Game1.currentLocation.NameOrUniqueName);
    private JsonElement Body=>_args.TryGetProperty("target",out var t)?t:_args.TryGetProperty("region",out var r)?r:_args;
    private GridPoint Tile=>new(Body.GetProperty("x").GetInt32(),Body.GetProperty("y").GetInt32());
    private static int Count(string id)=>BusinessExecutor.Count(id);
    private static Item? Item(string id,int? quality=null)=>Game1.player.Items.FirstOrDefault(i=>i?.QualifiedItemId==id&&(!quality.HasValue||i.Quality==quality));
    private string End(string reason){Reason=reason;return Completed>0?"partial":"failed";}
    private string Finish(string reason){Reason=reason;return "completed";}
    internal string Tick(AiInputSimulator input)
    {
        if(_disposed)return End("native_operation_cancelled");
        if(_action!="recover"&&Environment.TickCount64-_started>120000)return End("native_action_timeout:"+_action);
        if(_action=="approach"){var state=Near(Map,Tile,input);return state=="reached"?Finish("target_approached"):state is "blocked" or "unsupported"?End("target_unreachable"):"running";}
        if(_action=="craft")return Craft();
        if(_action=="prepare_recipe")return PrepareRecipe();
        if(_action=="recover")return Recover(input);
        if(Game1.activeClickableMenu==null&&_action!="attend_event"&&(Game1.eventUp||Game1.player.UsingTool||!Game1.player.CanMove)){_nav.Suspend();Phase="waiting_for_native_animation";return "running";}
        if(_action is "place" or "plant_tree" or "fertilize" or "remove_dead" or "machine" or "water_coverage" or "forage_extras")
            return Farming(input);
        return Domain(input);
    }
    private string Near(string map,GridPoint tile,AiInputSimulator input,bool adjacent=true)
    {
        if(Game1.currentLocation.NameOrUniqueName!=map)return _nav.Move(map,tile,input,_interact);
        var goal=adjacent?_nav.BestAdjacent(tile):tile;
        if(goal==null)return "blocked";
        var status=_nav.Move(map,goal.Value,input,_interact);Phase="approach_native_target";return status;
    }
    private string PrepareRecipe()
    {
        var recipe=Text("recipe");if(recipe=="")return Finish("existing_items_requested");
        var known=Flag("cooking")?Game1.player.cookingRecipes:Game1.player.craftingRecipes;
        if(!known.ContainsKey(recipe))return End("recipe_not_unlocked:"+recipe);
        var definition=new CraftingRecipe(recipe,Flag("cooking"));
        foreach(var ingredient in definition.recipeList){
            var candidates=Game1.player.Items.Where(i=>CraftingRecipe.ItemMatchesForCrafting(i,ingredient.Key)).ToArray();
            if(candidates.Any(i=>i.modData.ContainsKey("StardewCompanion/Protected")))return End("protected_recipe_ingredient:"+ingredient.Key);
            if(Flag("cooking")&&candidates.Any(i=>i.Quality!=Num("ingredientQuality")))return End("ingredient_quality_requires_loadout:"+ingredient.Key);
            if(candidates.Sum(i=>i.Stack)<ingredient.Value*Num("count",1))return End("recipe_materials_missing:"+ingredient.Key);
        }
        if(Flag("cooking")&&Count("(O)917")>0)return End("automatic_seasoning_requires_separate_loadout");
        return Finish("recipe_and_inventory_verified");
    }
    private string Craft()
    {
        if(_crafting==null){
            if(Game1.activeClickableMenu!=null)return End("unexpected_menu");
            if(Flag("cooking")&&(Game1.currentLocation is not FarmHouse home||home.upgradeLevel<1))return End("accessible_kitchen_required");
            if(PrepareRecipe()!="completed")return End(Reason);
            _crafting=new CraftingPage(100,100,800,600,cooking:Flag("cooking"),standaloneMenu:true);
            Game1.activeClickableMenu=_crafting;
        }
        if(!ReferenceEquals(Game1.activeClickableMenu,_crafting))return End("crafting_menu_changed");
        if(_crafting.heldItem!=null){
            var empty=Game1.player.Items.IndexOf(null);if(empty<0){
                var same=Game1.player.Items.Select((i,n)=>new{i,n}).FirstOrDefault(p=>p.i!=null&&p.i.canStackWith(_crafting.heldItem)&&p.i.Stack+_crafting.heldItem.Stack<=p.i.maximumStackSize());empty=same?.n??-1;
            }
            if(empty<0)return End("crafted_output_inventory_full");
            var slot=_crafting.inventory.inventory[empty].bounds.Center;_crafting.receiveLeftClick(slot.X,slot.Y);
            if(_crafting.heldItem!=null)return End("crafted_output_not_received");
            Completed++;_crafts++;
        }
        if(_crafts>=Num("count",1)){_crafting.exitThisMenu();return Finish("native_recipe_outputs_received");}
        var entry=_crafting.pagesOfCraftingRecipes.SelectMany((p,index)=>p.Select(pair=>(pair,index))).FirstOrDefault(e=>e.pair.Value.name==Text("recipe"));
        if(entry.pair.Key==null)return End("recipe_not_visible");
        if(!entry.pair.Value.doesFarmerHaveIngredientsInInventory())return End("recipe_materials_missing_after_partial_craft");
        var before=BusinessExecutor.Inventory();_crafting.currentCraftingPage=entry.index;
        var click=entry.pair.Key.bounds.Center;_crafting.receiveRightClick(click.X,click.Y);
        if(_crafting.heldItem==null)return End("native_recipe_did_not_create_output");
        _results.Add(new{recipe=Text("recipe"),output=_crafting.heldItem.QualifiedItemId,count=_crafting.heldItem.Stack});
        return "running";
    }
    private string Farming(AiInputSimulator input)
    {
        if(Game1.activeClickableMenu!=null)return End("unexpected_menu");
        if(_action=="fertilize"||_action=="remove_dead"){
            var body=Body;var location=body.GetProperty("location").GetString()!;
            var cells=Enumerable.Range(body.GetProperty("x").GetInt32(),body.GetProperty("width").GetInt32())
                .SelectMany(x=>Enumerable.Range(body.GetProperty("y").GetInt32(),body.GetProperty("height").GetInt32()).Select(y=>new GridPoint(x,y)));
            if(Game1.currentLocation.NameOrUniqueName!=location){var travel=Near(location,new(body.GetProperty("x").GetInt32(),body.GetProperty("y").GetInt32()),input);return travel is "blocked" or "unsupported"?End("plot_unreachable"):"running";}
            if(_target==null)_target=cells.Where(p=>!_done.Contains(p)).Where(p=>{
                if(!Game1.currentLocation.terrainFeatures.TryGetValue(new(p.X,p.Y),out var f)||f is not HoeDirt d)return false;
                return _action=="remove_dead"?d.crop?.dead.Value==true:d.crop==null&&string.IsNullOrEmpty(d.fertilizer.Value);}).Select(p=>(GridPoint?)p).FirstOrDefault();
            if(_target is not {} target)return Finish("region_native_operations_finished");
            var move=Near(location,target,input);if(move=="blocked"||move=="unsupported")return End("target_unreachable");if(move!="reached")return "running";
            if(_action=="remove_dead"){
                var tool=Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w=>w.isScythe());if(tool==null)return End("scythe_missing");
                if(AreaActionExecutor.HasScytheHarvestRisk(target,tool))return End("harvestable_crop_protected_from_scythe_area");
                if(_wait==0){Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(tool);Face(target);input.Tool=true;_wait=Environment.TickCount64+800;return "running";}
                if(Environment.TickCount64<_wait)return "running";
                if(Game1.currentLocation.terrainFeatures.TryGetValue(new(target.X,target.Y),out var terrain)&&terrain is HoeDirt {crop:not null})return End("dead_crop_clear_not_verified");
            }else if(!PlaceItem(Text("itemId"),target))return End("fertilizer_native_rejected");
            _done.Add(target);_target=null;_wait=0;Completed++;return "running";
        }
        if(_action=="place"||_action=="plant_tree"){
            var move=Near(Map,Tile,input);if(move is "blocked" or "unsupported")return End("placement_unreachable");if(move!="reached")return "running";
            var id=Text("itemId");
            if(_action=="plant_tree"&&(ItemRegistry.Create(id) is not StardewValley.Object seedling||!seedling.IsWildTreeSapling()&&!seedling.IsFruitTreeSapling()))return End("tree_seed_or_sapling_required");
            if(_action=="plant_tree"&&IsFruitTreeSapling(id)&&FruitTree.IsGrowthBlocked(new(Tile.X,Tile.Y),Game1.currentLocation))return End("fruit_tree_growth_space_blocked");
            if(Game1.currentLocation.objects.TryGetValue(new(Tile.X,Tile.Y),out var existing)&&existing.QualifiedItemId==id){Completed=0;return Finish("already_placed");}
            if(!PlaceItem(id,Tile))return End("native_placement_rejected");
            Completed=1;
            if(Body.TryGetProperty("attachmentId",out var attachment)&&attachment.ValueKind!=JsonValueKind.Null){
                var item=Item(attachment.GetString()!);if(item==null)return End("attachment_missing");
                var before=Count(item.QualifiedItemId);Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(item);_interact(Tile);
                if(Count(item.QualifiedItemId)!=before-1)return End("attachment_consumption_not_verified");
            }
            if(_action=="plant_tree")_results.Add(new{season=Game1.currentSeason,fruitTree=IsFruitTreeSapling(id),growth=IsFruitTreeSapling(id)?"grows_in_all_seasons_fruit_depends_on_tree_season":Game1.currentSeason=="winter"?"wild_tree_winter_growth_requires_native_exception":"native_growth_rules"});
            Completed=1;return Finish(_action=="plant_tree"?"native_tree_planted":"native_item_placed");
        }
        if(_action=="machine")return ServiceMachine(input);
        return FarmingExtras(input);
    }
    private bool PlaceItem(string id,GridPoint tile)
    {
        var item=Item(id) as StardewValley.Object;if(item==null)return false;
        var before=Count(id);Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(item);
        return Utility.tryToPlaceItem(Game1.currentLocation,item,tile.X*64,tile.Y*64)&&Count(id)==before-1;
    }
    private static bool IsFruitTreeSapling(string id)=>Game1.fruitTreeData.ContainsKey(ItemRegistry.GetDataOrErrorItem(id).ItemId);
    private static void Face(GridPoint target){var p=Navigation.Current;Game1.player.faceDirection(target.X>p.X?1:target.X<p.X?3:target.Y>p.Y?2:0);}
    private static int EmptyHand(){var empty=Game1.player.Items.IndexOf(null);return empty>=0?empty:Game1.player.Items.ToList().FindIndex(i=>i is Tool);}
    private string Recover(AiInputSimulator input)
    {
        var targetStamina=_args.GetProperty("targetStamina").GetSingle();var targetHealth=Num("targetHealth");
        if(targetStamina>Game1.player.MaxStamina||targetHealth>Game1.player.maxHealth)return End("recovery_target_exceeds_character_maximum");
        if(Game1.player.Stamina>=targetStamina&&Game1.player.health>=targetHealth)return Finish("requested_recovery_reached");
        var minutes=Game1.timeOfDay/100*60+Game1.timeOfDay%100;if(_recoveryStartMinutes<0)_recoveryStartMinutes=minutes;
        if(minutes-_recoveryStartMinutes>=Num("maxGameMinutes"))return End("recovery_requested_wait_limit");
        if(_wait!=0&&Environment.TickCount64<_wait)return "running";
        if(Text("method")=="food"){
            if(Game1.player.isEating||!Game1.player.CanMove)return "running";
            if(Completed>=Num("maxItems"))return End("recovery_item_limit_reached");
            var food=Item(Text("itemId"),Num("quality")) as StardewValley.Object;if(food==null)return End("requested_food_missing");
            if(food.edibility.Value<=0||food.modData.ContainsKey("StardewCompanion/Protected"))return End("requested_food_not_restorative_or_protected");
            Game1.player.CurrentToolIndex=Game1.player.Items.IndexOf(food);
            Game1.player.eatObject(food);
            if(!Game1.player.isEating)return End("native_food_consumption_rejected");
            Game1.player.reduceActiveItemByOne();Completed++;_wait=Environment.TickCount64+1500;return "running";
        }
        return RecoverByLocation(input);
    }
    internal bool Cleanup()
    {
        _disposed=true;
        if(_crafting!=null&&ReferenceEquals(Game1.activeClickableMenu,_crafting)){
            if(_crafting.heldItem is Item held){if(Game1.player.addItemToInventoryBool(held))_crafting.heldItem=null;else{Game1.createItemDebris(held,Game1.player.Position,Game1.player.FacingDirection,Game1.currentLocation);_crafting.heldItem=null;}}
            _crafting.exitThisMenu();
        }
        return CleanupDomain();
    }
}
