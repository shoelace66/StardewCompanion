using System.Text.Json;
using System.Text.Json.Nodes;
using StardewCompanion.Core;

internal static class PlanningFixture
{
    internal static void Seed(CompanionRuntime runtime,string? objective=null)
    {
        var state=runtime.Observation!;
        var world=JsonNode.Parse(state.WorldView)!.AsObject();
        var facts=world["decisionFacts"] as JsonObject;
        if(facts==null)world["decisionFacts"]=facts=new JsonObject();
        var calendar=facts["calendar"] as JsonObject;
        if(calendar==null)facts["calendar"]=calendar=new JsonObject();
        calendar["season"]??=JsonValue.Create("spring");
        state.WorldView=world.ToJsonString();
        if(objective!=null)
            runtime.Memory.Set(runtime.Scope,"player_goal",JsonSerializer.Serialize(new{status="active",objective,version=1}));
        runtime.Memory.Set(runtime.Scope,"season_plan",JsonSerializer.Serialize(new{
            year=state.GameDay/112+1,season=calendar["season"]!.GetValue<string>(),
            goalVersion=runtime.PlayerGoal["version"]!.GetValue<int>(),goal=runtime.PlayerGoal["objective"]!.GetValue<string>(),
            constraints=new[]{"preserve protected items"},successCriteria=new[]{"verify native receipts"},
            milestones=new[]{"complete the fixture's work"},resourceStrategy=new[]{"use observed resources"},progress=Array.Empty<string>()
        }));
    }
}
