using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewCompanion.Mod;

internal static class ExecutionTargets
{
    internal sealed record Choice(string Id,string Name,string? Alias=null,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? Quality=null,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? Quantity=null);
    internal sealed record ResolutionFailure(string Field,string Requested,string Reason,Choice[] Candidates,string CatalogMeaning,int CandidateCount=0,bool CandidatesTruncated=false);
    internal sealed class TargetOutsideMapException(string map,string path,int x,int y,int width,int height,int maxX,int maxY)
        :InvalidOperationException($"target_outside_map:{map};path={path};x={x};y={y};width={width};height={height};valid_x=0..{maxX};valid_y=0..{maxY}")
    {
        internal object Evidence {get;}=new{blockedMap=map,target=new{x,y,width,height},bounds=new{minX=0,minY=0,maxX,maxY},
            releaseCondition="use_observed_coordinates_inside_the_destination_map"};
    }

    internal static string? Resolve(string requested,IEnumerable<Choice> source,out Choice[] candidates)
    {
        candidates=source.ToArray();
        var exact=candidates.Where(c=>c.Id==requested).ToArray();
        if(exact.Length==1)return exact[0].Id;
        var matches=candidates.Where(c=>c.Name==requested||string.Equals(c.Alias,requested,StringComparison.OrdinalIgnoreCase))
            .Select(c=>c.Id).Distinct().ToArray();
        return matches.Length==1?matches[0]:null;
    }

