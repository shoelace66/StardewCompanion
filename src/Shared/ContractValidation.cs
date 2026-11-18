using System.Text.Json;

namespace StardewCompanion.Protocol;

public static class ContractValidation
{
    public static void ValidateSchema(JsonElement schema,JsonElement value,string path="arguments")=>Check(value,schema,path);
    public static void ValidateNavigation(JsonElement args)
    {
        var named=args.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(name.GetString());
        var located=args.TryGetProperty("location",out var location)&&location.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(location.GetString());
        var hasX=args.TryGetProperty("x",out var x);var hasY=args.TryGetProperty("y",out var y);
        if(!named&&!located)throw new InvalidOperationException("go_to_requires_location_or_name");
        if(args.TryGetProperty("name",out _)&&!named)throw new InvalidOperationException("go_to_name_must_not_be_empty");
        if(args.TryGetProperty("location",out _)&&!located)throw new InvalidOperationException("go_to_location_must_not_be_empty");
        if(hasX!=hasY)throw new InvalidOperationException("go_to_coordinates_require_both_x_and_y");
        if(hasX&&(named||!located))throw new InvalidOperationException("go_to_use_location_coordinates_or_name_not_both");
        if(hasX&&(!x.TryGetInt32(out var left)||!y.TryGetInt32(out var top)||left<0||top<0))throw new InvalidOperationException("go_to_invalid_coordinates");
    }
    public static void Validate(BusinessCapability contract,JsonElement args,bool internalFields=false)
    {
        using var properties=JsonDocument.Parse(contract.PropertiesJson);
        foreach(var key in contract.Required)if(!args.TryGetProperty(key,out _))throw new InvalidOperationException(contract.Name+":missing_parameter:"+key);
        if(contract.Name=="purchase_items"&&args.TryGetProperty("items",out var purchases)&&purchases.ValueKind==JsonValueKind.Array){
            foreach(var item in purchases.EnumerateArray())
                if(item.ValueKind==JsonValueKind.Object&&(!item.TryGetProperty("itemName",out var name)||name.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(name.GetString())))
                    throw new InvalidOperationException("shop_purchase_requires_itemName:所有商店购买均使用itemName商品名称，不再接受itemId；匹配失败会返回当前真实目录");
        }
        foreach(var field in args.EnumerateObject()){
            if(internalFields&&field.Name=="_bindings")continue;
            if(!properties.RootElement.TryGetProperty(field.Name,out var schema))throw new InvalidOperationException(contract.Name+":unknown_parameter:"+field.Name);
            Check(field.Value,schema,field.Name);
        }
        if(contract.Name=="attend_event"&&args.GetProperty("activity").GetString()=="shop"
            &&(!args.TryGetProperty("itemName",out var eventName)||eventName.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(eventName.GetString())))
            throw new InvalidOperationException("event_purchase_requires_itemName:节日购物同样使用itemName商品名称");
        if(contract.Name=="attend_event"&&args.GetProperty("activity").GetString()=="shop"&&args.GetProperty("count").GetInt32()<1)
            throw new InvalidOperationException("event_purchase_requires_positive_count");
        void RequireText(string key){
            if(!args.TryGetProperty(key,out var text)||text.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(text.GetString()))
                throw new InvalidOperationException(contract.Name+":required_target_name_or_identity:"+key);
        }
        if(contract.Name is "craft_items" or "cook_recipes" or "craft_and_place")RequireText("recipe");
        if(contract.Name is "collect_outputs" or "service_machines"){
            if(args.TryGetProperty("targets",out _)&&(args.TryGetProperty("machineName",out _)||args.TryGetProperty("region",out _)))
                throw new InvalidOperationException("machine_selection_use_targets_or_name_region");
            if(contract.Name=="service_machines"&&!args.TryGetProperty("targets",out _)&&args.GetProperty("mode").GetString()!="collect")RequireText("inputName");
        }
        if(contract.Name is "upgrade_tool" or "collect_upgraded_tool")RequireText("toolName");
        if(contract.Name=="order_building")RequireText("buildingType");
        if(contract.Name=="modify_building"){
            RequireText("buildingName");if(args.GetProperty("operation").GetString()=="upgrade")RequireText("upgradeType");
        }
        if(contract.Name=="manage_livestock"){
            var operation=args.GetProperty("operation").GetString();
            if(operation=="purchase"){RequireText("animalType");RequireText("name");RequireText("buildingName");}
            if(operation is "sell" or "rehome")RequireText("animalName");
            if(operation=="rehome")RequireText("buildingName");
            if(operation=="incubate"){RequireText("itemName");RequireText("buildingName");}
        }
    }
    private static void Check(JsonElement value,JsonElement schema,string path)
    {
        if(schema.TryGetProperty("oneOf",out var variants)){
            var matches=0;var errors=new List<string>();string? selectedError=null;
            foreach(var variant in variants.EnumerateArray())try{Check(value,variant,path);matches++;}catch(InvalidOperationException ex){
                errors.Add(ex.Message);
                if(value.ValueKind==JsonValueKind.Object&&value.TryGetProperty("skill",out var skill)&&skill.ValueKind==JsonValueKind.String
                    &&variant.TryGetProperty("properties",out var properties)&&properties.TryGetProperty("skill",out var selector)&&selector.TryGetProperty("enum",out var names)
                    &&names.EnumerateArray().Any(n=>n.GetString()==skill.GetString()))selectedError=ex.Message;
            }
            if(matches!=1)throw new InvalidOperationException("parameter_variant:"+path+";matches="+matches+";details="+(selectedError??string.Join(" | ",errors.Take(3))));
        }
        if(schema.TryGetProperty("type",out var type)){
            var types=type.ValueKind==JsonValueKind.Array?type.EnumerateArray().Select(v=>v.GetString()).ToArray():new[]{type.GetString()};
            bool Matches(string? t)=>t switch{
                "null"=>value.ValueKind==JsonValueKind.Null,"object"=>value.ValueKind==JsonValueKind.Object,
                "array"=>value.ValueKind==JsonValueKind.Array,"string"=>value.ValueKind==JsonValueKind.String,
                "boolean"=>value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer"=>value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out _),"number"=>value.ValueKind==JsonValueKind.Number,_=>false};
            if(!types.Any(Matches))throw new InvalidOperationException("parameter_type:"+path+";expected="+string.Join("/",types)+";actual="+value.ValueKind);
        }
        if(schema.TryGetProperty("enum",out var allowed)&&!allowed.EnumerateArray().Any(v=>v.ValueKind==value.ValueKind&&(v.ValueKind==JsonValueKind.String?v.GetString()==value.GetString():v.GetRawText()==value.GetRawText())))
            throw new InvalidOperationException("parameter_enum:"+path);
        if(value.ValueKind==JsonValueKind.Number){
            var n=value.GetDouble();
            if(schema.TryGetProperty("minimum",out var min)&&n<min.GetDouble()||schema.TryGetProperty("maximum",out var max)&&n>max.GetDouble())
                throw new InvalidOperationException("parameter_range:"+path);
        }
        if(value.ValueKind==JsonValueKind.Array){
            var count=value.GetArrayLength();
            if(schema.TryGetProperty("minItems",out var min)&&count<min.GetInt32()||schema.TryGetProperty("maxItems",out var max)&&count>max.GetInt32())
                throw new InvalidOperationException("parameter_array_length:"+path);
            if(schema.TryGetProperty("items",out var items)){var index=0;foreach(var element in value.EnumerateArray())Check(element,items,path+"["+index+++"]");}
        }
        if(value.ValueKind==JsonValueKind.Object){
            if(schema.TryGetProperty("required",out var required))foreach(var key in required.EnumerateArray())
                if(!value.TryGetProperty(key.GetString()!,out _))throw new InvalidOperationException("missing_parameter:"+path+"."+key.GetString());
            if(!schema.TryGetProperty("properties",out var props))return;
            foreach(var field in value.EnumerateObject()){
                if(!props.TryGetProperty(field.Name,out var child)){
                    if(schema.TryGetProperty("additionalProperties",out var extra)&&extra.ValueKind==JsonValueKind.False)throw new InvalidOperationException("unknown_parameter:"+path+"."+field.Name);
                    continue;
                }
                Check(field.Value,child,path+"."+field.Name);
            }
        }
    }
}
