using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StardewCompanion.Core;



// Model-facing projections only. Original plans, receipts, player text and logs
// remain intact in storage. No model call is used to produce these summaries.
public static class DecisionContextCompression
{

    public static readonly JsonSerializerOptions ReadableJson=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public static string Serialize(object? value)=>JsonSerializer.Serialize(value,ReadableJson);
    public static JsonNode? Get(JsonNode? node,string name)=>node is JsonObject obj
        ?obj.FirstOrDefault(p=>p.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value:null;
    public static string Text(JsonNode? node,string name)=>Get(node,name)?.ToString()??"";
    public static JsonNode? Decode(JsonNode? value)
    {
        if(value is JsonValue v&&v.TryGetValue<string>(out var text)&&text.TrimStart() is var trimmed&&
            (trimmed.StartsWith('{')||trimmed.StartsWith('[')))
        {try{return JsonNode.Parse(text);}catch(JsonException){}}
        return value?.DeepClone();
    }

    private static JsonObject Pick(JsonNode? source,params string[] fields)
    {
        var result=new JsonObject();
        foreach(var field in fields)if(Get(source,field) is {} value)result[field]=value.DeepClone();
        return result;
    }
    public static JsonObject Plan(JsonNode? source,bool includeSteps=false)
    {
        var result=Pick(source,"id","revision","goal","state","basis","assumptions","successCriteria","pending");
        if(Text(source,"state")!="planned")result["pending"]=new JsonArray();
        if(includeSteps&&Get(source,"batch") is {} batch)result["steps"]=batch.DeepClone();
        // A completed batch is evidence for its steps, never proof that its goal
        // or model-written pending items are complete.
        result["pendingMeaning"]=Text(source,"state")=="planned"?"active_plan_items_require_recheck":"previous_plan_pending_retired_until_replanned";
        return result;
    }
    private static JsonNode? Evidence(JsonNode? source)
    {
        if(source is JsonObject obj){
            var result=new JsonObject();
            foreach(var pair in obj){
                if(pair.Key is "affectedTiles" or "remainingDrops" or "chunks"){
                    if(pair.Value is JsonArray list)result[pair.Key]=new JsonObject{
                        ["count"]=list.Count,["sample"]=new JsonArray(list.Take(4).Select(v=>v?.DeepClone()).ToArray()),["sampleOnly"]=list.Count>4};
                    else result[pair.Key]=pair.Value?.DeepClone();
                }else result[pair.Key]=Evidence(pair.Key.Equals("details",StringComparison.OrdinalIgnoreCase)?Decode(pair.Value):pair.Value);
            }
            return result;
        }
        if(source is JsonArray array)return new JsonArray(array.Select(Evidence).ToArray());
        return source?.DeepClone();
    }
    public static JsonObject Receipt(JsonNode? source)
    {
        var result=Pick(source,"taskId","skill","status","reason","completed","observationRevision");
        if(Decode(Get(source,"details")) is JsonObject detail){
            foreach(var name in new[]{"requestedTarget","playerStart","playerEnd","location","collectedItems","actualCost","completedUnit","businessDetails"})
                if(Get(detail,name) is {} value)result[name]=Evidence(value);
            if(result["requestedTarget"] is JsonObject target){target.Remove("_bindings");target.Remove("allowedIds");}
            foreach(var name in new[]{"affectedTiles","remainingDrops"})
                if(Get(detail,name) is JsonArray array)result[name]=new JsonObject{["count"]=array.Count,["sample"]=new JsonArray(array.Take(4).Select(v=>v?.DeepClone()).ToArray()),["sampleOnly"]=array.Count>4};
        }
        return result;
    }
    public static JsonNode? ToolResult(string name,object? result)
    {
        var node=JsonSerializer.SerializeToNode(result);
        if(name=="save_plan")return Plan(node);
        if(Get(node,"taskId")!=null){
            if(name is "observe" or "inspect_target" or "inspect_targets" or "inspect_route" or "inspect_area" or "survey_area" or "read_mailbox"){
                var query=Pick(node,"taskId","status","reason","observationRevision");query["data"]=CompactQuery(Decode(Get(node,"details")));return query;
            }
            return Receipt(node);
        }
        return node;
    }
    private static void Table(JsonObject obj,string field)
    {
        if(Get(obj,field) is not JsonArray array||array.Count<8||array.Any(v=>v is not JsonObject))return;
        var columns=array.OfType<JsonObject>().SelectMany(o=>o.Select(p=>p.Key)).Distinct().ToArray();
        var missing=new JsonArray();
        var rows=new JsonArray(array.OfType<JsonObject>().Select((o,row)=>(JsonNode)new JsonArray(columns.Select((c,column)=>{
            if(o.TryGetPropertyValue(c,out var v))return v?.DeepClone();
            missing.Add(new JsonArray(row,column));return null;
        }).ToArray())).ToArray());
        var table=new JsonObject{["columns"]=new JsonArray(columns.Select(c=>(JsonNode?)JsonValue.Create(c)).ToArray()),["rows"]=rows};
        if(missing.Count>0)table["missing"]=missing;
        obj[field]=table;
    }
    private static JsonNode? CompactQuery(JsonNode? data)
    {
        if(data is JsonObject obj){
            foreach(var key in obj.Select(p=>p.Key).ToArray()){
                obj[key]=CompactQuery(obj[key]?.DeepClone());Table(obj,key);
            }
        }else if(data is JsonArray array){
            var holder=new JsonObject{["items"]=new JsonArray(array.Select(v=>CompactQuery(v?.DeepClone())).ToArray())};
            Table(holder,"items");return holder["items"]?.DeepClone();
        }
        return data;
    }
}
