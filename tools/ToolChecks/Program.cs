using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;
using StardewCompanion.Protocol;

var checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
void Reject(Action action,string message){try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(message);}
void Validate(JsonNode schema,JsonNode args){using var s=JsonDocument.Parse(schema.ToJsonString());using var a=JsonDocument.Parse(args.ToJsonString());ContractValidation.ValidateSchema(s.RootElement,a.RootElement);}
JsonNode Sample(JsonNode schema){
    if(schema["enum"] is JsonArray choices)return choices[0]!.DeepClone();
    if(schema["oneOf"] is JsonArray variants)return Sample(variants[0]!);
    var type=schema["type"] is JsonArray types?types.Select(t=>t!.ToString()).First(t=>t!="null"):schema["type"]!.ToString();
    if(type=="object"){var result=new JsonObject();foreach(var key in schema["required"] as JsonArray??new())result[key!.ToString()]=Sample(schema["properties"]![key.ToString()]!);return result;}
    if(type=="array"){var result=new JsonArray();for(var i=0;i<(schema["minItems"]?.GetValue<int>()??0);i++)result.Add(Sample(schema["items"]!));return result;}
    return type switch{"string"=>JsonValue.Create("sample")!,"integer"=>JsonValue.Create(schema["minimum"]?.GetValue<int>()??1)!,"number"=>JsonValue.Create(1.0)!,"boolean"=>JsonValue.Create(false)!,_=>throw new Exception(type)};
}
void AuditSchema(JsonNode schema,string path){
    if(schema is not JsonObject obj)return;
    if(obj["required"] is JsonArray required){var names=required.Select(n=>n!.ToString()).ToArray();Check(names.Distinct().Count()==names.Length,path+": duplicate required field");foreach(var name in names)Check(obj["properties"]?[name]!=null,path+": missing property "+name);}
    foreach(var (name,node) in obj){if(node is JsonArray array){foreach(var child in array)if(child is JsonObject)AuditSchema(child,path+"."+name);}else if(node is JsonObject child){if(name=="properties"){foreach(var (key,value) in child)if(value!=null)AuditSchema(value,path+"."+key);}else AuditSchema(child,path+"."+name);}}
}
var primitives=ToolCatalog.PrimitiveDefinitions();
Check(primitives.Select(t=>t!["function"]!["name"]!.ToString()).Distinct().Count()==primitives.Count,"duplicate primitive tool");
foreach(var t in primitives){var f=t!["function"]!;var schema=f["parameters"]!;AuditSchema(schema,f["name"]!.ToString());Validate(schema,Sample(schema));}
foreach(var t in ToolCatalog.SeasonMaintenanceDefinitions()){var f=t!["function"]!;AuditSchema(f["parameters"]!,f["name"]!.ToString());Validate(f["parameters"]!,Sample(f["parameters"]!));}
var tools=ToolCatalog.Definitions();var groups=tools.Where(t=>t!["function"]!["name"]!.ToString().StartsWith("group_")).ToArray();
Check(groups.Length==8,"all eight parent tools must be discoverable");
Check(!tools.Any(t=>t!["function"]!["name"]!.ToString()=="execute_plan"),"old catch-all plan tool remains exposed");
Check(ToolCatalog.TopLevelFrequentTools.Length==10&&ToolCatalog.TopLevelFrequentTools.All(n=>tools.Any(t=>t!["function"]!["name"]!.ToString()==n)),"ten direct tools missing");
var covered=new HashSet<string>();
foreach(var tool in tools){var f=tool!["function"]!;AuditSchema(f["parameters"]!,f["name"]!.ToString());}
foreach(var group in groups){var f=group!["function"]!;var variants=f["parameters"]!["properties"]!["steps"]!["items"]!["oneOf"]!.AsArray();
    foreach(var variant in variants){var name=variant!["properties"]!["skill"]!["enum"]![0]!.ToString();Check(covered.Add(name),"mutation appears under multiple parents: "+name);Check(f["name"]!.ToString()=="group_"+BusinessCapabilities.GroupFor(name),"wrong parent: "+name);
        var childArguments=Sample(variant["properties"]!["arguments"]!).AsObject();ToolCatalog.ValidateArguments(name,childArguments);
        var plan=new JsonObject{["planId"]="audit-"+name,["steps"]=new JsonArray(new JsonObject{["skill"]=name,["arguments"]=childArguments.DeepClone()})};
        Validate(f["parameters"]!,plan);Check(ShortPlan.Parse(plan).Steps.Single().Skill==name,"child cannot be parsed: "+name);
        var wrong=(JsonObject)plan.DeepClone();wrong["steps"]![0]!["skill"]="nonexistent";Reject(()=>Validate(f["parameters"]!,wrong),"parent accepted unknown child");
    }
}
foreach(var name in ToolCatalog.Actions.Where(n=>n!="mine_travel").Concat(BusinessCapabilities.All.Where(c=>!c.Query).Select(c=>c.Name)))Check(covered.Contains(name),"missing action: "+name);
foreach(var name in new[]{"deposit_items","withdraw_items"}){
    foreach(var count in new[]{1,9999}){ToolCatalog.ValidateArguments(name,new(){["chest"]="工作箱",["itemId"]="(O)388",["count"]=count});checks++;}
    foreach(var count in new[]{0,10000})Reject(()=>ToolCatalog.ValidateArguments(name,new(){["chest"]="工作箱",["itemId"]="(O)388",["count"]=count}),"transfer quantity outside the permission range: "+name);
}
var waitSchema=tools.First(t=>t!["function"]!["name"]!.ToString()=="wait_for_change")!["function"]!["parameters"]!;
foreach(var seconds in new[]{1,120}){Validate(waitSchema,new JsonObject{["seconds"]=seconds,["reason"]="waiting for native state"});checks++;}
foreach(var seconds in new[]{0,121})Reject(()=>Validate(waitSchema,new JsonObject{["seconds"]=seconds,["reason"]="invalid duration"}),"wait duration outside the runtime range");
var go=new JsonObject{["location"]="Farm",["x"]=1,["y"]=1};ToolCatalog.ValidateArguments("go_to",go);checks++;
var badGo=(JsonObject)go.DeepClone();badGo["pickupRadius"]=9;Reject(()=>ToolCatalog.ValidateArguments("go_to",badGo),"pickup radius limit ignored");
var water=new JsonObject{["location"]="Farm",["x"]=1,["y"]=1,["width"]=32,["height"]=32};ToolCatalog.ValidateArguments("water_area",water);checks++;
water["width"]=33;Reject(()=>ToolCatalog.ValidateArguments("water_area",water),"area exceeds native 32 tile limit");
var state=new GameObservation{WorldReady=true,IsHost=false,Location="Farm"};
var chest=new ChestBinding("工作箱","bound","Farm",1,2,new[]{"(O)388"},Array.Empty<string>());
var deposit=new JsonObject{["chest"]="工作箱",["itemId"]="(O)388",["count"]=1};
Check(PermissionPolicy.Authorize("deposit_items",deposit,new[]{chest},state)["chestId"]!.ToString()=="bound","storage binding lost");
deposit["count"]=0;Reject(()=>PermissionPolicy.Authorize("deposit_items",deposit,new[]{chest},state),"invalid transfer quantity accepted");
deposit["count"]=1;deposit["chestId"]="forged";Reject(()=>PermissionPolicy.Authorize("deposit_items",deposit,new[]{chest},state),"forged internal field accepted");
Reject(()=>ToolCatalog.ValidateArguments("end_day",new JsonObject{["storageRules"]=new JsonArray(new JsonObject()),["shippingItems"]=new JsonArray(),["sleep"]=false}),"malformed nested storage rules accepted");
var report=new{checks,primitiveCount=primitives.Count,mutationCount=covered.Count,parentCount=groups.Length,tools=primitives.Select(t=>new{name=t!["function"]!["name"]!.ToString(),schema="passed",gameplay="requires_native_conditions_and_live_receipts"})};
var output=Path.GetFullPath(args.FirstOrDefault()??".local/tool-audit/contracts.json");Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} contract checks; {primitives.Count} tools, {covered.Count} mutations, {groups.Length} parents. Gameplay outcomes are recorded separately.");
