using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private JsonArray ProjectRelevantMemories(string query)
    {
        var tokens=query.Split(' ','，','。',':').Where(t=>t.Length>1).ToArray();
        var entries=_memory.List(_scope).Where(m=>m.State is not ("invalid" or "cancelled" or "user_confirmed_complete")&&m.Kind is "routine" or "project" or "agreement")
            .GroupBy(m=>m.Kind).SelectMany(g=>g.OrderByDescending(m=>tokens.Count(t=>m.Text.Contains(t,StringComparison.OrdinalIgnoreCase))).Take(4));
        return new JsonArray(entries.Select(m=>(JsonNode)new JsonObject{
            ["id"]=m.Id,["kind"]=m.Kind,["state"]=m.State,["source"]=m.Source,
            ["text"]=m.Kind=="project"?JsonValue.Create(m.Display):m.Kind=="agreement"?JsonValue.Create(m.Text):DecisionContextCompression.Decode(JsonValue.Create(m.Text)),
            ["historyReference"]="read_context_history(source=memory,id="+m.Id+",offset=0,limit=4)"
        }).ToArray());
    }
    private JsonArray ProjectActiveProjects()
    {
        var projects=_memory.Projects(_scope);
        return new JsonArray(projects.Select(p=>(JsonNode)new JsonObject{
            ["id"]=p.Id,["goal"]=p.Goal,["nextStep"]=p.NextStep,["status"]=p.Status,
            ["remaining"]=JsonSerializer.SerializeToNode(p.Remaining),["lastCheckedSession"]=p.LastCheckedSession,
            ["knowledge"]=p.LastCheckedSession==_session?"current_session_receipts":"requires_recheck",
            ["verifiedResults"]=new JsonArray(p.VerifiedResults.TakeLast(4).Select(r=>DecisionContextCompression.Decode(JsonValue.Create(r))).ToArray()),
            ["verifiedResultCount"]=p.VerifiedResults.Length,["unverifiedResultCount"]=p.UnverifiedResults.Length,
            ["historyReference"]="read_context_history(source=memory,id="+p.Id+",offset=0,limit=4)"
        }).ToArray());
    }

    private JsonArray ProjectBlockerConstraints()
    {
        var blockers=AwarenessSnapshot()["actionFeedback"]?["blockers"] as JsonArray??new();
        return new JsonArray(blockers.OfType<JsonObject>().Where(b=>IsShopDayClosure(b["reason"]?.ToString())||b["reason"]?.ToString().Contains("inventory_full")==true)
            .Select(b=>(JsonNode)new JsonObject{
                ["kind"]=b["reason"]!.ToString().Contains("inventory_full")?"inventory_capacity_blocked":b["reason"]!.ToString(),
                ["skill"]=b["skill"]?.DeepClone(),["location"]=b["location"]?.DeepClone(),["target"]=b["target"]?.DeepClone(),
                ["blockedDay"]=b["blockedDay"]?.DeepClone(),["source"]="native_execution_failure",
                ["releaseCondition"]=b["reason"]!.ToString().Contains("inventory_full")?"free_slot_or_target_stack_can_accept":"day_or_native_shop_access_changed_requires_recheck"
            }).ToArray());
    }

    private static JsonObject FarmingSummary(JsonObject world,GameObservation state)
    {
        var soil=world["soil"] as JsonArray;
        var inventory=world["inventory"] as JsonArray;
        var empty=soil?.OfType<JsonObject>().Count(t=>t["crop"]==null&&t["dead"]?.GetValue<bool>()!=true);
        var seedMetadata=inventory!=null&&inventory.OfType<JsonObject>().Where(i=>(i["count"]?.GetValue<int>()??0)>0).All(i=>i.ContainsKey("isSeed"));
        int? seeds=seedMetadata?inventory!.OfType<JsonObject>().Where(i=>i["isSeed"]?.GetValue<bool>()==true).Sum(i=>i["count"]?.GetValue<int>()??0):null;
        return new JsonObject{
            ["location"]=state.Location,["observedRevision"]=state.Revision,["scope"]="current_location_observed_tiles",
            ["harvestableCount"]=soil?.OfType<JsonObject>().Count(t=>t["ready"]?.GetValue<bool>()==true&&t["dead"]?.GetValue<bool>()!=true),
            ["waterableCount"]=soil?.OfType<JsonObject>().Count(t=>t["needsWater"]?.GetValue<bool>()==true&&t["dead"]?.GetValue<bool>()!=true),
            ["emptyPlotCount"]=empty,["availableSeedCount"]=seeds,
            ["nearbyRefillTiles"]=world["nearbyRefillTiles"]?.DeepClone(),
            ["nearbyRefillSourceObserved"]=world["nearbyRefillTiles"] is JsonArray refill?JsonValue.Create(refill.Count>0):null,
            ["refillMeaning"]="native refill tile within Manhattan distance 5; reachability still requires execution verification",
            ["seedCoverageRatio"]=empty>0&&seeds.HasValue?(double)seeds.Value/empty.Value:null,
            ["seedCoverageMeaning"]="seed_category_count_only; season, crop type and planting rules require verification; missing seed metadata is unknown"
        };
    }
}
