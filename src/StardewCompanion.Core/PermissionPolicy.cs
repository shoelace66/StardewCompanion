using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public static class PermissionPolicy
{
    public static JsonObject Authorize(string skill,JsonObject args,IReadOnlyList<ChestBinding> chests,GameObservation state)
    {
        if(skill=="mine_travel")throw new InvalidOperationException("skill_disabled:mine_travel");
        ToolCatalog.ValidateArguments(skill,args);
        if(BusinessCapabilities.Find(skill) is {} business){
            if(!ControlModePolicy.CanControl(state))throw new InvalidOperationException("capability_not_available:"+skill);
            using var doc=System.Text.Json.JsonDocument.Parse(args.ToJsonString());ContractValidation.Validate(business,doc.RootElement);
            var bound=(JsonObject)args.DeepClone();bound["_bindings"]=System.Text.Json.JsonSerializer.SerializeToNode(chests);return bound;
        }
        if(!ToolCatalog.Actions.Contains(skill))throw new InvalidOperationException("未注册的技能");
        var copy=(JsonObject)args.DeepClone();
        if(skill=="go_to"&&copy["name"]!=null)copy["_bindings"]=System.Text.Json.JsonSerializer.SerializeToNode(chests);
        if(skill is "deposit_items" or "withdraw_items")
        {
            var name=copy["chest"]?.GetValue<string>();var itemName=copy["itemName"]?.GetValue<string>();var id=copy["itemId"]?.GetValue<string>();var count=copy["count"]?.GetValue<int>()??0;
            if(string.IsNullOrWhiteSpace(id)&&!string.IsNullOrWhiteSpace(itemName)){
                var world=System.Text.Json.Nodes.JsonNode.Parse(state.WorldView);var itemMatches=(world?["inventory"] as System.Text.Json.Nodes.JsonArray??new())
                    .OfType<System.Text.Json.Nodes.JsonObject>().Where(i=>string.Equals(i["name"]?.ToString(),itemName,StringComparison.Ordinal)).Select(i=>i["id"]?.ToString()).Where(v=>!string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToArray();
                if(itemMatches.Length!=1)throw new InvalidOperationException(itemMatches.Length==0?"inventory_item_name_not_found":"inventory_item_name_ambiguous");
                id=itemMatches[0];copy["itemId"]=id;
            }
            var matches=chests.Where(c=>c.Name==name||c.Id==name).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException((matches.Length==0?"chest_not_authorized:":"authorized_chest_ambiguous:")+name+";authorizedNames="+string.Join(",",chests.Select(c=>c.Name)));
            var chest=matches[0];
            if(id==null||!chest.AllowedIds.Contains(id)||chest.ProtectedIds.Contains(id)||id.StartsWith("(T)")||id.StartsWith("(W)")||id is "(O)434" or "(O)326"||count is <1 or >9999)throw new InvalidOperationException("物品不在允许清单或受到保护");
            copy.Remove("chest");copy["chestId"]=chest.Id;copy["location"]=chest.Location;copy["x"]=chest.X;copy["y"]=chest.Y;copy["allowedIds"]=new JsonArray(chest.AllowedIds.Except(chest.ProtectedIds).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray());
        }
        if(skill.EndsWith("_area"))
        {
            foreach(var key in new[]{"x","y","width","height"})if(copy[key] is not JsonValue)throw new InvalidOperationException("区域参数缺失");
            if(copy["width"]!.GetValue<int>() is <1 or >120||copy["height"]!.GetValue<int>() is <1 or >120)throw new InvalidOperationException("区域尺寸超出限制");
        }
        return copy;
    }
}