    internal static JsonElement NormalizeBusiness(string skill,string json,out ResolutionFailure? failure)
    {
        var root=JsonNode.Parse(json)!.AsObject();NormalizePublicReferences(root,skill);ResolutionFailure? issue=null;
        void ResolveField(JsonObject obj,string field,IEnumerable<Choice> choices,string catalogMeaning="identity_definitions_only; current availability requires native observation"){
            if(issue!=null||obj[field] is not JsonValue value||!value.TryGetValue<string>(out var requested)||string.IsNullOrWhiteSpace(requested))return;
            var id=Resolve(requested.Trim(),choices,out var catalog);
            if(id==null){
                var matching=catalog.Where(c=>c.Name==requested.Trim()||string.Equals(c.Alias,requested.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
                var choicesForReply=matching.Length>0?matching:catalog;
                issue=new(field,requested,"target_name_unknown_or_ambiguous",choicesForReply.Take(64).ToArray(),catalogMeaning,choicesForReply.Length,choicesForReply.Length>64);
            }
            else obj[field]=id;
        }
        if(root["recipe"] is JsonValue recipe&&recipe.TryGetValue<string>(out var recipeName)&&recipeName!=""){
            var cooking=skill=="cook_recipes";
            var known=cooking?Game1.player.cookingRecipes:Game1.player.craftingRecipes;
            ResolveField(root,"recipe",known.Keys.Select(id=>new Choice(id,new CraftingRecipe(id,cooking).DisplayName)),"player_learned_recipes; materials and kitchen still require validation");
        }
        foreach(var field in new[]{"buildingType","upgradeType"})
            ResolveField(root,field,Game1.buildingData.Select(p=>new Choice(p.Key,StardewValley.TokenizableStrings.TokenParser.ParseText(p.Value.Name)??p.Key)));
        if(skill is "manage_livestock" or "modify_building")
            ResolveField(root,"buildingId",Game1.getFarm().buildings.Select(b=>new Choice(b.id.Value.ToString(),b.GetIndoors()?.NameOrUniqueName??b.id.Value.ToString(),b.buildingType.Value)),
                "actual_farm_buildings; resolve the exact building name or unique interior location; ambiguous names are rejected");
        var characters=DataLoader.Characters(Game1.content).Select(p=>new Choice(p.Key,StardewValley.TokenizableStrings.TokenParser.ParseText(p.Value.DisplayName)??p.Key)).ToArray();
        ResolveField(root,"npc",characters);
        if(root["visits"] is JsonArray visits)foreach(var visit in visits.OfType<JsonObject>())ResolveField(visit,"npc",characters);
        if(skill=="manage_quests")ResolveField(root,"questId",QuestChoices(),"current quest titles; duplicate titles are rejected");
        if(skill=="respond_dialogue"&&root["responseKey"] is JsonValue response&&response.TryGetValue<string>(out var requestedResponse)&&requestedResponse!=""){
            var choices=Game1.activeClickableMenu is DialogueBox dialogue?dialogue.responses.Select(r=>new Choice(r.responseKey,r.responseText)).ToArray():Array.Empty<Choice>();
            ResolveField(root,"responseKey",choices,"currently visible dialogue response text");
        }
        if(skill=="manage_livestock"&&root["operation"]?.ToString() is "sell" or "rehome"){
            var animals=Game1.getFarm().animals.Values
                .Concat(Game1.getFarm().buildings.SelectMany(b=>b.GetIndoors() is AnimalHouse house?house.animals.Values:Enumerable.Empty<FarmAnimal>()))
                .GroupBy(a=>a.myID.Value).Select(g=>g.First())
                .Select(a=>{
                    var home=a.home;var location=home?.GetIndoors()?.NameOrUniqueName??"Farm";
                    var homeName=home?.buildingType.Value??"Farm";
                    return new Choice(a.myID.Value.ToString(),a.displayName,$"{a.displayName} ({a.type.Value}, {homeName} at {home?.tileX.Value},{home?.tileY.Value}; {location}; tile {a.TilePoint.X},{a.TilePoint.Y})");
                }).ToArray();
            if(root["animalId"] is JsonValue one&&one.TryGetValue<string>(out var animalName)){
                var selected=Resolve(animalName,animals,out var offered);
                if(selected==null){var matches=animals.Where(c=>c.Name==animalName||c.Alias==animalName).ToArray();issue=new("animalName",animalName,"target_name_unknown_or_ambiguous",(matches.Length>0?matches:offered).Take(64).ToArray(),"current_farm_animals; provide the exact observed animal name and enough context to disambiguate",matches.Length>0?matches.Length:offered.Length,(matches.Length>64||offered.Length>64));}
                else root["animalId"]=selected;
            }
        }
        if(skill=="tend_animals"&&root["animalIds"] is JsonArray animalNames){
            var animals=Game1.getFarm().animals.Values.Concat(Game1.getFarm().buildings.SelectMany(b=>b.GetIndoors() is AnimalHouse house?house.animals.Values:Enumerable.Empty<FarmAnimal>()))
                .GroupBy(a=>a.myID.Value).Select(g=>g.First()).Select(a=>new Choice(a.myID.Value.ToString(),a.displayName,$"{a.displayName} ({a.type.Value}, {a.home?.buildingType.Value}; tile {a.TilePoint.X},{a.TilePoint.Y})")).ToArray();
            var ids=new JsonArray();foreach(var value in animalNames){var requested=value?.GetValue<string>()??"";var id=Resolve(requested,animals,out var offered);if(id==null){var matches=animals.Where(c=>c.Name==requested||c.Alias==requested).ToArray();issue=new("animalNames",requested,"target_name_unknown_or_ambiguous",(matches.Length>0?matches:offered).Take(64).ToArray(),"current_farm_animals; provide exact observed names when names repeat",matches.Length>0?matches.Length:offered.Length,(matches.Length>64||offered.Length>64));break;}ids.Add(id);}if(issue==null)root["animalIds"]=ids;
        }
        if(skill=="care_for_pets"&&root["petIds"] is JsonArray petNames){
            var pets=Game1.locations.SelectMany(l=>l.characters.OfType<StardewValley.Characters.Pet>()).GroupBy(p=>p.petId.Value).Select(g=>g.First())
                .Select(p=>new Choice(p.petId.Value.ToString(),p.displayName,$"{p.displayName} ({p.Name} at {p.currentLocation?.NameOrUniqueName}; tile {p.TilePoint.X},{p.TilePoint.Y})")).ToArray();
            var ids=new JsonArray();foreach(var value in petNames){var requested=value?.GetValue<string>()??"";var id=Resolve(requested,pets,out var offered);if(id==null){var matches=pets.Where(c=>c.Name==requested||c.Alias==requested).ToArray();issue=new("petNames",requested,"target_name_unknown_or_ambiguous",(matches.Length>0?matches:offered).Take(64).ToArray(),"current pets; provide observed names when names repeat",matches.Length>0?matches.Length:offered.Length,(matches.Length>64||offered.Length>64));break;}ids.Add(id);}if(issue==null)root["petIds"]=ids;
        }
        if(skill=="attend_event"){
            var active=Game1.currentLocation.currentEvent??Game1.locations.Select(l=>l.currentEvent).FirstOrDefault(e=>e!=null);
            if(active!=null){
                if(string.IsNullOrEmpty(root["eventId"]?.GetValue<string>()))root["eventId"]=active.id;
                else if(root["eventId"]!.ToString()!=active.id)
                    issue=new("eventName",root["eventId"]!.ToString(),"active_event_name_mismatch",new[]{new Choice(active.id,active.id)},"only the currently active native event is addressable");
            }
        }
        if(root["shopId"] is JsonValue shopName&&shopName.TryGetValue<string>(out var requestedShop)&&requestedShop!=""){
            var shops=KnownShops();var id=Resolve(requestedShop,shops,out var offered);if(id==null){var matches=offered.Where(c=>c.Name==requestedShop||c.Alias==requestedShop).ToArray();issue??=new("shopName",requestedShop,"target_name_unknown_or_ambiguous",(matches.Length>0?matches:offered).Take(64).ToArray(),"supported native shop names; choose an exact listed name",matches.Length>0?matches.Length:offered.Length,offered.Length>64);}else root["shopId"]=id;
        }
        if(skill=="sell_items"){
            failure=issue??NormalizeSaleItems(root);
            return JsonSerializer.SerializeToElement(root);
        }
        if(skill=="donate_bundle"&&root["bundleId"] is JsonValue bundleName&&bundleName.TryGetValue<string>(out var requestedBundle)&&int.TryParse(requestedBundle,out _)==false){
            var candidates=BundleChoices();var id=Resolve(requestedBundle,candidates,out var offered);if(id==null){var matches=offered.Where(c=>c.Name==requestedBundle||c.Alias==requestedBundle).ToArray();issue=new("bundleName",requestedBundle,"target_name_unknown_or_ambiguous",(matches.Length>0?matches:offered).Take(64).ToArray(),"currently observed native community bundles; include the room name when bundle titles repeat",matches.Length>0?matches.Length:offered.Length,offered.Length>64);}else root["bundleId"]=int.Parse(id,System.Globalization.CultureInfo.InvariantCulture);
        }
        var inventory=Game1.player.Items.Where(i=>i!=null).Select(i=>new Choice(i.QualifiedItemId,i.DisplayName,i.Name))
            .Concat(Game1.player.toolBeingUpgraded.Value is {} pending?new[]{new Choice(pending.QualifiedItemId,pending.DisplayName,pending.Name)}:Array.Empty<Choice>())
            .GroupBy(c=>c.Id).Select(g=>g.First()).ToArray();
        Choice[]? definitions=null;
        IEnumerable<Choice> ItemDefinitions()=>definitions??=Game1.objectData.Select(p=>new Choice("(O)"+p.Key,
                StardewValley.TokenizableStrings.TokenParser.ParseText(p.Value.DisplayName)??p.Value.Name,p.Value.Name))
            .Concat(Game1.bigCraftableData.Select(p=>new Choice("(BC)"+p.Key,
                StardewValley.TokenizableStrings.TokenParser.ParseText(p.Value.DisplayName)??p.Value.Name,p.Value.Name)))
            .Concat(inventory).GroupBy(c=>c.Id).Select(g=>g.First()).ToArray();
        if(skill is "craft_and_place" or "install_irrigation"&&root["recipe"] is JsonValue resolvedRecipe&&resolvedRecipe.TryGetValue<string>(out var recipeId)&&!string.IsNullOrEmpty(recipeId)
            &&root["itemId"] is JsonValue outputName&&outputName.TryGetValue<string>(out var requestedOutput)&&!requestedOutput.StartsWith('(')){
            var output=new CraftingRecipe(recipeId,false).createItem();
            if(requestedOutput==output.DisplayName||requestedOutput==output.Name)root["itemId"]=output.QualifiedItemId;
        }
        void NormalizeItems(JsonNode? node){
            if(node is JsonObject obj){
                foreach(var field in new[]{"itemId","seedId","replantSeedId","fertilizerId","inputId","fuelId","attachmentId","toolId","giftId","weaponId"}){
                    if(obj[field] is not JsonValue value||!value.TryGetValue<string>(out var raw)||string.IsNullOrEmpty(raw)||raw.StartsWith('('))continue;
                    ResolveField(obj,field,field is "toolId" or "weaponId"?inventory:ItemDefinitions(),
                        field is "toolId" or "weaponId"?"inventory_or_pending_tool; availability requires validation":"native_item_identity_definitions; not evidence of ownership, unlocks or stock");
                }
                foreach(var pair in obj.ToArray())if(pair.Key!="_bindings")NormalizeItems(pair.Value);
            }else if(node is JsonArray array)foreach(var item in array)NormalizeItems(item);
        }
        NormalizeItems(root);failure=issue;
        return JsonSerializer.SerializeToElement(root);
    }

    internal static bool IsSaleItem(Item item)=>item is not Tool&&!item.modData.ContainsKey("StardewCompanion/Protected");

    private static ResolutionFailure? NormalizeSaleItems(JsonObject root)
    {
        var stock=Game1.player.Items.Where(item=>item!=null)
            .GroupBy(item=>(item.QualifiedItemId,item.Quality))
            .Select(group=>new Choice(group.Key.QualifiedItemId,group.First().DisplayName,group.First().Name,
                group.Key.Quality,group.Where(IsSaleItem).Sum(item=>item.Stack)))
            .ToArray();
        var items=root["items"]!.AsArray();
        var normalized=new JsonArray();
        var merged=new Dictionary<(string Id,int Quality),JsonObject>();
        foreach(var entry in items.OfType<JsonObject>()){
            var requested=entry["itemId"]!.GetValue<string>().Trim();
            var quality=entry["quality"]!.GetValue<int>();
            var matching=stock.Where(item=>item.Quality==quality&&
                (item.Id==requested||string.Equals(item.Name,requested,StringComparison.OrdinalIgnoreCase)||
                 string.Equals(item.Alias,requested,StringComparison.OrdinalIgnoreCase))).ToArray();
            var exact=matching.FirstOrDefault(item=>item.Id==requested);
            if(exact!=null)matching=new[]{exact};
            if(matching.Length!=1||matching[0].Quantity==0){
                var choices=matching.Length>0?matching:stock.Where(item=>item.Quantity>0).ToArray();
                var reason=matching.Length switch{0=>"sale_item_missing",1=>"sale_item_protected",_=>"sale_item_name_ambiguous"};
                return new("itemName",requested,reason,choices.Take(36).ToArray(),
                    "current_backpack; use an exact displayed name and quality or an observed qualified item ID",
                    choices.Length,choices.Length>36);
            }
            var id=matching[0].Id;
            var count=entry["count"]!.GetValue<int>();
            var retain=entry["retain"]!.GetValue<int>();
            if(merged.TryGetValue((id,quality),out var earlier)){
                earlier["count"]=checked(earlier["count"]!.GetValue<int>()+count);
                earlier["retain"]=Math.Max(earlier["retain"]!.GetValue<int>(),retain);
            }else{
                var row=new JsonObject{["itemId"]=id,["quality"]=quality,["count"]=count,["retain"]=retain};
                normalized.Add(row);
                merged.Add((id,quality),row);
            }
        }
        root["items"]=normalized;
        return null;
    }

    private static void NormalizePublicReferences(JsonObject root,string skill)
    {
        var preservePurchaseName=skill=="purchase_items"||skill=="attend_event"&&root["activity"]?.ToString()=="shop";
        var map=new Dictionary<string,string>(StringComparer.Ordinal){
            ["seedName"]="seedId",["replantSeedName"]="replantSeedId",["fertilizerName"]="fertilizerId",["inputName"]="inputId",["fuelName"]="fuelId",["attachmentName"]="attachmentId",
            ["toolName"]="toolId",["giftName"]="giftId",["weaponName"]="weaponId",["animalName"]="animalId",["animalNames"]="animalIds",["petNames"]="petIds",["buildingName"]="buildingId",
            ["npcName"]="npc",["eventName"]="eventId",["shopName"]="shopId",["questName"]="questId",["projectName"]="projectId",["playerName"]="playerId",["responseName"]="responseKey",["bundleName"]="bundleId"
        };
        if(!preservePurchaseName)map["itemName"]="itemId";
        void Visit(JsonNode? node){
            if(node is JsonObject obj){
                foreach(var pair in obj.ToArray()){
                    if(map.TryGetValue(pair.Key,out var internalName)&&obj[internalName]==null){obj[internalName]=pair.Value==null?null:JsonNode.Parse(pair.Value.ToJsonString());obj.Remove(pair.Key);}
                }
                foreach(var pair in obj.ToArray())if(pair.Key!="_bindings")Visit(pair.Value);
            }else if(node is JsonArray array)foreach(var child in array)Visit(child);
        }
        Visit(root);
    }

    private static Choice[] BundleChoices()=>Game1.netWorldState.Value.BundleData.Select(pair=>{
        var key=pair.Key.Split('/',StringSplitOptions.RemoveEmptyEntries);var room=key.Length>1?key[0]:"";
        var index=key.Length>1&&int.TryParse(key[^1],out var parsed)?parsed:-1;var title=pair.Value.Split('/')[0];
        return new Choice(index.ToString(System.Globalization.CultureInfo.InvariantCulture),title,$"{room}: {title}");
    }).Where(c=>c.Id!="-1").ToArray();
    private static Choice[] QuestChoices()=>Game1.player.questLog.Select(q=>new Choice(QuestIdentity.For(q)??"",q.questTitle))
        .Concat(Game1.questOfTheDay is {} daily?new[]{new Choice(QuestIdentity.For(daily)??"",daily.questTitle)}:Array.Empty<Choice>())
        .Concat(Game1.player.team.specialOrders.Select(q=>new Choice(q.questKey.Value,q.GetName()))).GroupBy(c=>c.Id).Select(g=>g.First()).ToArray();
    private static Choice[] KnownShops()=>new[]{
        new Choice("SeedShop","Pierre's General Store","Pierre's Shop"),new Choice("AnimalShop","Marnie's Ranch","Marnie’s Ranch"),
        new Choice("Carpenter","Robin's Carpenter Shop","Carpenter Shop"),new Choice("Blacksmith","Clint's Blacksmith Shop","Blacksmith"),
        new Choice("FishShop","Willy's Fish Shop","Willy's Shop"),new Choice("Saloon","Stardrop Saloon","Saloon"),
        new Choice("JojaMart","JojaMart"),new Choice("Hospital","Harvey's Clinic","Hospital"),new Choice("HatMouse","Hat Mouse Shop"),
        new Choice("Traveler","Traveling Cart","Traveling Merchant"),new Choice("DesertTrade","Desert Trader"),new Choice("ShadowShop","Krobus's Shop"),
        new Choice("IslandTrade","Island Trader"),new Choice("VolcanoShop","Volcano Dwarf Shop"),new Choice("ResortBar","Island Resort Bar")
    };

    internal static JsonElement NormalizePrimitive(string skill,string json,out ResolutionFailure? failure)
    {
        var root=JsonNode.Parse(json)!.AsObject();NormalizePublicReferences(root,skill);ResolutionFailure? issue=null;
        void ResolveField(JsonObject obj,string field,IEnumerable<Choice> source,string meaning){
            if(issue!=null||obj[field] is not JsonValue value||!value.TryGetValue<string>(out var requested)||string.IsNullOrWhiteSpace(requested))return;
            var selected=Resolve(requested.Trim(),source,out var offered);if(selected!=null){obj[field]=selected;return;}
            var matching=offered.Where(c=>c.Name==requested.Trim()||c.Alias==requested.Trim()).ToArray();issue=new(field,requested,"target_name_unknown_or_ambiguous",(matching.Length>0?matching:offered).Take(64).ToArray(),meaning,matching.Length>0?matching.Length:offered.Length,offered.Length>64);
        }
        var inventory=Game1.player.Items.Where(i=>i!=null).Select(i=>new Choice(i.QualifiedItemId,i.DisplayName,i.Name)).GroupBy(c=>c.Id).Select(g=>g.First()).ToArray();
        var definitions=Game1.objectData.Select(p=>new Choice("(O)"+p.Key,StardewValley.TokenizableStrings.TokenParser.ParseText(p.Value.DisplayName)??p.Value.Name,p.Value.Name))
            .Concat(Game1.bigCraftableData.Select(p=>new Choice("(BC)"+p.Key,StardewValley.TokenizableStrings.TokenParser.ParseText(p.Value.DisplayName)??p.Value.Name,p.Value.Name))).Concat(inventory).GroupBy(c=>c.Id).Select(g=>g.First()).ToArray();
        void Items(JsonNode? node){if(node is JsonObject obj){foreach(var field in new[]{"itemId","seedId","weaponId"})if(obj[field] is JsonValue value&&value.TryGetValue<string>(out var raw)&&raw!=""&&!raw.StartsWith('('))ResolveField(obj,field,field=="weaponId"?inventory:definitions,field=="weaponId"?"player inventory weapons":"native item display names; possession is checked by the action");foreach(var child in obj.ToArray())Items(child.Value);}else if(node is JsonArray array)foreach(var child in array)Items(child);}
        Items(root);
        if(skill=="follow_player")ResolveField(root,"playerId",Game1.getOnlineFarmers().Where(p=>p.UniqueMultiplayerID!=Game1.player.UniqueMultiplayerID).Select(p=>new Choice(p.UniqueMultiplayerID.ToString(),p.Name,p.displayName)),"currently connected player names");
        if(skill=="respond_dialogue"&&root["responseKey"] is JsonValue response&&response.TryGetValue<string>(out var requestedResponse)&&requestedResponse!=""){
            var choices=Game1.activeClickableMenu is DialogueBox dialogue?dialogue.responses.Select(r=>new Choice(r.responseKey,r.responseText)).ToArray():Array.Empty<Choice>();
            ResolveField(root,"responseKey",choices,"currently visible dialogue response text");
        }
        failure=issue;return JsonSerializer.SerializeToElement(root);
    }

    internal static void ValidateBounds(JsonElement args,string? inheritedMap=null,string path="$")
    {
        if(args.ValueKind==JsonValueKind.Array){var index=0;foreach(var child in args.EnumerateArray())ValidateBounds(child,inheritedMap,$"{path}[{index++}]");return;}
        if(args.ValueKind!=JsonValueKind.Object)return;
        var map=args.TryGetProperty("location",out var name)&&name.ValueKind==JsonValueKind.String?name.GetString():inheritedMap;
        if(map!=null&&(!AllowedLocation(map)))throw new InvalidOperationException("location_disabled:"+map);
        if(args.TryGetProperty("x",out var x)&&args.TryGetProperty("y",out var y)&&x.TryGetInt32(out var left)&&y.TryGetInt32(out var top)){
            var location=Game1.getLocationFromName(map??Game1.currentLocation.NameOrUniqueName);
            if(location?.Map==null)throw new InvalidOperationException("target_location_unknown:"+map);
            var width=args.TryGetProperty("width",out var w)&&w.TryGetInt32(out var wi)?wi:1;
            var height=args.TryGetProperty("height",out var h)&&h.TryGetInt32(out var hi)?hi:1;
            var layer=location.Map.Layers[0];
            if(left<0||top<0||width<1||height<1||(long)left+width>layer.LayerWidth||(long)top+height>layer.LayerHeight)
                throw new TargetOutsideMapException(location.NameOrUniqueName,path,left,top,width,height,layer.LayerWidth-1,layer.LayerHeight-1);
        }
        foreach(var property in args.EnumerateObject())if(property.Name!="_bindings"&&property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            ValidateBounds(property.Value,map,path+"."+property.Name);
    }
    internal static void ValidateCommandBounds(string skill,JsonElement args)
    {
        if(skill=="group_construction_space"&&args.TryGetProperty("steps",out var steps)&&steps.ValueKind==JsonValueKind.Array){
            foreach(var step in steps.EnumerateArray()){
                if(!step.TryGetProperty("skill",out var childSkill)||childSkill.ValueKind!=JsonValueKind.String
                    ||!step.TryGetProperty("arguments",out var childArgs))continue;
                var name=childSkill.GetString();
                ValidateBounds(childArgs,name is "order_building" or "modify_building"?"Farm":null);
            }
            return;
        }
        ValidateBounds(args,skill is "order_building" or "modify_building"?"Farm":null);
    }

    internal static bool AllowedLocation(string map)=>map is not ("Mine" or "SkullCave" or "SkullCavern" or "VolcanoDungeon" or "BugLand")
        &&!map.StartsWith("UndergroundMine",StringComparison.Ordinal);
}
